using System.Diagnostics;
using MicPilot.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicPilot.App.Services;

public sealed record AudioDevice(string Id, string Name) { public override string ToString() => Name; }
public sealed record EngineSnapshot(ProcessingMeters Meters, double ProcessingMs, int QueueMs, int EstimatedLatencyMs, long BufferResets, long EmptyReads);

public sealed class ProcessingProvider : ISampleProvider, IDisposable
{
    private readonly ISampleProvider _source;
    private readonly float[] _input = new float[480], _output = new float[480];
    private int _position = 480;
    public MicProcessor Processor { get; }
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
    public double ProcessingMs { get; private set; }
    public ProcessingProvider(ISampleProvider source, MicSettings settings)
    {
        if (source.WaveFormat.SampleRate != 48000 || source.WaveFormat.Channels != 1) throw new ArgumentException("Processing requires mono 48 kHz.");
        _source = source; Processor = new MicProcessor(settings, new RnNoise());
    }
    public int Read(Span<float> buffer)
    {
        var count = buffer.Length;
        for (var written = 0; written < count;)
        {
            if (_position == 480)
            {
                var read = 0;
                while (read < 480) { var n = _source.Read(_input.AsSpan(read, 480 - read)); if (n == 0) break; read += n; }
                if (read < 480) Array.Clear(_input, read, 480 - read);
                var start = Stopwatch.GetTimestamp(); Processor.ProcessFrame(_input, _output);
                ProcessingMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds; _position = 0;
            }
            var ncopy = Math.Min(count - written, 480 - _position);
            _output.AsSpan(_position, ncopy).CopyTo(buffer[written..]); written += ncopy; _position += ncopy;
        }
        return count;
    }
    public void Dispose() => Processor.Dispose();
}

public sealed class AudioEngine : IDisposable
{
    private readonly object _sync = new();
    private WasapiRecorder? _recorder;
    private WasapiPlayer? _player;
    private BufferedWaveProvider? _buffer;
    private ProcessingProvider? _pipeline;
    private MicSettings _settings = new();
    private bool _bypass, _mute;
    private long _resets, _emptyReads;
    private int _deviceLatency;
    public event EventHandler<string>? Faulted;
    public bool IsRunning { get { lock (_sync) return _recorder is not null; } }
    public static AudioDevice[] Inputs() => Devices(DataFlow.Capture);
    public static AudioDevice[] Outputs() => Devices(DataFlow.Render).Where(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)).ToArray();
    private static AudioDevice[] Devices(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            using (device) result.Add(new(device.ID, device.FriendlyName));
        return result.ToArray();
    }
    public async Task StartAsync(string inputId, string outputId)
    {
        if (IsRunning) return;
        using var enumerator = new MMDeviceEnumerator();
        using var input = enumerator.GetDevice(inputId); using var output = enumerator.GetDevice(outputId);
        if (!output.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Выберите виртуальный CABLE Input, а не колонки: это защищает от акустической обратной связи.");
        if (input.FriendlyName.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Нельзя подавать виртуальный выход обратно на вход. Выберите физический микрофон.");
        WasapiRecorder? recorder = null; WasapiPlayer? player = null; ProcessingProvider? pipeline = null;
        try
        {
            recorder = new WasapiRecorderBuilder().WithDevice(input).WithSharedMode().WithEventSync().Build();
            player = new WasapiPlayerBuilder().WithDevice(output).WithSharedMode().WithEventSync().WithLatency(24).WithLowLatency().WithMmcssThreadPriority("Pro Audio").Build();
            var buffer = new BufferedWaveProvider(recorder.WaveFormat, TimeSpan.FromMilliseconds(180)) { ReadFully = true, DiscardOnBufferOverflow = true };
            ISampleProvider source = buffer.ToSampleProvider();
            if (source.WaveFormat.Channels == 2) source = new StereoToMonoSampleProvider(source) { LeftVolume = .5f, RightVolume = .5f };
            if (source.WaveFormat.Channels != 1) throw new InvalidOperationException("Поддерживается моно- или стереомикрофон.");
            if (source.WaveFormat.SampleRate != 48000) source = new WdlResamplingSampleProvider(source, 48000);
            pipeline = new ProcessingProvider(source, _settings); pipeline.Processor.SetBypass(_bypass);
            var muted = new MutableVolumeProvider(pipeline, () => _mute);
            player.Init(AudioOutputAdapter.ForDevice(muted, player.DeviceMixFormat));
            recorder.DataAvailable += OnData; recorder.RecordingStopped += OnStopped; player.PlaybackStopped += OnStopped;
            lock (_sync)
            {
                _recorder = recorder; _player = player; _buffer = buffer; _pipeline = pipeline; _resets = _emptyReads = 0;
                _deviceLatency = recorder.LatencyMilliseconds + player.LatencyMilliseconds;
            }
            recorder.StartRecording();
            await Task.Delay(35); // Initial prefill only; no accumulating artificial delay.
            player.Play();
        }
        catch { if (_recorder == recorder) Stop(); else { recorder?.Dispose(); player?.Dispose(); pipeline?.Dispose(); } throw; }
    }
    public void Apply(MicSettings settings) { lock (_sync) { _settings = settings.Sanitize(); _pipeline?.Processor.SetSettings(_settings); } }
    public void Bypass(bool value) { lock (_sync) { _bypass = value; _pipeline?.Processor.SetBypass(value); } }
    public void Mute(bool value) => _mute = value;
    public EngineSnapshot? Snapshot()
    {
        lock (_sync)
        {
            if (_pipeline is null || _buffer is null) return null;
            var queue = (int)(_buffer.BufferedBytes * 1000L / _buffer.WaveFormat.AverageBytesPerSecond);
            return new(_pipeline.Processor.Meters, _pipeline.ProcessingMs, queue, _deviceLatency + MicProcessor.LatencySamples * 1000 / MicProcessor.SampleRate + queue, _resets, _emptyReads);
        }
    }
    private void OnData(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long position, long qpc)
    {
        lock (_sync)
        {
            if (_buffer is null) return;
            if (_buffer.BufferedBytes + data.Length > _buffer.WaveFormat.AverageBytesPerSecond * .12) { _buffer.ClearBuffer(); _resets++; }
            if (_buffer.BufferedBytes == 0 && _pipeline is not null) _emptyReads++;
            _buffer.AddSamples(data);
        }
    }
    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) Faulted?.Invoke(this, "Аудиоустройство остановлено. Проверьте подключение и перезапустите обработку. " + e.Exception.Message);
    }
    public void Stop()
    {
        WasapiRecorder? recorder; WasapiPlayer? player; ProcessingProvider? pipeline;
        lock (_sync) { recorder = _recorder; player = _player; pipeline = _pipeline; _recorder = null; _player = null; _pipeline = null; _buffer = null; }
        if (recorder is not null) { recorder.DataAvailable -= OnData; recorder.RecordingStopped -= OnStopped; try { recorder.StopRecording(); } catch { } recorder.Dispose(); }
        if (player is not null) { player.PlaybackStopped -= OnStopped; try { player.Stop(); } catch { } player.Dispose(); }
        pipeline?.Dispose();
    }
    public void Dispose() => Stop();

    private sealed class MutableVolumeProvider(ISampleProvider source, Func<bool> mute) : ISampleProvider
    {
        private float _gain = 1;
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(Span<float> buffer)
        {
            var n = source.Read(buffer); var target = mute() ? 0 : 1;
            for (var i = 0; i < n; i++) { _gain += (target - _gain) * .01f; buffer[i] *= _gain; }
            return n;
        }
    }
}
