using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MicPilot.Core;
using MicPilot.App.Services;
using NAudio.Wave;
using NAudio.CoreAudioApi;

var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
void Throws(Action action, string name) { try { action(); } catch { Check(true, name); return; } throw new Exception("FAIL: expected exception: " + name); }
float[] Signal(int seconds, float amplitude = .1f, float frequency = 160) => Enumerable.Range(0, seconds * 48000).Select(i => amplitude * MathF.Sin(2 * MathF.PI * frequency * i / 48000)).ToArray();
float[] Process(float[] samples, MicSettings settings, bool bypass = false)
{
    using var p = new MicProcessor(settings, new DelayedPassThrough()); p.SetBypass(bypass);
    var result = new float[samples.Length]; var input = new float[480]; var output = new float[480];
    for (var offset = 0; offset < samples.Length; offset += 480) { Array.Copy(samples, offset, input, 0, 480); p.ProcessFrame(input, output); Array.Copy(output, 0, result, offset, 480); }
    return result;
}
var broken = new MicSettings { InputGainDb = float.NaN, NoiseStrength = float.PositiveInfinity, HighPassHz = -50, GateThresholdDb = 10, CompressorRatio = 500, OutputGainDb = -100, LimiterCeilingDb = 10 }.Sanitize();
Check(float.IsFinite(broken.InputGainDb) && float.IsFinite(broken.NoiseStrength), "Nonfinite settings sanitized");
Check(broken.HighPassHz == 20 && broken.GateThresholdDb == -20 && broken.CompressorRatio == 6, "Unsafe parameter values bounded");
Check(broken.OutputGainDb == -18 && broken.LimiterCeilingDb == -.3f, "Output parameters bounded");
foreach (var profile in MicProfile.BuiltIns) Check(profile.Settings == profile.Settings.Sanitize(), "Preset in range: " + profile.Name);
Check(MicProfile.BuiltIns[0].ToString() == "Естественный", "Profile selector shows name rather than record dump");
using (var p = new MicProcessor(new(), new DelayedPassThrough()))
{
    Throws(() => p.ProcessFrame(new float[12], new float[480]), "Reject incorrect frame size");
    var noise = new float[480]; var result = new float[480]; noise[2] = float.NaN; noise[3] = float.PositiveInfinity; p.ProcessFrame(noise, result);
    Check(result.All(float.IsFinite), "Nonfinite input cannot escape pipeline");
}
var zero = Process(new float[48000], new()); Check(zero.All(s => s == 0), "Silence remains silence");
var hot = Process(Signal(2, 3), new() { InputGainDb = 18, OutputGainDb = 12, CompressorEnabled = false, GateEnabled = false });
Check(hot.All(float.IsFinite) && hot.Max(Math.Abs) <= AudioMath.Gain(-1) + .00001, "Limiter bounds extreme input");
var low = Signal(2, .1f, 35); var hp = Process(low, MicSettings.Neutral with { HighPassHz = 150 }); var plain = Process(low, MicSettings.Neutral);
Check(hp.Skip(48000).Sum(s => s * s) < plain.Skip(48000).Sum(s => s * s) * .1, "High-pass reduces low-frequency rumble");
var regular = Signal(2); var bypassed = Process(regular, new() { InputGainDb = 6 }, true);
var maxError = Enumerable.Range(10000, regular.Length - 10000).Max(i => Math.Abs(bypassed[i] - regular[i - MicProcessor.LatencySamples]));
Check(maxError < .001, "Bypass preserves source with stable latency");
var impulse = new float[48000]; impulse[12000] = .2f; var delayed = Process(impulse, MicSettings.Neutral, true);
Check(Array.IndexOf(delayed, delayed.Max()) == 12000 + MicProcessor.LatencySamples, "25 ms pipeline latency verified on impulse");
var blendDry = Process(regular, MicSettings.Neutral);
var blendWet = Process(regular, MicSettings.Neutral with { NoiseEnabled = true, NoiseStrength = .5f });
Check(blendDry.Zip(blendWet).All(pair => Math.Abs(pair.First - pair.Second) < .00001), "Wet/dry paths aligned at 50% mix");
var quiet = Process(Signal(3, .0003f), MicSettings.Neutral with { GateEnabled = true, GateThresholdDb = -48, GateFloorDb = -24 });
Check(quiet.Skip(96000).Max(Math.Abs) < .0001, "Soft expander attenuates quiet background");
var loud = Process(Signal(3, .6f), MicSettings.Neutral with { CompressorEnabled = true, CompressorThresholdDb = -24, CompressorRatio = 4 });
Check(loud.Skip(48000).Max(Math.Abs) < .25, "Compressor reduces loud speech-like input");
var calibrationSpeech = Signal(12, .08f); var room = Signal(2, .0008f, 1000);
var fit = CalibrationAnalyzer.Analyze(calibrationSpeech, room, Signal(2, .005f, 2500));
Check(fit.HasNoiseSample && fit.Settings == fit.Settings.Sanitize(), "Separate room calibration yields bounded settings");
Check(fit.Findings.Any(t => t.Contains("клавиатуры")), "Keyboard sample acknowledged without perfect-removal claim");
var continuous = CalibrationAnalyzer.Analyze(calibrationSpeech);
Check(continuous.Settings.GateThresholdDb == -55 && continuous.Findings.Any(t => t.Contains("нет надёжных пауз")), "Continuous file gives conservative proposal, not invented room-noise estimate");
Throws(() => CalibrationAnalyzer.Analyze(new float[48000]), "Reject short calibration");
Throws(() => CalibrationAnalyzer.Analyze(new float[48000 * 10]), "Reject silence calibration");
calibrationSpeech[200] = float.NaN; Throws(() => CalibrationAnalyzer.Analyze(calibrationSpeech), "Reject corrupted calibration");
var clipped = CalibrationAnalyzer.Analyze(Signal(10, 1.2f), room); Check(clipped.ClippedPercent > 0 && clipped.Findings.Any(t => t.Contains("Перегруз")), "Clipped source produces hardware warning");
var taskDirectory = Path.Combine(Path.GetTempPath(), "micpilot-selftest-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(taskDirectory);
var store = new ProfileStore(taskDirectory); store.Save(new(null, null, "Тест", new(), [])); Check(store.Load().ProfileName == "Тест", "Atomic settings round-trip");
var profilePath = Path.Combine(taskDirectory, "profile.json"); ProfileStore.Export(profilePath, new("Проверка", "Локальный профиль", new())); Check(ProfileStore.Import(profilePath).Name == "Проверка", "Profile import/export round-trip");
var corruptPath = Path.Combine(taskDirectory, "settings.json"); File.WriteAllText(corruptPath, "{bad-json"); var corruptStore = new ProfileStore(taskDirectory); corruptStore.Load(); corruptStore.Save(new(null, null, "Тест", new(), [])); Check(File.ReadAllText(corruptPath) == "{bad-json", "Invalid user settings not silently overwritten");
foreach (var invalid in new[] { "{}", "{\"ProfileName\":null,\"Settings\":{},\"Profiles\":[]}", "{\"ProfileName\":\"Test\",\"Settings\":{},\"Profiles\":[null]}" })
{
    File.WriteAllText(corruptPath, invalid); var invalidStore = new ProfileStore(taskDirectory); var state = invalidStore.Load();
    Check(invalidStore.Warning.Length > 0 && state.Settings is not null && File.ReadAllText(corruptPath) == invalid, "Invalid JSON schema safely preserved");
}
var dry = Signal(1, .1f); var wet = Signal(1, .4f); AudioFiles.MatchLoudness(dry, wet); Check(Math.Abs(dry.Sum(v => v * v) - wet.Sum(v => v * v)) < .01, "A/B loudness matched without boosting peaks");
foreach (var bits in new[] { 16, 24, 32 })
{
    var nativeFormat = new WaveFormat(48000, bits, 2);
    var adapter = AudioOutputAdapter.ForDevice(new TestSignalProvider(), nativeFormat);
    var bytes = new byte[nativeFormat.BlockAlign * 64]; var read = adapter.Read(bytes);
    Check(read == bytes.Length && adapter.WaveFormat.Equals(nativeFormat), $"Exact WASAPI PCM {bits}-bit device format");
}
var surroundFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 8);
var surroundOutput = AudioOutputAdapter.ForDevice(new TestSignalProvider(), surroundFormat);
var surroundBytes = new byte[surroundFormat.BlockAlign * 64]; surroundOutput.Read(surroundBytes);
var surround = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(surroundBytes).ToArray();
Check(Enumerable.Range(0, 64).All(frame => surround[frame * 8] == .25f && surround[frame * 8 + 1] == .25f && surround.AsSpan(frame * 8 + 2, 6).ToArray().All(v => v == 0)), "7.1 headset: front channels only, no voice in LFE/surround");
// Unique test directory contains only files created above; no recursive removal.
foreach (var path in Directory.EnumerateFiles(taskDirectory)) File.Delete(path); Directory.Delete(taskDirectory);
if (RnNoise.IsAvailable)
{
    using (var native = new RnNoise())
    {
        var pulse = new float[480]; var nativeOutput = new float[480]; var rendered = new float[48000];
        for (var frame = 0; frame < 100; frame++)
        {
            Array.Clear(pulse); if (frame == 50) pulse[125] = .8f;
            native.Process(pulse, nativeOutput); nativeOutput.CopyTo(rendered, frame * 480);
        }
        var peakIndex = Enumerable.Range(0, rendered.Length).MaxBy(i => Math.Abs(rendered[i]));
        Console.WriteLine($"Native RNNoise impulse: peak at {peakIndex}, expected {50 * 480 + 125 + MicProcessor.DenoiserDelaySamples}");
        Check(rendered.Max(Math.Abs) > .00001 && Math.Abs(peakIndex - (50 * 480 + 125 + MicProcessor.DenoiserDelaySamples)) <= 1, "Pinned RNNoise native delay measured: 20 ms");
    }
    using var processor = new MicProcessor(new(), new RnNoise());
    var input = new float[480]; var output = new float[480]; var random = new Random(42); var timings = new List<double>();
    for (var frame = 0; frame < 500; frame++)
    {
        for (var i = 0; i < 480; i++) { var t = (frame * 480 + i) / 48000f; input[i] = .08f * MathF.Sin(2 * MathF.PI * 160 * t) + .01f * (float)(random.NextDouble() * 2 - 1); }
        var start = Stopwatch.GetTimestamp(); processor.ProcessFrame(input, output); var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (frame >= 30) timings.Add(elapsed);
        if (!output.All(float.IsFinite) || output.Max(Math.Abs) > AudioMath.Gain(-1) + .0001) throw new Exception("Invalid native audio at frame " + frame);
    }
    Check(true, "500 native audio frames: finite output within limiter ceiling");
    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    for (var frame = 0; frame < 1000; frame++) processor.ProcessFrame(input, output);
    Check(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore < 4096, "Steady DSP path: no per-frame managed allocations in 1000 frames");
    var partialLength = AudioFiles.Process(Signal(1).Take(47017).ToArray(), new());
    Check(partialLength.Length == 47017 && partialLength.All(float.IsFinite), "File preview keeps exact sample length, including partial last frame");
    var report = new { syntheticInput = true, microphoneCaptured = false, frames = timings.Count, meanProcessingMs = timings.Average(), p95ProcessingMs = timings.Order().ElementAt((int)(timings.Count * .95)), maxProcessingMs = timings.Max(), pipelineLatencyMs = MicProcessor.LatencySamples * 1000 / MicProcessor.SampleRate };
    var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.tmp")); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "audio-benchmark.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Check(timings.Average() < 10, "RNNoise mean processing fits 10 ms audio block"); Console.WriteLine(JsonSerializer.Serialize(report));
}
else Console.WriteLine("SKIP: RNNoise DLL not available; native audio quality and processing budget not verified.");
if (args.Contains("--devices"))
{
    var inputInfo = AudioEngine.Inputs().FirstOrDefault(d => d.Name.Contains("PD200X", StringComparison.OrdinalIgnoreCase)) ?? throw new IOException("PD200X not found.");
    var outputInfo = AudioEngine.Outputs().FirstOrDefault() ?? throw new IOException("CABLE Input not found.");
    using var enumerator = new MMDeviceEnumerator(); using var inputDevice = enumerator.GetDevice(inputInfo.Id); using var outputDevice = enumerator.GetDevice(outputInfo.Id);
    using var recorder = new WasapiRecorderBuilder().WithDevice(inputDevice).WithSharedMode().WithEventSync().Build();
    using var player = new WasapiPlayerBuilder().WithDevice(outputDevice).WithSharedMode().WithEventSync().WithLatency(24).WithLowLatency().WithMmcssThreadPriority("Pro Audio").Build();
    player.Init(AudioOutputAdapter.ForDevice(new TestSignalProvider(0), player.DeviceMixFormat));
    Check(recorder.WaveFormat.Channels is 1 or 2, "Actual PD200X capture format supported (recording not started)");
    Check(player.DeviceMixFormat.Channels is >= 1 and <= 8, "Actual CABLE render session initialized (playback not started)");
    Console.WriteLine($"Device initialization only: {inputInfo.Name}: {recorder.WaveFormat}; {outputInfo.Name}: {player.DeviceMixFormat}");
}
Console.WriteLine($"PASS: {passed} assertions. No microphone capture, live output or OBS changes.");

sealed class TestSignalProvider(float value = .25f) : ISampleProvider
{
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
    public int Read(Span<float> buffer) { buffer.Fill(value); return buffer.Length; }
}
