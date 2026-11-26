namespace CreatureIA.Core.Physiology;

public readonly record struct TemperatureResult(double Temperature, double ShiverEnergyCost, double ThermalDamage);

public static class Thermoregulation
{
    public static TemperatureResult Calculate(
        BodyTraits traits,
        PhysiologyState state,
        PhysiologyInput input,
        double deltaSeconds)
    {
        if (!double.IsFinite(input.AmbientTemperature)) throw new ArgumentOutOfRangeException(nameof(input.AmbientTemperature));

        var exchange = (input.AmbientTemperature - state.BodyTemperature)
            * (1 - traits.Insulation)
            * traits.HeatExchangeRate;
        var shivering = state.BodyTemperature < traits.SetPointTemperature - 2 && state.Energy > 0
            ? traits.ShiverPower
            : 0;
        var temperature = state.BodyTemperature + deltaSeconds * (exchange + shivering);
        var shiverCost = shivering > 0 ? deltaSeconds * traits.ShiverEnergyCost : 0;
        var distanceOutsideVitalRange = temperature < traits.MinimumVitalTemperature
            ? traits.MinimumVitalTemperature - temperature
            : Math.Max(0, temperature - traits.MaximumVitalTemperature);
        var damage = deltaSeconds * traits.ThermalDamageRate * distanceOutsideVitalRange;
        return new TemperatureResult(temperature, shiverCost, damage);
    }
}
