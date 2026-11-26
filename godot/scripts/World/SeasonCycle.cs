using Godot;

namespace CreatureIA.Godot.World;

public partial class SeasonCycle : Node
{
    public const float YearDurationSeconds = DayNightCycle.DayDurationSeconds * 8f;
    // Start at spring (winter strength 0.5). Previously 0 = midnight / deep winter, which alone added -18 °C.
    [Export] public float YearProgress01 { get; set; } = 0.25f;
    public float WinterStrength => Mathf.Clamp((Mathf.Cos(YearProgress01 * Mathf.Tau) + 1f) * 0.5f, 0f, 1f);
    public float GrowthMultiplier => Mathf.Lerp(1f, 0.2f, WinterStrength);
    public float TemperatureOffsetCelsius => -10f * WinterStrength;

    // Physics step = simulated time: pauses with the lockstep wait, so cycles are deterministic.
    public override void _PhysicsProcess(double delta) =>
        YearProgress01 = Mathf.PosMod(YearProgress01 + (float)delta / YearDurationSeconds, 1f);
}
