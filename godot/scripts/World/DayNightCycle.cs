using Godot;

namespace CreatureIA.Godot.World;

public partial class DayNightCycle : Node
{
    public const float DayDurationSeconds = 600f;
    // Start at ~07:12, just after sunrise. Previously 0 = midnight / deep winter, which alone added -18 °C.
    [Export] public float TimeOfDay01 { get; set; } = 0.3f;
    public float LightFactor => Mathf.Clamp(Mathf.Sin(TimeOfDay01 * Mathf.Tau - Mathf.Pi / 2f) * 0.5f + 0.5f, 0f, 1f);
    public float VisionMultiplier => Mathf.Lerp(0.4f, 1f, LightFactor);
    public float TemperatureOffsetCelsius => Mathf.Lerp(-8f, 0f, LightFactor);

    // Physics step = simulated time: pauses with the lockstep wait, so cycles are deterministic.
    public override void _PhysicsProcess(double delta) =>
        TimeOfDay01 = Mathf.PosMod(TimeOfDay01 + (float)delta / DayDurationSeconds, 1f);
}
