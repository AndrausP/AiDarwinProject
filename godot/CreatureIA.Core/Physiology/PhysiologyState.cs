namespace CreatureIA.Core.Physiology;

public sealed record PhysiologyState
{
    public double Energy { get; init; } = 1.0;
    public double Water { get; init; } = 1.0;
    public double BodyTemperature { get; init; } = 37.0;
    public double HeadIntegrity { get; init; } = 1.0;
    public double TorsoIntegrity { get; init; } = 1.0;
    public double LegsIntegrity { get; init; } = 1.0;
    public double Fatigue { get; init; }
    public double Pain { get; init; }
    public double Libido { get; init; }
    public double AgeSeconds { get; init; }
    public bool IsSleeping { get; init; }
    public bool IsAlive { get; init; } = true;
    public DeathCause DeathCause { get; init; } = DeathCause.None;
}
