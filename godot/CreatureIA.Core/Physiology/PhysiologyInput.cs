namespace CreatureIA.Core.Physiology;

public readonly record struct DamageEvent(DamageZone Zone, double Amount, DeathCause Cause = DeathCause.None);

public sealed record PhysiologyInput
{
    public double AmbientTemperature { get; init; } = 20.0;
    public double Thrust { get; init; }
    public double Gait { get; init; }
    public double Turn { get; init; }
    public double EnergyIntake { get; init; }
    public double WaterIntake { get; init; }
    public bool Sleeping { get; init; }
    /// <summary>Ends an ongoing sleep (plan 4.3: sleep action &lt; 0). Pain also wakes.</summary>
    public bool WakeRequested { get; init; }
    public IReadOnlyList<DamageEvent> DamageEvents { get; init; } = Array.Empty<DamageEvent>();
}
