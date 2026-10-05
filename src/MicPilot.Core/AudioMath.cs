namespace MicPilot.Core;

public static class AudioMath
{
    public static float Db(float linear) => 20 * MathF.Log10(MathF.Max(1e-6f, MathF.Abs(linear)));
    public static float Gain(float db) => MathF.Pow(10, db / 20);
    public static float Coefficient(float milliseconds, int rate = 48000) => MathF.Exp(-1 / (MathF.Max(1, milliseconds) * .001f * rate));
}

public sealed class Biquad
{
    private double _b0 = 1, _b1, _b2, _a1, _a2, _z1, _z2;
    public float Process(float sample)
    {
        double result = _b0 * sample + _z1;
        _z1 = _b1 * sample - _a1 * result + _z2;
        _z2 = _b2 * sample - _a2 * result;
        if (Math.Abs(_z1) < 1e-25) _z1 = 0;
        if (Math.Abs(_z2) < 1e-25) _z2 = 0;
        return (float)result;
    }
    public void SetHighPass(double hz, double q = .707, int rate = 48000) => Set(hz, q, 0, rate, 0);
    public void SetLowPass(double hz, double q = .707, int rate = 48000) => Set(hz, q, 0, rate, 1);
    public void SetPeak(double hz, double db, double q = .8, int rate = 48000) => Set(hz, q, db, rate, 2);
    private void Set(double hz, double q, double db, int rate, int kind)
    {
        var w = 2 * Math.PI * hz / rate; var c = Math.Cos(w); var alpha = Math.Sin(w) / (2 * q);
        var gain = Math.Pow(10, db / 40); double a0, a1, a2, b0, b1, b2;
        if (kind == 0) { b0 = (1 + c) / 2; b1 = -(1 + c); b2 = b0; a0 = 1 + alpha; a1 = -2 * c; a2 = 1 - alpha; }
        else if (kind == 1) { b0 = (1 - c) / 2; b1 = 1 - c; b2 = b0; a0 = 1 + alpha; a1 = -2 * c; a2 = 1 - alpha; }
        else { b0 = 1 + alpha * gain; b1 = -2 * c; b2 = 1 - alpha * gain; a0 = 1 + alpha / gain; a1 = -2 * c; a2 = 1 - alpha / gain; }
        _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
    }
}
