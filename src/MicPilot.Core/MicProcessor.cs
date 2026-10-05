namespace MicPilot.Core;

public interface IFrameDenoiser : IDisposable
{
    // Pinned RNNoise: 48 kHz mono, 480 samples, two-frame (20 ms) algorithmic delay.
    float Process(float[] input, float[] output);
}

public sealed class DelayedPassThrough : IFrameDenoiser
{
    private readonly float[] _previous = new float[960];
    private int _position;
    public float Process(float[] input, float[] output)
    {
        _previous.AsSpan(_position, 480).CopyTo(output);
        input.AsSpan().CopyTo(_previous.AsSpan(_position, 480));
        _position = (_position + 480) % 960; return 0;
    }
    public void Dispose() { }
}

public readonly record struct ProcessingMeters(float InputPeak, float OutputPeak, float InputRms, float OutputRms,
    float CompressionDb, float GateReductionDb, float DeEssReductionDb, float SpeechProbability, long ClippedInputSamples);

/// <summary>Stateful, allocation-free 10 ms processing; shared by live audio and file previews.</summary>
public sealed class MicProcessor : IDisposable
{
    public const int FrameSize = 480;
    public const int SampleRate = 48000;
    public const int DenoiserDelaySamples = FrameSize * 2;
    public const int LatencySamples = DenoiserDelaySamples + 240;
    private readonly IFrameDenoiser _denoiser;
    private MicSettings _pending;
    private MicSettings? _applied;
    private readonly Biquad _highPass = new(), _popLow = new(), _bass = new(), _mud = new(), _presence = new(), _air = new(), _sibilant = new();
    private readonly float[] _prepared = new float[FrameSize], _wet = new float[FrameSize], _raw = new float[FrameSize];
    private readonly float[] _previousPrepared = new float[DenoiserDelaySamples], _previousRaw = new float[DenoiserDelaySamples];
    private readonly float[] _limiterBuffer = new float[240];
    private int _limiterIndex, _dryPosition;
    private float _inputGain = 1, _outputGain = 1, _noiseMix, _compressGain = 1, _gateGain = 1, _deEssGain = 1, _bypassMix;
    private float _envelope, _highEnvelope, _popEnvelope, _limiterGain = 1, _limiterPeak;
    private int _gateHoldSamples;
    private long _clipped;
    private volatile bool _bypass;
    public ProcessingMeters Meters { get; private set; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    public MicProcessor(MicSettings settings, IFrameDenoiser denoiser)
    {
        _denoiser = denoiser; _pending = settings.Sanitize();
        _popLow.SetLowPass(150); _sibilant.SetHighPass(4500); Configure(_pending);
        _inputGain = AudioMath.Gain(_pending.InputGainDb); _outputGain = AudioMath.Gain(_pending.OutputGainDb); _noiseMix = _pending.NoiseEnabled ? _pending.NoiseStrength : 0;
    }
    public void SetSettings(MicSettings settings) => Volatile.Write(ref _pending, settings.Sanitize());
    public void SetBypass(bool bypass) => _bypass = bypass;
    public void ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.Length != FrameSize || output.Length != FrameSize) throw new ArgumentException("Exactly 480 mono samples are required.");
        var s = Volatile.Read(ref _pending); if (s != _applied) Configure(s);
        float inPeak = 0, outPeak = 0; double inEnergy = 0, outEnergy = 0;
        var inputTarget = AudioMath.Gain(s.InputGainDb); var outTarget = AudioMath.Gain(s.OutputGainDb);
        for (var i = 0; i < FrameSize; i++)
        {
            float raw = float.IsFinite(input[i]) ? Math.Clamp(input[i], -4, 4) : 0;
            _raw[i] = raw;
            if (Math.Abs(raw) >= .999f) _clipped++;
            inPeak = Math.Max(inPeak, Math.Abs(raw)); inEnergy += raw * raw;
            _inputGain += (inputTarget - _inputGain) * .001f;
            var sample = _highPass.Process(raw * _inputGain);
            var low = _popLow.Process(sample);
            _popEnvelope += (Math.Abs(low) - _popEnvelope) * .004f;
            // Dynamic low-frequency attenuation; it cannot repair a clipped air blast.
            var pop = Math.Clamp((_popEnvelope - .045f) / .12f, 0, 1) * s.PopStrength;
            _prepared[i] = sample - low * pop * .85f;
        }
        float speech = _denoiser.Process(_prepared, _wet);
        if (!float.IsFinite(speech)) speech = 0;
        var noiseTarget = s.NoiseEnabled ? s.NoiseStrength : 0;
        var attack = AudioMath.Coefficient(s.CompressorAttackMs); var release = AudioMath.Coefficient(s.CompressorReleaseMs);
        var gateRelease = AudioMath.Coefficient(s.GateReleaseMs); var ceiling = AudioMath.Gain(s.LimiterCeilingDb);
        for (var i = 0; i < FrameSize; i++)
        {
            _noiseMix += (noiseTarget - _noiseMix) * .001f;
            var dryIndex = _dryPosition + i;
            var sample = _previousPrepared[dryIndex] + (_wet[i] - _previousPrepared[dryIndex]) * _noiseMix;
            sample = _air.Process(_presence.Process(_mud.Process(_bass.Process(sample))));
            var absolute = Math.Abs(sample);
            _envelope = absolute > _envelope ? .992f * _envelope + .008f * absolute : .9996f * _envelope + .0004f * absolute;
            var envelopeDb = AudioMath.Db(_envelope);
            if (envelopeDb > s.GateThresholdDb + 3) _gateHoldSamples = 4800;
            else _gateHoldSamples = Math.Max(0, _gateHoldSamples - 1);
            // Soft 8 dB knee and hold avoid chattering and cut consonants in pauses.
            var gateOpen = Math.Clamp((envelopeDb - s.GateThresholdDb + 5) / 8, 0, 1);
            var gateTarget = !s.GateEnabled || _gateHoldSamples > 0 ? 1 : AudioMath.Gain(s.GateFloorDb * (1 - gateOpen));
            var gateCoef = gateTarget > _gateGain ? .995f : gateRelease;
            _gateGain = gateCoef * _gateGain + (1 - gateCoef) * gateTarget;
            var high = _sibilant.Process(sample);
            _highEnvelope += (Math.Abs(high) - _highEnvelope) * .003f;
            var deEssDb = -Math.Clamp(AudioMath.Db(_highEnvelope) - s.DeEssThresholdDb, 0, 12) * s.DeEssStrength;
            _deEssGain += (AudioMath.Gain(deEssDb) - _deEssGain) * .002f;
            sample = (sample - high * (1 - _deEssGain)) * _gateGain;
            var over = envelopeDb - s.CompressorThresholdDb;
            var knee = over <= -3 ? 0 : over >= 3 ? over : (over + 3) * (over + 3) / 12;
            var gainTarget = s.CompressorEnabled ? AudioMath.Gain(-knee * (1 - 1 / s.CompressorRatio)) : 1;
            var compCoef = gainTarget < _compressGain ? attack : release;
            _compressGain = compCoef * _compressGain + (1 - compCoef) * gainTarget;
            _outputGain += (outTarget - _outputGain) * .001f;
            sample *= _compressGain * _outputGain;
            _bypassMix += ((_bypass ? 1 : 0) - _bypassMix) * .004f;
            sample = sample * (1 - _bypassMix) + _previousRaw[dryIndex] * _bypassMix;
            // Five milliseconds of look-ahead; a peak hold covers the delayed sample.
            _limiterPeak = Math.Max(Math.Abs(sample), _limiterPeak * .9998f);
            var limitTarget = _limiterPeak > ceiling ? ceiling / _limiterPeak : 1;
            _limiterGain = limitTarget < _limiterGain ? limitTarget : _limiterGain + (limitTarget - _limiterGain) * .0005f;
            var delayed = _limiterBuffer[_limiterIndex]; _limiterBuffer[_limiterIndex] = sample;
            _limiterIndex = (_limiterIndex + 1) % _limiterBuffer.Length;
            var result = Math.Clamp(delayed * _limiterGain, -ceiling, ceiling);
            output[i] = float.IsFinite(result) ? result : 0;
            outPeak = Math.Max(outPeak, Math.Abs(output[i])); outEnergy += output[i] * output[i];
            _previousPrepared[dryIndex] = _prepared[i]; _previousRaw[dryIndex] = _raw[i];
        }
        _dryPosition = (_dryPosition + FrameSize) % DenoiserDelaySamples;
        Meters = new(inPeak, outPeak, (float)Math.Sqrt(inEnergy / FrameSize), (float)Math.Sqrt(outEnergy / FrameSize),
            -AudioMath.Db(_compressGain), -AudioMath.Db(_gateGain), -AudioMath.Db(_deEssGain), Math.Clamp(speech, 0, 1), _clipped);
    }
    private void Configure(MicSettings s)
    {
        _highPass.SetHighPass(s.HighPassHz); _bass.SetPeak(130, s.BassDb); _mud.SetPeak(350, s.MudDb);
        _presence.SetPeak(2800, s.PresenceDb); _air.SetPeak(8500, s.AirDb); _applied = s;
    }
    public void Dispose() => _denoiser.Dispose();
}
