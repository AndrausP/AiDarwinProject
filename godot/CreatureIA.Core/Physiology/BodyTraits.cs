namespace CreatureIA.Core.Physiology;

public sealed record BodyTraits
{
    public double Size { get; init; } = 1.0;
    public double Armor { get; init; }
    public double Insulation { get; init; } = 0.5;
    public double BasalMetabolicRate { get; init; } = 0.002;
    public double MovementCost { get; init; } = 0.003;
    public double TurnCost { get; init; } = 0.001;
    public double BrainCost { get; init; } = 0.0005;
    public double BasalWaterLoss { get; init; } = 0.0015;
    public double SweatCost { get; init; } = 0.001;
    // Calibrated 2026-11-26: at 0.02 shivering could only hold T_ambient + 6 °C, so every biome was
    // lethal within ~100 s and random agents died of cold instead of hunger/thirst (G05). At 0.005 a
    // shivering body holds T_ambient + 24 °C: survives >= ~6 °C ambient, still dies in freezing cold.
    public double HeatExchangeRate { get; init; } = 0.005;
    public double SetPointTemperature { get; init; } = 37.0;
    public double ShiverPower { get; init; } = 0.06;
    public double ShiverEnergyCost { get; init; } = 0.002;
    public double FatigueBaseRate { get; init; } = 0.0004;
    public double FatigueEffortRate { get; init; } = 0.001;
    public double FatigueRecoveryRate { get; init; } = 0.004;
    public double HealingRate { get; init; } = 0.001;
    public double HealingEnergyCost { get; init; } = 0.0005;
    public double StarvationDamageRate { get; init; } = 0.01;
    public double DehydrationDamageRate { get; init; } = 0.015;
    public double ThermalDamageRate { get; init; } = 0.01;
    public double MinimumVitalTemperature { get; init; } = 30.0;
    public double MaximumVitalTemperature { get; init; } = 43.0;
    public double MaximumAgeSeconds { get; init; } = 3600.0;
    public double SenescenceDamageRate { get; init; } = 0.002;

    public double Mass => Math.Pow(Size, 3) * (1 + 0.5 * Armor);

    public void Validate()
    {
        if (!double.IsFinite(Size) || Size <= 0) throw new ArgumentOutOfRangeException(nameof(Size));
        ValidateNormalized(Armor, nameof(Armor));
        ValidateNormalized(Insulation, nameof(Insulation));
        if (!double.IsFinite(SetPointTemperature)) throw new ArgumentOutOfRangeException(nameof(SetPointTemperature));
        if (MaximumAgeSeconds <= 0 || !double.IsFinite(MaximumAgeSeconds)) throw new ArgumentOutOfRangeException(nameof(MaximumAgeSeconds));
    }

    private static void ValidateNormalized(double value, string name)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(name);
    }
}
