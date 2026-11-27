namespace CreatureIA.Core.Simulation;

/// <summary>Day/night and season cycles in simulated time (same formulas as the Godot nodes of etapa 1).</summary>
public sealed class SimClock
{
    public const float DayDurationSeconds = 600f;
    public const float YearDurationSeconds = DayDurationSeconds * 8f;

    /// <summary>Starts at ~07:12, just after sunrise.</summary>
    public float TimeOfDay01 { get; private set; } = 0.3f;
    /// <summary>Starts at spring (winter strength 0.5).</summary>
    public float YearProgress01 { get; private set; } = 0.25f;
    public double ElapsedSeconds { get; private set; }

    public float LightFactor => Math.Clamp(MathF.Sin(TimeOfDay01 * MathF.Tau - MathF.PI / 2f) * 0.5f + 0.5f, 0f, 1f);
    public float VisionMultiplier => Lerp(0.4f, 1f, LightFactor);
    public float WinterStrength => Math.Clamp((MathF.Cos(YearProgress01 * MathF.Tau) + 1f) * 0.5f, 0f, 1f);
    public float GrowthMultiplier => Lerp(1f, 0.2f, WinterStrength);
    public float TemperatureOffsetCelsius => Lerp(-8f, 0f, LightFactor) - 10f * WinterStrength;

    public void Advance(float dt)
    {
        ElapsedSeconds += dt;
        TimeOfDay01 = PositiveMod(TimeOfDay01 + dt / DayDurationSeconds);
        YearProgress01 = PositiveMod(YearProgress01 + dt / YearDurationSeconds);
    }

    internal static float Lerp(float from, float to, float weight) => from + (to - from) * weight;

    private static float PositiveMod(float value)
    {
        var result = value % 1f;
        return result < 0f ? result + 1f : result;
    }
}
