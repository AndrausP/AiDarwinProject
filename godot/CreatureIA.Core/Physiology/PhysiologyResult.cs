namespace CreatureIA.Core.Physiology;

public sealed record PhysiologyResult(
    PhysiologyState State,
    double EnergyCost,
    double WaterCost,
    double HealingEnergyCost,
    bool DiedThisStep);
