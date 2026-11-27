using System.Numerics;
using CreatureIA.Core.Simulation;
using NUnit.Framework;

namespace CreatureIA.Tests;

[TestFixture]
public sealed class SimulationTests
{
    // Small worlds keep generation fast; the default 384 m map is exercised by the Sim benchmark.
    private static SimWorld NewWorld(int seed = 3, int population = 8) =>
        new(new SimWorldSettings { Seed = seed, Population = population, Plants = 600, Lakes = 6 });

    private static float[] RandomAction(SimRandom rng)
    {
        var action = new float[ObservationLayout.ActionCount];
        for (var i = 0; i < action.Length; i++) action[i] = rng.Range(-1f, 1f);
        return action;
    }

    [Test]
    public void Should_ProduceIdenticalObservations_When_SameSeedAndActions()
    {
        float[] Run()
        {
            var world = NewWorld();
            var rng = new SimRandom(9);
            var observation = new float[ObservationLayout.Total];
            var digest = new List<float>();
            for (var tick = 0; tick < 40; tick++)
            {
                world.StepBrainTick();
                foreach (var creature in world.Creatures.ToArray())
                {
                    world.Observe(creature, observation);
                    digest.Add(observation.Sum());
                    creature.SetAction(RandomAction(rng));
                }
            }
            return digest.ToArray();
        }

        Assert.That(Run(), Is.EqualTo(Run()));
    }

    [Test]
    public void Should_RoundTripDirections_When_ConvertingBetweenLocalAndWorld()
    {
        var creature = new SimCreature(1, Vector3.Zero, (1f, 1f, 1f)) { Yaw = 0.7f };
        var local = new Vector3(0.3f, -0.2f, -0.9f);

        var back = creature.ToLocal(creature.ToWorld(local));

        Assert.That(Vector3.Distance(back, local), Is.LessThan(1e-5f));
        Assert.That(Vector3.Distance(creature.ToWorld(-Vector3.UnitZ), creature.Forward), Is.LessThan(1e-5f));
        Assert.That(Vector3.Distance(creature.ToWorld(Vector3.UnitX), creature.Right), Is.LessThan(1e-5f));
    }

    [Test]
    public void Should_FaceMinusZ_When_YawIsZero()
    {
        var creature = new SimCreature(1, Vector3.Zero, (1f, 1f, 1f));

        Assert.That(Vector3.Distance(creature.Forward, -Vector3.UnitZ), Is.LessThan(1e-6f));
    }

    [Test]
    public void Should_FillWellFormedObservation_When_CreatureObserves()
    {
        var world = NewWorld();
        world.StepBrainTick();
        var creature = world.Creatures[0];
        var observation = new float[ObservationLayout.Total];

        world.Observe(creature, observation);

        for (var ray = 0; ray < ObservationLayout.Rays; ray++)
        {
            var offset = ray * ObservationLayout.FeaturesPerRay;
            Assert.That(observation[offset], Is.InRange(0f, 1f));
            var hot = 0f;
            for (var k = 1; k < ObservationLayout.FeaturesPerRay; k++) hot += observation[offset + k];
            Assert.That(hot, Is.EqualTo(1f), $"ray {ray} must have exactly one class");
        }
        var intero = ObservationLayout.InteroceptionOffset;
        Assert.That(observation[intero], Is.EqualTo(0.6f).Within(0.01f), "newborn energy");
        Assert.That(observation[intero + 1], Is.EqualTo(0.7f).Within(0.01f), "newborn water");
        Assert.That(observation[intero + 2], Is.EqualTo(37f).Within(1f), "body temperature");
        Assert.That(observation.All(float.IsFinite), Is.True);
    }

    [Test]
    public void Should_RaiseWater_When_CreatureDrinksAtLakeShore()
    {
        var world = NewWorld(population: 0);
        var lake = world.Terrain.World.Lakes[0];
        // Walk outward from the lake centre until dry land next to water.
        Vector3? shore = null;
        for (var r = lake.RadiusMeters * 0.5f; r < lake.RadiusMeters * 2f && shore is null; r += 0.25f)
        {
            var x = lake.CenterX + r;
            var sample = world.Terrain.Sample(x, lake.CenterZ);
            var p = new Vector3(x, sample.Height + 0.05f, lake.CenterZ);
            if (sample.WaterDepth <= 0f && world.Terrain.IsNearDrinkableWater(p)) shore = p;
        }
        Assert.That(shore, Is.Not.Null, "lake must have a drinkable shore");
        var creature = world.Spawn(1, shore)!;
        var drink = new float[ObservationLayout.ActionCount];
        drink[6] = 1f;
        drink[7] = -1f;
        creature.SetAction(drink);
        var before = creature.Physiology.Water;

        for (var tick = 0; tick < 50; tick++) world.StepBrainTick();

        Assert.That(creature.Physiology.Water, Is.GreaterThan(before + 0.05));
    }

    [Test]
    public void Should_RaiseEnergyAndReportAte_When_CreatureEatsNextToPlant()
    {
        var world = NewWorld(population: 0);
        var plant = world.Plants.Plants.First(p => p.Biomass > 0.5f);
        var creature = world.Spawn(1, plant.Position + new Vector3(0.6f, 0.05f, 0f))!;
        var eat = new float[ObservationLayout.ActionCount];
        eat[5] = 1f;
        eat[7] = -1f;
        creature.SetAction(eat);
        var before = creature.Physiology.Energy;

        for (var tick = 0; tick < 20; tick++) world.StepBrainTick();
        var events = world.TakeEvents();

        Assert.That(creature.Physiology.Energy, Is.GreaterThan(before));
        Assert.That(events.Any(e => e.Kind == SimEventKind.Ate && e.CreatureId == 1), Is.True);
    }

    [Test]
    public void Should_HearCallInFrontQuadrant_When_NeighbourCallsAhead()
    {
        var world = NewWorld(population: 0);
        var spot = world.Terrain.RandomLandPoint(new SimRandom(5), 60f) + Vector3.UnitY * 0.05f;
        var listener = world.Spawn(1, spot)!;
        var caller = world.Spawn(2, spot + new Vector3(0.5f, 0f, -6f))!; // ahead and slightly right
        var call = new float[ObservationLayout.ActionCount];
        call[9] = 1f;
        caller.SetAction(call);
        world.StepPhysics();
        var observation = new float[ObservationLayout.Total];

        world.Observe(listener, observation);

        var frontRight = observation[ObservationLayout.AudioOffset + 2];
        Assert.That(frontRight, Is.GreaterThan(0.5f));
        Assert.That(observation[ObservationLayout.AudioOffset + 4], Is.EqualTo(0f), "nothing behind");
    }

    [Test]
    public void Should_ReportDeathOnce_When_CreatureDies()
    {
        var world = NewWorld(population: 0);
        var spot = world.Terrain.RandomLandPoint(new SimRandom(5), 60f);
        var creature = world.Spawn(1, spot + Vector3.UnitY * 0.05f)!;
        creature.ApplyDamage(world, BodyZone.Head, 2f, CreatureIA.Core.Physiology.DeathCause.HeadInjury);
        for (var tick = 0; tick < 60 && creature.IsAlive; tick++) world.StepBrainTick();

        var deaths = world.TakeEvents().Count(e => e.Kind == SimEventKind.Death);

        Assert.That(creature.IsAlive, Is.False);
        Assert.That(deaths, Is.EqualTo(1));
        Assert.That(world.Creatures, Is.Empty);
    }

    [Test]
    public void Should_RejectSpawn_When_IdWasAlreadyUsed()
    {
        var world = NewWorld(population: 2);

        Assert.That(world.Spawn(1, null), Is.Null);
        Assert.That(world.Spawn(3, null), Is.Not.Null);
    }

    [Test]
    public void Should_GiveSameSequence_When_SimRandomSeedsMatch()
    {
        var a = new SimRandom(42);
        var b = new SimRandom(42);
        var c = new SimRandom(43);

        var sa = Enumerable.Range(0, 5).Select(_ => a.NextUInt()).ToArray();

        Assert.That(Enumerable.Range(0, 5).Select(_ => b.NextUInt()), Is.EqualTo(sa));
        Assert.That(Enumerable.Range(0, 5).Select(_ => c.NextUInt()), Is.Not.EqualTo(sa));
        Assert.That(Enumerable.Range(0, 1000).Select(_ => a.NextFloat()).All(f => f is >= 0f and < 1f), Is.True);
    }
}
