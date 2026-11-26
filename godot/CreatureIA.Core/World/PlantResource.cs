namespace CreatureIA.Core.World;

public sealed record PlantResource(double Biomass)
{
    public PlantResource Grow(double rate, double deltaSeconds)
    {
        if (!double.IsFinite(rate) || rate < 0) throw new ArgumentOutOfRangeException(nameof(rate));
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0) throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        var biomass = Math.Clamp(Biomass, 0, 1);
        if (biomass is 0 or 1 || rate == 0 || deltaSeconds == 0) return new PlantResource(biomass);

        // Exact logistic solution avoids time-step-dependent growth drift.
        var grown = 1 / (1 + ((1 - biomass) / biomass) * Math.Exp(-rate * deltaSeconds));
        return new PlantResource(Math.Clamp(grown, 0, 1));
    }

    public (PlantResource Plant, double Consumed) Consume(double requested)
    {
        if (!double.IsFinite(requested) || requested < 0) throw new ArgumentOutOfRangeException(nameof(requested));
        var consumed = Math.Min(Math.Clamp(Biomass, 0, 1), requested);
        return (new PlantResource(Math.Clamp(Biomass - consumed, 0, 1)), consumed);
    }
}
