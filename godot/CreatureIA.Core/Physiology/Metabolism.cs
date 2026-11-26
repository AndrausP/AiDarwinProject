namespace CreatureIA.Core.Physiology;

public static class Metabolism
{
    public static (double EnergyCost, double WaterCost, double Fatigue) Calculate(
        BodyTraits traits,
        PhysiologyState state,
        PhysiologyInput input,
        double deltaSeconds)
    {
        var thrust = ClampAction(input.Thrust);
        var gait = ClampAction(input.Gait);
        var turn = ClampAction(input.Turn);
        var effort = thrust * thrust * gait * gait;
        var basal = traits.BasalMetabolicRate * Math.Pow(traits.Mass, 0.75);
        var movement = traits.MovementCost * traits.Mass * effort;
        var turning = traits.TurnCost * turn * turn;
        var sleepMultiplier = input.Sleeping ? 0.5 : 1.0;
        var energyCost = deltaSeconds * (basal + movement + turning + traits.BrainCost) * sleepMultiplier;

        var sweat = traits.SweatCost * Math.Max(0, state.BodyTemperature - traits.SetPointTemperature);
        var waterCost = deltaSeconds * (traits.BasalWaterLoss + sweat) * sleepMultiplier;

        var fatigue = input.Sleeping
            ? state.Fatigue - deltaSeconds * traits.FatigueRecoveryRate
            : state.Fatigue + deltaSeconds * (traits.FatigueBaseRate + traits.FatigueEffortRate * gait * gait);

        return (Math.Max(0, energyCost), Math.Max(0, waterCost), Math.Clamp(fatigue, 0, 1));
    }

    private static double ClampAction(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "Action must be finite.");
        return Math.Clamp(value, -1, 1);
    }
}
