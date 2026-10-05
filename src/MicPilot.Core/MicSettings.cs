namespace MicPilot.Core;

public sealed record MicSettings
{
    public float InputGainDb { get; init; }
    public bool NoiseEnabled { get; init; } = true;
    public float NoiseStrength { get; init; } = .72f;
    public float HighPassHz { get; init; } = 80;
    public float PopStrength { get; init; } = .5f;
    public bool GateEnabled { get; init; } = true;
    public float GateThresholdDb { get; init; } = -48;
    public float GateFloorDb { get; init; } = -18;
    public float GateReleaseMs { get; init; } = 180;
    public float BassDb { get; init; }
    public float MudDb { get; init; } = -1;
    public float PresenceDb { get; init; } = 1;
    public float AirDb { get; init; }
    public float DeEssStrength { get; init; } = .25f;
    public float DeEssThresholdDb { get; init; } = -24;
    public bool CompressorEnabled { get; init; } = true;
    public float CompressorThresholdDb { get; init; } = -18;
    public float CompressorRatio { get; init; } = 2.5f;
    public float CompressorAttackMs { get; init; } = 8;
    public float CompressorReleaseMs { get; init; } = 160;
    public float OutputGainDb { get; init; } = 2;
    public float LimiterCeilingDb { get; init; } = -1;

    public MicSettings Sanitize() => this with
    {
        InputGainDb = Bound(InputGainDb, -18, 18, 0), NoiseStrength = Bound(NoiseStrength, 0, 1, .72f),
        HighPassHz = Bound(HighPassHz, 20, 180, 80), PopStrength = Bound(PopStrength, 0, 1, .5f),
        GateThresholdDb = Bound(GateThresholdDb, -70, -20, -48), GateFloorDb = Bound(GateFloorDb, -40, 0, -18),
        GateReleaseMs = Bound(GateReleaseMs, 60, 500, 180), BassDb = Bound(BassDb, -8, 8, 0),
        MudDb = Bound(MudDb, -8, 6, -1), PresenceDb = Bound(PresenceDb, -6, 6, 1), AirDb = Bound(AirDb, -6, 6, 0),
        DeEssStrength = Bound(DeEssStrength, 0, 1, .25f), DeEssThresholdDb = Bound(DeEssThresholdDb, -45, -10, -24),
        CompressorThresholdDb = Bound(CompressorThresholdDb, -36, -6, -18), CompressorRatio = Bound(CompressorRatio, 1, 6, 2.5f),
        CompressorAttackMs = Bound(CompressorAttackMs, 1, 40, 8), CompressorReleaseMs = Bound(CompressorReleaseMs, 50, 500, 160),
        OutputGainDb = Bound(OutputGainDb, -18, 12, 2), LimiterCeilingDb = Bound(LimiterCeilingDb, -6, -.3f, -1),
    };

    private static float Bound(float v, float min, float max, float fallback) => float.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
    public static MicSettings Neutral => new() { NoiseEnabled = false, HighPassHz = 20, PopStrength = 0, GateEnabled = false,
        BassDb = 0, MudDb = 0, PresenceDb = 0, AirDb = 0, DeEssStrength = 0, CompressorEnabled = false, OutputGainDb = 0 };
}

public sealed record MicProfile(string Name, string Description, MicSettings Settings)
{
    public override string ToString() => Name;
    public static MicProfile[] BuiltIns =>
    [
        new("Естественный", "Мягкая очистка · сохранить свой голос", new()),
        new("Игра и общение", "Больше подавления фона · ровная громкость", new() { NoiseStrength = .9f, GateFloorDb = -24, CompressorRatio = 3, PopStrength = .65f }),
        new("Запись и эфир", "Бережная обработка · открытые окончания", new() { NoiseStrength = .55f, GateFloorDb = -8, GateThresholdDb = -55, CompressorRatio = 2, OutputGainDb = 1 }),
    ];
}
