using CreatureIA.Core.Genetics;
using CreatureIA.Core.Simulation;
using NUnit.Framework;
using LifeCycle = CreatureIA.Core.Genetics.LifeCycle;

namespace CreatureIA.Tests;

[TestFixture]
public sealed class LifeCycleTests
{
    private static readonly LifeCycle SpeciesA = new(LongevityYears: 3f, MaturityYears: 0.4f, NewbornSizeFraction: 0.2f);

    [Test]
    public void Should_LeaveTenPercentAlive_When_CohortReachesLongevity()
    {
        Assert.That(SpeciesA.Survival(0), Is.EqualTo(1.0));
        Assert.That(SpeciesA.Survival(3 * LifeCycle.SecondsPerYear), Is.EqualTo(0.1).Within(1e-9));
        Assert.That(SpeciesA.Survival(1 * LifeCycle.SecondsPerYear), Is.GreaterThan(0.9), "few die young of old age");
    }

    [Test]
    public void Should_RaiseMortalityExponentially_When_AgeIncreases()
    {
        var year = (double)LifeCycle.SecondsPerYear;
        var ratio1 = SpeciesA.Hazard(2 * year) / SpeciesA.Hazard(1 * year);
        var ratio2 = SpeciesA.Hazard(3 * year) / SpeciesA.Hazard(2 * year);

        Assert.That(ratio1, Is.GreaterThan(1.0));
        Assert.That(ratio2, Is.EqualTo(ratio1).Within(1e-9), "Gompertz: constant ratio per equal age step");
    }

    [Test]
    public void Should_MatchAnalyticSurvival_When_CohortIsSimulatedStepByStep()
    {
        // 4000 individuals stepped in 1-hour (simulated) steps; compare the empirical survival curve.
        var rng = new SimRandom(12);
        const int n = 4000;
        const double dt = 3600.0;
        var ages = new double[n];
        var alive = Enumerable.Repeat(true, n).ToArray();
        foreach (var checkpointYears in new[] { 1.5, 2.5, 3.0, 3.5 })
        {
            var target = checkpointYears * LifeCycle.SecondsPerYear;
            for (var i = 0; i < n; i++)
                while (alive[i] && ages[i] + dt <= target)
                {
                    if (SpeciesA.DiesOfAge(ages[i], dt, rng)) alive[i] = false;
                    ages[i] += dt;
                }
            var empirical = alive.Count(a => a) / (double)n;
            Assert.That(empirical, Is.EqualTo(SpeciesA.Survival(target)).Within(0.025), $"at {checkpointYears} years");
        }
    }

    [Test]
    public void Should_GrowFromNewbornToAdult_When_MaturityIsReached()
    {
        var maturity = 0.4 * LifeCycle.SecondsPerYear;

        Assert.That(SpeciesA.SizeFraction(0), Is.EqualTo(0.2f).Within(1e-6f));
        Assert.That(SpeciesA.SizeFraction(maturity), Is.EqualTo(0.95f).Within(1e-5f));
        Assert.That(SpeciesA.SizeFraction(maturity / 2), Is.InRange(0.2f, 0.95f));
        Assert.That(SpeciesA.IsMature(maturity * 0.99), Is.False);
        Assert.That(SpeciesA.IsMature(maturity), Is.True);
    }

    [Test]
    public void Should_LiveLonger_When_LongevityGeneIsHigher()
    {
        var catalog = GeneCatalog.Basic();
        var shortLived = Genome.Founder(catalog, Sex.Female);
        var longLived = Genome.Founder(catalog, Sex.Female);
        var locus = catalog.IndexOf("longevity_years");
        longLived.Maternal[locus] = new Allele(20f, 0.5f);
        longLived.Paternal[locus] = new Allele(20f, 0.5f);
        var at = 5.0 * LifeCycle.SecondsPerYear;

        Assert.That(LifeCycle.FromGenome(longLived).Survival(at), Is.GreaterThan(LifeCycle.FromGenome(shortLived).Survival(at)));
    }
}
