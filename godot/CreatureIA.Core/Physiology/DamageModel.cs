namespace CreatureIA.Core.Physiology;

public readonly record struct DamageResult(
    double Head,
    double Torso,
    double Legs,
    double Pain,
    DeathCause DeathCause);

public static class DamageModel
{
    public static DamageResult Apply(
        BodyTraits traits,
        PhysiologyState state,
        PhysiologyInput input,
        double thermalDamage,
        double deltaSeconds)
    {
        var head = state.HeadIntegrity;
        var torso = state.TorsoIntegrity;
        var legs = state.LegsIntegrity;
        var recentDamage = 0.0;
        var lethalHint = DeathCause.None;

        foreach (var damageEvent in input.DamageEvents)
        {
            if (!double.IsFinite(damageEvent.Amount) || damageEvent.Amount < 0)
                throw new ArgumentOutOfRangeException(nameof(input.DamageEvents), "Damage must be finite and non-negative.");

            var effective = damageEvent.Amount * (1 - 0.6 * traits.Armor);
            recentDamage += effective;
            lethalHint = damageEvent.Cause == DeathCause.None ? lethalHint : damageEvent.Cause;
            switch (damageEvent.Zone)
            {
                case DamageZone.Head: head -= effective; break;
                case DamageZone.Torso: torso -= effective; break;
                case DamageZone.Legs: legs -= effective; break;
                default: throw new ArgumentOutOfRangeException(nameof(input.DamageEvents), "Unknown damage zone.");
            }
        }

        if (state.Energy <= 0)
        {
            var damage = deltaSeconds * traits.StarvationDamageRate;
            torso -= damage;
            recentDamage += damage;
        }

        if (state.Water <= 0)
        {
            var damage = deltaSeconds * traits.DehydrationDamageRate;
            torso -= damage;
            recentDamage += damage;
        }

        if (thermalDamage > 0)
        {
            torso -= thermalDamage;
            recentDamage += thermalDamage;
        }

        if (state.AgeSeconds > traits.MaximumAgeSeconds)
        {
            var damage = deltaSeconds * traits.SenescenceDamageRate
                * (1 + (state.AgeSeconds - traits.MaximumAgeSeconds) / traits.MaximumAgeSeconds);
            torso -= damage;
            recentDamage += damage;
        }

        var painDecay = Math.Exp(-deltaSeconds / 3.0);
        var pain = Math.Clamp(state.Pain * painDecay + recentDamage, 0, 1);
        var cause = DeathCause.None;
        if (head <= 0) cause = lethalHint is DeathCause.Fall or DeathCause.Bite ? lethalHint : DeathCause.HeadInjury;
        else if (torso <= 0)
        {
            cause = state.Water <= 0 ? DeathCause.Dehydration
                : state.Energy <= 0 ? DeathCause.Starvation
                : thermalDamage > 0 ? (state.BodyTemperature < traits.MinimumVitalTemperature ? DeathCause.Cold : DeathCause.Heat)
                : state.AgeSeconds > traits.MaximumAgeSeconds ? DeathCause.Age
                : lethalHint is DeathCause.Fall or DeathCause.Bite ? lethalHint
                : DeathCause.TorsoInjury;
        }

        return new DamageResult(
            Math.Clamp(head, 0, 1),
            Math.Clamp(torso, 0, 1),
            Math.Clamp(legs, 0, 1),
            pain,
            cause);
    }
}
