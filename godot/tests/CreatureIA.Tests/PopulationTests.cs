using System.Numerics;
using CreatureIA.Core.Genetics;
using CreatureIA.Core.Physiology;
using CreatureIA.Core.Simulation;
using NUnit.Framework;
using LifeCycle = CreatureIA.Core.Genetics.LifeCycle;

namespace CreatureIA.Tests;

/// <summary>Etapa 2 life in the simulator (T2.4, T2.1b/c, T2.5, T2.8): births, rescue, control, death causes.</summary>
[TestFixture]
public sealed class PopulationTests
{
    private static SimWorld EmptyWorld(bool births = true, bool selection = true, int minimum = 0, int population = 8) =>
        new(new SimWorldSettings
        {
            Seed = 5, Population = population, Plants = 600, Lakes = 6, Births = births, Selection = selection,
            MinPopulation = minimum, MaxPopulation = 200,
        }, Array.Empty<uint>());

    private static float[] Action(params (int Index, float Value)[] values)
    {
        var action = new float[ObservationLayout.ActionCount];
        foreach (var (i, v) in values) action[i] = v;
        return action;
    }

    private static SimCreature Adult(SimWorld world, uint id, Sex sex, Vector3 at)
    {
        var genome = Species.Founder(FounderSpecies.A, sex, new SimRandom(id));
        return world.AddForTest(new SimCreature(id, at, genome, FounderSpecies.A), ageSeconds: 2 * LifeCycle.SecondsPerYear * 0.15, energy: 1.0);
    }

    [Test]
    public void Should_BearChildOfBothParents_When_WillingAdultsMate()
    {
        var world = EmptyWorld();
        var spot = world.Terrain.RandomLandPoint(new SimRandom(3), 40f) + Vector3.UnitY * 0.05f;
        var mother = Adult(world, 1, Sex.Female, spot);
        var father = Adult(world, 2, Sex.Male, spot + new Vector3(0.8f, 0f, 0f));
        var mate = Action((8, 1f));
        var births = new List<SimEvent>();
        for (var tick = 0; tick < 4000 && births.Count == 0; tick++)
        {
            foreach (var c in world.Creatures) c.SetAction(c.Id <= 2 ? mate : Action());
            world.StepBrainTick();
            births.AddRange(world.TakeEvents().Where(e => e.Kind == SimEventKind.Birth));
        }

        Assert.That(births, Is.Not.Empty, "a willing fed pair conceives and the mother gives birth after gestation");
        var child = world.Creatures.First(c => c.Id == births[0].CreatureId);
        Assert.Multiple(() =>
        {
            Assert.That(child.MotherId, Is.EqualTo(mother.Id));
            Assert.That(child.FatherId, Is.EqualTo(father.Id));
            Assert.That(child.Generation, Is.EqualTo(1));
            Assert.That(child.Id, Is.GreaterThanOrEqualTo(SimWorld.SimulatorIdBase));
            Assert.That(child.IsMature, Is.False);
            Assert.That(child.SizeScale, Is.LessThan(mother.SizeScale));
            Assert.That(world.Population.Births, Is.GreaterThanOrEqualTo(1));
        });
    }

    [Test]
    public void Should_KeepFoundingSpeciesApart_When_CrossingThem()
    {
        var rng = new SimRandom(8);
        float Viability(FounderSpecies a, FounderSpecies b) =>
            Enumerable.Range(0, 40).Average(_ => Reproduction.Viability(Reproduction.Distance(
                Species.Founder(a, Sex.Female, rng), Species.Founder(b, Sex.Male, rng))));

        Assert.That(Viability(FounderSpecies.A, FounderSpecies.A), Is.GreaterThan(0.8f));
        Assert.That(Viability(FounderSpecies.A, FounderSpecies.B), Is.LessThan(0.3f));
        Assert.That(Viability(FounderSpecies.A, FounderSpecies.C), Is.LessThan(0.3f));
    }

    [Test]
    public void Should_NotConceive_When_OnlyOneWantsToMate()
    {
        var world = EmptyWorld();
        var spot = world.Terrain.RandomLandPoint(new SimRandom(3), 40f) + Vector3.UnitY * 0.05f;
        Adult(world, 1, Sex.Female, spot);
        Adult(world, 2, Sex.Male, spot + new Vector3(0.8f, 0f, 0f));
        for (var tick = 0; tick < 200; tick++)
        {
            foreach (var c in world.Creatures) c.SetAction(c.Id == 1 ? Action((8, 1f)) : Action());
            world.StepBrainTick();
        }

        Assert.That(world.Population.Conceptions, Is.Zero);
    }

    [Test]
    public void Should_RescueFromArchive_When_PopulationFallsBelowMinimum()
    {
        var world = EmptyWorld(minimum: 6);

        world.StepBrainTick();

        Assert.That(world.Creatures.Count, Is.EqualTo(6));
        Assert.That(world.Population.Rescues, Is.EqualTo(6));
        Assert.That(world.Creatures.All(c => c.Id >= SimWorld.SimulatorIdBase), Is.True);
    }

    [Test]
    public void Should_BreedRandomParents_When_ControlRunIgnoresActions()
    {
        var world = EmptyWorld(selection: false, population: 12);
        var spot = world.Terrain.RandomLandPoint(new SimRandom(3), 40f) + Vector3.UnitY * 0.05f;
        for (uint id = 1; id <= 6; id++) Adult(world, id, id % 2 == 0 ? Sex.Male : Sex.Female, spot + new Vector3(id * 3f, 0f, 0f));
        // Nobody chooses to mate, nobody is near anybody: in the control run parents are drawn anyway.
        for (var tick = 0; tick < 30; tick++) world.StepBrainTick();

        Assert.That(world.Population.Conceptions, Is.GreaterThan(0));
    }

    [Test]
    public void Should_DieOfAge_When_FarBeyondLongevity()
    {
        var world = EmptyWorld(births: false);
        var spot = world.Terrain.RandomLandPoint(new SimRandom(3), 40f) + Vector3.UnitY * 0.05f;
        var old = world.AddForTest(new SimCreature(1, spot, Species.Founder(FounderSpecies.A, Sex.Female, new SimRandom(1)), FounderSpecies.A),
            ageSeconds: 6 * LifeCycle.SecondsPerYear);
        for (var tick = 0; tick < 600 && old.IsAlive; tick++) world.StepBrainTick();

        Assert.That(old.IsAlive, Is.False);
        Assert.That(old.Physiology.DeathCause, Is.EqualTo(DeathCause.Age));
    }

    [Test]
    public void Should_LeaveCarcassThatRots_When_CreatureDies()
    {
        var world = EmptyWorld(births: false);
        var spot = world.Terrain.RandomLandPoint(new SimRandom(3), 40f) + Vector3.UnitY * 0.05f;
        var victim = Adult(world, 1, Sex.Female, spot);
        victim.ApplyDamage(world, BodyZone.Head, 2f, DeathCause.HeadInjury);
        world.StepPhysics();

        Assert.That(world.Carcasses, Has.Count.EqualTo(1));
        var meat = world.Carcasses[0].Meat;
        for (var i = 0; i < 600; i++) world.StepPhysics();
        Assert.That(world.Carcasses.Count == 0 || world.Carcasses[0].Meat < meat, Is.True);
    }

    [Test]
    public void Should_HurtPrey_When_CarnivoreBites()
    {
        var world = EmptyWorld(births: false);
        var spot = world.Terrain.RandomLandPoint(new SimRandom(3), 40f) + Vector3.UnitY * 0.05f;
        var prey = Adult(world, 1, Sex.Female, spot);
        var hunter = world.AddForTest(new SimCreature(2, spot + new Vector3(0f, 0f, 0.9f), Species.Founder(FounderSpecies.C, Sex.Male, new SimRandom(2)), FounderSpecies.C),
            ageSeconds: LifeCycle.SecondsPerYear);
        hunter.Yaw = 0f; // faces −Z, towards the prey
        var damaged = false;
        for (var tick = 0; tick < 30 && !damaged; tick++)
        {
            hunter.SetAction(Action((5, 1f)));
            prey.SetAction(Action());
            world.StepBrainTick();
            damaged = world.TakeEvents().Any(e => e.Kind == SimEventKind.Damaged && e.CreatureId == prey.Id && e.SourceId == hunter.Id);
        }

        Assert.That(damaged, Is.True);
        Assert.That(prey.Physiology.TorsoIntegrity, Is.LessThan(1.0));
    }

    [Test]
    public void Should_RefuseDrink_When_ShallowLakeIsDryInLateSummer()
    {
        var world = EmptyWorld(births: false);
        var lake = world.Terrain.World.Lakes.OrderBy(l => l.RadiusMeters).First();
        var shore = new Vector3(lake.CenterX, world.Terrain.World.WaterSurfaceY, lake.CenterZ);
        world.Terrain.SetSeason(0.5f);
        var wet = world.Terrain.IsNearDrinkableWater(shore) || lake.RadiusMeters >= SimTerrain.ShallowLakeRadiusMeters;
        world.Terrain.SetSeason(1f);
        var dry = !world.Terrain.IsNearDrinkableWater(shore);

        Assert.That(wet, Is.True);
        Assert.That(dry || lake.RadiusMeters >= SimTerrain.ShallowLakeRadiusMeters, Is.True);
    }

    [Test]
    public void Should_KeepGenomesAndPregnancy_When_WorldIsSnapshotted()
    {
        var world = EmptyWorld();
        var spot = world.Terrain.RandomLandPoint(new SimRandom(3), 40f) + Vector3.UnitY * 0.05f;
        var mother = Adult(world, 1, Sex.Female, spot);
        var father = Adult(world, 2, Sex.Male, spot + new Vector3(0.8f, 0f, 0f));
        var mate = Action((8, 1f));
        for (var tick = 0; tick < 400 && !mother.IsPregnant; tick++)
        {
            mother.SetAction(mate);
            father.SetAction(mate);
            world.StepBrainTick();
        }
        Assume.That(mother.IsPregnant, Is.True);
        using var snapshot = new MemoryStream();
        world.Save(snapshot);
        var restored = SimWorld.Load(new MemoryStream(snapshot.ToArray()));

        Assert.That(restored.StateHash(), Is.EqualTo(world.StateHash()));
        Assert.That(restored.Creatures.First(c => c.Id == 1).IsPregnant, Is.True);
    }
}
