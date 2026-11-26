namespace CreatureIA.Core.World;

public readonly record struct CycleModifiers(double Light, double TemperatureOffset, double PlantGrowthMultiplier, double VisionMultiplier);

public static class WorldCycles
{
    public const double DaySeconds = 600;
    public const double YearSeconds = DaySeconds * 8;

    public static CycleModifiers At(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0) throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        var dayPhase = elapsedSeconds % DaySeconds / DaySeconds;
        var light = Math.Clamp((Math.Sin((dayPhase - 0.25) * Math.Tau) + 1) / 2, 0, 1);
        var isNight = light < 0.25;
        var yearPhase = elapsedSeconds % YearSeconds / YearSeconds;
        var winterStrength = Math.Clamp((Math.Cos(yearPhase * Math.Tau) + 1) / 2, 0, 1);
        var winterGrowth = 1 - 0.8 * winterStrength;
        return new CycleModifiers(
            light,
            (isNight ? -8 : 0) - 10 * winterStrength,
            winterGrowth * Math.Max(0.1, light),
            isNight ? 0.4 : 1.0);
    }
}
