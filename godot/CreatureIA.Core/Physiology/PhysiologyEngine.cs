namespace CreatureIA.Core.Physiology;

public sealed class PhysiologyEngine
{
    public PhysiologyResult Step(
        PhysiologyState state,
        BodyTraits traits,
        PhysiologyInput input,
        double deltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(traits);
        ArgumentNullException.ThrowIfNull(input);
        traits.Validate();
        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        if (!state.IsAlive)
            return new PhysiologyResult(state, 0, 0, 0, false);

        ValidateState(state);
        // Sleep persists until a wake request or pain; previously it could never end, so one nap
        // halved metabolism for the rest of the creature's life.
        var effectiveInput = input with { Sleeping = input.Sleeping || (state.IsSleeping && !input.WakeRequested) };
        var metabolism = Metabolism.Calculate(traits, state, effectiveInput, deltaSeconds);
        var temperature = Thermoregulation.Calculate(traits, state, effectiveInput, deltaSeconds);

        var energy = Math.Clamp(state.Energy + Math.Max(0, input.EnergyIntake)
            - metabolism.EnergyCost - temperature.ShiverEnergyCost, 0, 1);
        var water = Math.Clamp(state.Water + Math.Max(0, input.WaterIntake) - metabolism.WaterCost, 0, 1);

        var canHeal = energy > 0.3;
        var missingIntegrity = (1 - state.HeadIntegrity) + (1 - state.TorsoIntegrity) + (1 - state.LegsIntegrity);
        var healPerZone = canHeal ? deltaSeconds * traits.HealingRate : 0;
        var healingCost = canHeal ? Math.Min(energy, deltaSeconds * traits.HealingEnergyCost * missingIntegrity) : 0;
        energy = Math.Max(0, energy - healingCost);

        var damageState = state with
        {
            Energy = energy,
            Water = water,
            BodyTemperature = temperature.Temperature,
            HeadIntegrity = Math.Clamp(state.HeadIntegrity + healPerZone, 0, 1),
            TorsoIntegrity = Math.Clamp(state.TorsoIntegrity + healPerZone, 0, 1),
            LegsIntegrity = Math.Clamp(state.LegsIntegrity + healPerZone, 0, 1),
            AgeSeconds = state.AgeSeconds + deltaSeconds
        };
        var damage = DamageModel.Apply(traits, damageState, effectiveInput, temperature.ThermalDamage, deltaSeconds);
        var died = damage.DeathCause != DeathCause.None;
        var next = damageState with
        {
            HeadIntegrity = damage.Head,
            TorsoIntegrity = damage.Torso,
            LegsIntegrity = damage.Legs,
            Pain = damage.Pain,
            Fatigue = metabolism.Fatigue,
            IsSleeping = effectiveInput.Sleeping && damage.Pain <= state.Pain,
            IsAlive = !died,
            DeathCause = damage.DeathCause
        };

        return new PhysiologyResult(next, metabolism.EnergyCost + temperature.ShiverEnergyCost, metabolism.WaterCost, healingCost, died);
    }

    private static void ValidateState(PhysiologyState state)
    {
        var values = new[] { state.Energy, state.Water, state.HeadIntegrity, state.TorsoIntegrity, state.LegsIntegrity, state.Fatigue, state.Pain, state.Libido };
        if (values.Any(value => !double.IsFinite(value) || value is < 0 or > 1))
            throw new ArgumentOutOfRangeException(nameof(state), "Normalized physiology values must be finite and inside [0,1].");
        if (!double.IsFinite(state.BodyTemperature) || !double.IsFinite(state.AgeSeconds) || state.AgeSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(state));
    }
}
