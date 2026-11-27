using CreatureIA.Core.Simulation;

namespace CreatureIA.Core.Genetics;

/// <summary>
/// Life cycle from genes (T2.2c): Gompertz senescence (mortality rising exponentially with age) and
/// growth from a newborn size to adult size by the age of maturity. Time in simulated seconds; one
/// year is 12 days of 600 s (etapa-2 plan).
/// </summary>
public readonly record struct LifeCycle(float LongevityYears, float MaturityYears, float NewbornSizeFraction)
{
    public const float SecondsPerYear = 12f * 600f;
    /// <summary>Share of a cohort still alive at the longevity age (defines "longevity").</summary>
    public const double SurvivalAtLongevity = 0.1;
    /// <summary>Gompertz shape: mortality multiplies by e every LongevityYears / Steepness.</summary>
    public const double Steepness = 6.0;
    /// <summary>Size fraction reached at the age of maturity.</summary>
    public const float AdultFraction = 0.95f;

    public static LifeCycle FromGenome(Genome genome, float newbornSizeFraction = 0.25f) =>
        new(genome.Phenotype("longevity_years"), genome.Phenotype("maturity_years"), newbornSizeFraction);

    private double B => Steepness / (LongevityYears * SecondsPerYear);
    private double A => B * Math.Log(1.0 / SurvivalAtLongevity) / (Math.Exp(Steepness) - 1.0);

    /// <summary>Instantaneous senescence mortality at <paramref name="ageSeconds"/> (per second).</summary>
    public double Hazard(double ageSeconds) => A * Math.Exp(B * ageSeconds);

    /// <summary>Probability of escaping senescent death until <paramref name="ageSeconds"/>.</summary>
    public double Survival(double ageSeconds) => Math.Exp(-(A / B) * (Math.Exp(B * ageSeconds) - 1.0));

    /// <summary>Chance of dying of old age during [age, age + dt): exact for the Gompertz law.</summary>
    public double DeathChance(double ageSeconds, double dt) => 1.0 - Survival(ageSeconds + dt) / Survival(ageSeconds);

    /// <summary>Rolls the senescent death for one step.</summary>
    public bool DiesOfAge(double ageSeconds, double dt, SimRandom rng) => rng.NextFloat() < DeathChance(ageSeconds, dt);

    /// <summary>Body size as a fraction of the adult size: exponential approach, 95 % at maturity.</summary>
    public float SizeFraction(double ageSeconds)
    {
        var k = Math.Log((1.0 - NewbornSizeFraction) / (1.0 - AdultFraction)) / (MaturityYears * SecondsPerYear);
        return (float)(1.0 - (1.0 - NewbornSizeFraction) * Math.Exp(-k * Math.Max(0.0, ageSeconds)));
    }

    public bool IsMature(double ageSeconds) => ageSeconds >= MaturityYears * SecondsPerYear;
}
