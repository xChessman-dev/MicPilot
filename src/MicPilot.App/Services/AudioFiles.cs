using MicPilot.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicPilot.App.Services;

public static class AudioFiles
{
    public static float[] ReadMono(string path, int maxSeconds = 120, CancellationToken token = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 200 * 1024 * 1024) throw new InvalidDataException("Файл не найден или больше 200 МБ.");
        using var reader = new AudioFileReader(path);
        ISampleProvider provider = reader;
        if (provider.WaveFormat.Channels == 2) provider = new StereoToMonoSampleProvider(provider) { LeftVolume = .5f, RightVolume = .5f };
        if (provider.WaveFormat.Channels != 1) throw new InvalidDataException("Нужна моно- или стереозапись.");
        if (provider.WaveFormat.SampleRate != 48000) provider = new WdlResamplingSampleProvider(provider, 48000);
        var samples = new List<float>(); var buffer = new float[4800]; int n;
        while ((n = provider.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            if (samples.Count + n > maxSeconds * 48000) throw new InvalidDataException($"Файл длиннее {maxSeconds} секунд. Выберите короткий образец.");
            for (var i = 0; i < n; i++) { if (!float.IsFinite(buffer[i])) throw new InvalidDataException("Некорректное аудио."); samples.Add(buffer[i]); }
        }
        return samples.ToArray();
    }
    public static float[] Process(float[] input, MicSettings settings, CancellationToken token = default)
    {
        using var processor = new MicProcessor(settings, new RnNoise());
        var output = new float[input.Length + MicProcessor.LatencySamples + 480];
        var frameIn = new float[480]; var frameOut = new float[480];
        for (var position = 0; position < output.Length; position += 480)
        {
            token.ThrowIfCancellationRequested(); Array.Clear(frameIn);
            if (position < input.Length) Array.Copy(input, position, frameIn, 0, Math.Min(480, input.Length - position));
            processor.ProcessFrame(frameIn, frameOut);
            Array.Copy(frameOut, 0, output, position, Math.Min(480, output.Length - position));
        }
        return output.AsSpan(MicProcessor.LatencySamples, input.Length).ToArray();
    }
    public static void MatchLoudness(float[] dry, float[] wet)
    {
        var dryEnergy = dry.Sum(v => (double)v * v); var wetEnergy = wet.Sum(v => (double)v * v);
        if (dryEnergy < 1e-8 || wetEnergy < 1e-8) return;
        // Attenuate the louder version, never boost peaks into clipping.
        var ratio = Math.Sqrt(dryEnergy / wetEnergy);
        if (ratio < 1) for (var i = 0; i < wet.Length; i++) wet[i] *= (float)ratio;
        else for (var i = 0; i < dry.Length; i++) dry[i] /= (float)ratio;
    }
    public static void Save(string path, float[] samples)
    {
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));
        writer.WriteSamples(samples, 0, samples.Length);
    }
    public static async Task<float[]> Capture(string inputId, int seconds, IProgress<double>? progress, CancellationToken token)
    {
        using var enumerator = new MMDeviceEnumerator(); using var device = enumerator.GetDevice(inputId);
        using var recorder = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode().WithEventSync().Build();
        var buffer = new BufferedWaveProvider(recorder.WaveFormat, TimeSpan.FromSeconds(seconds + 3)) { DiscardOnBufferOverflow = false, ReadFully = false };
        var sync = new object(); var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        void OnData(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long position, long qpc)
        { lock (sync) { try { buffer.AddSamples(data); } catch (Exception e) { failure = e; } } }
        void OnStopped(object? sender, StoppedEventArgs e) { if (e.Exception is not null) stopped.TrySetException(e.Exception); else stopped.TrySetResult(); }
        recorder.DataAvailable += OnData; recorder.RecordingStopped += OnStopped;
        try
        {
            recorder.StartRecording(); var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < seconds)
            {
                await Task.Delay(100, token); if (stopped.Task.IsCompleted) { await stopped.Task; throw new IOException("Микрофон остановился до завершения записи."); }
                if (failure is not null) throw new IOException("Ошибка записи.", failure);
                progress?.Report(Math.Min(1, timer.Elapsed.TotalSeconds / seconds));
            }
            recorder.StopRecording(); await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3), token);
            ISampleProvider provider = buffer.ToSampleProvider();
            if (provider.WaveFormat.Channels == 2) provider = new StereoToMonoSampleProvider(provider) { LeftVolume = .5f, RightVolume = .5f };
            if (provider.WaveFormat.Channels != 1) throw new InvalidDataException("Поддерживается моно- или стереомикрофон.");
            if (provider.WaveFormat.SampleRate != 48000) provider = new WdlResamplingSampleProvider(provider, 48000);
            var samples = new List<float>(); var frame = new float[4800]; int n;
            while ((n = provider.Read(frame)) > 0) samples.AddRange(frame.Take(n));
            token.ThrowIfCancellationRequested();
            if (samples.Count < (seconds - 1) * 48000) throw new IOException("Микрофон передал недостаточно звука.");
            progress?.Report(1); return samples.Take(seconds * 48000).ToArray();
        }
        finally
        {
            recorder.DataAvailable -= OnData; recorder.RecordingStopped -= OnStopped;
            try { recorder.StopRecording(); } catch { }
            if (stopped.Task.IsFaulted) _ = stopped.Task.Exception;
        }
    }
}
