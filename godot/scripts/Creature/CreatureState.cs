using Godot;
using CreatureIA.Core.Physiology;

namespace CreatureIA.Godot.Creature;

public enum BodyZone : byte { Head, Torso, Legs }

public sealed class CreatureState
{
    public float Energy { get; set; } = 1f;
    public float Water { get; set; } = 1f;
    public float BodyTemperatureCelsius { get; set; } = 37f;
    public float HeadIntegrity { get; set; } = 1f;
    public float TorsoIntegrity { get; set; } = 1f;
    public float LegsIntegrity { get; set; } = 1f;
    public float Fatigue { get; set; }
    public float Pain { get; set; }
    public float Libido { get; set; }
    public float AgeSeconds { get; set; }
    public float MaximumAgeSeconds { get; set; } = 3600f;
    public bool Sleeping { get; set; }
    public bool IsAlive { get; private set; } = true;
    public DeathCause CauseOfDeath { get; private set; }
    public bool IsMature => AgeSeconds >= MaximumAgeSeconds * 0.2f;

    public void SetIntegrity(BodyZone zone, float value)
    {
        value = Mathf.Clamp(value, 0f, 1f);
        switch (zone)
        {
            case BodyZone.Head: HeadIntegrity = value; break;
            case BodyZone.Torso: TorsoIntegrity = value; break;
            case BodyZone.Legs: LegsIntegrity = value; break;
        }
    }

    public bool TryDie(DeathCause cause)
    {
        if (!IsAlive) return false;
        IsAlive = false;
        CauseOfDeath = cause;
        return true;
    }

    public void Synchronize(PhysiologyState state)
    {
        Energy = (float)state.Energy;
        Water = (float)state.Water;
        BodyTemperatureCelsius = (float)state.BodyTemperature;
        HeadIntegrity = (float)state.HeadIntegrity;
        TorsoIntegrity = (float)state.TorsoIntegrity;
        LegsIntegrity = (float)state.LegsIntegrity;
        Fatigue = (float)state.Fatigue;
        Pain = (float)state.Pain;
        Libido = (float)state.Libido;
        AgeSeconds = (float)state.AgeSeconds;
        MaximumAgeSeconds = Mathf.Max(MaximumAgeSeconds, AgeSeconds);
        Sleeping = state.IsSleeping;
        IsAlive = state.IsAlive;
        CauseOfDeath = state.DeathCause;
    }
}
