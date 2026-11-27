using System.Numerics;
using CreatureIA.Core.Genetics;
using CreatureIA.Core.Simulation;
using NUnit.Framework;
using LifeCycle = CreatureIA.Core.Genetics.LifeCycle;

namespace CreatureIA.Tests;

/// <summary>Energy reserves scale with body mass (Kleiber: time to starve grows as M^0.25).</summary>
[TestFixture]
public sealed class EnergyBudgetTests
{
    private static double SecondsToHalfEnergy(FounderSpecies species)
    {
        var world = new SimWorld(new SimWorldSettings { Seed = 5, Population = 0, Plants = 10, Lakes = 6, Births = false }, Array.Empty<uint>());
        var spot = world.Terrain.RandomLandPoint(new SimRandom(3), 40f) + Vector3.UnitY * 0.05f;
        var genome = Species.Founder(species, Sex.Female, new SimRandom(1));
        var creature = world.AddForTest(new SimCreature(1, spot, genome, species), 3 * LifeCycle.SecondsPerYear, energy: 1.0);
        for (var t = 0; t < 36000; t++)
        {
            creature.SetAction(new float[ObservationLayout.ActionCount]);
            world.StepBrainTick();
            if (creature.Physiology.Energy <= 0.5) return t / 10.0;
        }
        return double.PositiveInfinity;
    }

    [Test]
    public void Should_StarveSlower_When_BodyIsBigger()
    {
        var small = SecondsToHalfEnergy(FounderSpecies.A);
        var big = SecondsToHalfEnergy(FounderSpecies.B);

        Assert.That(big, Is.GreaterThan(small * 1.3), $"A {small} s, B {big} s");
    }
}
