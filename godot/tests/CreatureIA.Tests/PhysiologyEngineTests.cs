using CreatureIA.Core.Physiology;
using NUnit.Framework;

namespace CreatureIA.Tests;

[TestFixture]
public sealed class PhysiologyEngineTests
{
    private readonly PhysiologyEngine _engine = new();

    [Test]
    public void Should_DecreaseEnergyFaster_When_CreatureIsRunning()
    {
        var traits = new BodyTraits();
        var resting = _engine.Step(new PhysiologyState(), traits, new PhysiologyInput(), 1).State;
        var running = _engine.Step(new PhysiologyState(), traits, new PhysiologyInput { Thrust = 1, Gait = 1 }, 1).State;
        Assert.That(running.Energy, Is.LessThan(resting.Energy));
    }

    [Test]
    public void Should_HalveBasalCost_When_CreatureIsSleeping()
    {
        var traits = new BodyTraits { BrainCost = 0, MovementCost = 0, TurnCost = 0, BasalWaterLoss = 0 };
        var awake = _engine.Step(new PhysiologyState(), traits, new PhysiologyInput(), 1);
        var asleep = _engine.Step(new PhysiologyState(), traits, new PhysiologyInput { Sleeping = true }, 1);
        Assert.That(asleep.EnergyCost, Is.EqualTo(awake.EnergyCost / 2).Within(1e-12));
    }

    [Test]
    public void Should_DamageTorso_When_EnergyIsZero()
    {
        var result = _engine.Step(new PhysiologyState { Energy = 0 }, new BodyTraits(), new PhysiologyInput(), 1);
        Assert.That(result.State.TorsoIntegrity, Is.LessThan(1));
    }

    [Test]
    public void Should_NotHeal_When_EnergyBelowThreshold()
    {
        var result = _engine.Step(new PhysiologyState { Energy = 0.2, HeadIntegrity = 0.5 }, new BodyTraits(), new PhysiologyInput(), 1);
        Assert.That(result.State.HeadIntegrity, Is.EqualTo(0.5));
        Assert.That(result.HealingEnergyCost, Is.Zero);
    }

    [Test]
    public void Should_KillCreature_When_HeadIntegrityReachesZero()
    {
        var input = new PhysiologyInput { DamageEvents = new[] { new DamageEvent(DamageZone.Head, 1) } };
        var result = _engine.Step(new PhysiologyState(), new BodyTraits(), input, 1.0 / 60);
        Assert.That(result.State.IsAlive, Is.False);
        Assert.That(result.State.DeathCause, Is.EqualTo(DeathCause.HeadInjury));
        Assert.That(result.DiedThisStep, Is.True);
    }

    [Test]
    public void Should_ReduceDamage_When_CreatureHasArmor()
    {
        var input = new PhysiologyInput { DamageEvents = new[] { new DamageEvent(DamageZone.Legs, 0.5) } };
        var unarmored = _engine.Step(new PhysiologyState(), new BodyTraits { Armor = 0 }, input, 1.0 / 60).State;
        var armored = _engine.Step(new PhysiologyState(), new BodyTraits { Armor = 1 }, input, 1.0 / 60).State;
        Assert.That(armored.LegsIntegrity, Is.GreaterThan(unarmored.LegsIntegrity));
    }

    [Test]
    public void Should_DepleteEnergyWithinTolerance_When_BasalRateIsKnown()
    {
        var traits = new BodyTraits
        {
            BasalMetabolicRate = 0.01,
            BrainCost = 0,
            BasalWaterLoss = 0,
            MovementCost = 0,
            TurnCost = 0,
            HeatExchangeRate = 0,
            ShiverPower = 0
        };
        var state = new PhysiologyState();
        var elapsed = 0;
        while (state.Energy > 0 && elapsed < 200)
        {
            state = _engine.Step(state, traits, new PhysiologyInput(), 1).State;
            elapsed++;
        }
        Assert.That(elapsed, Is.EqualTo(100).Within(20));
    }

    [Test]
    public void Should_DepleteWaterWithinTolerance_When_BasalLossIsKnown()
    {
        var traits = new BodyTraits
        {
            BasalMetabolicRate = 0,
            BrainCost = 0,
            BasalWaterLoss = 0.02,
            HeatExchangeRate = 0,
            ShiverPower = 0
        };
        var state = new PhysiologyState();
        var elapsed = 0;
        while (state.Water > 0 && elapsed < 100)
        {
            state = _engine.Step(state, traits, new PhysiologyInput(), 1).State;
            elapsed++;
        }
        Assert.That(elapsed, Is.EqualTo(50).Within(10));
    }

    [Test]
    public void Should_ApplyThermalDamage_When_BodyIsOutsideVitalRange()
    {
        var traits = new BodyTraits { Insulation = 0, HeatExchangeRate = 1, ShiverPower = 0 };
        var result = _engine.Step(new PhysiologyState(), traits, new PhysiologyInput { AmbientTemperature = -20 }, 1);
        Assert.That(result.State.TorsoIntegrity, Is.LessThan(1));
    }

    // Calibration guards (G05): cold must be survivable in mild weather and lethal when freezing,
    // so that random agents die of hunger/thirst and seeking warmth is learnable rather than impossible.
    [Test]
    public void Should_NotDieOfCold_When_RestingTenMinutesInMildAmbient()
    {
        var (state, _) = Simulate(ambient: 10, seconds: 600);
        Assert.That(state.IsAlive, Is.True);
        Assert.That(state.BodyTemperature, Is.GreaterThan(30));
    }

    [Test]
    public void Should_DieOfCold_When_AmbientIsFreezing()
    {
        var (state, _) = Simulate(ambient: -10, seconds: 3600);
        Assert.That(state.IsAlive, Is.False);
        Assert.That(state.DeathCause, Is.EqualTo(DeathCause.Cold));
    }

    private (PhysiologyState State, double Seconds) Simulate(double ambient, double seconds)
    {
        const double dt = 1.0 / 60.0;
        var traits = new BodyTraits();
        var state = new PhysiologyState();
        // Food and water are supplied so only temperature can kill.
        var input = new PhysiologyInput { AmbientTemperature = ambient, EnergyIntake = 0.0002, WaterIntake = 0.0001 };
        var elapsed = 0.0;
        while (elapsed < seconds && state.IsAlive)
        {
            state = _engine.Step(state, traits, input, dt).State;
            elapsed += dt;
        }
        return (state, elapsed);
    }

    [Test]
    public void Should_StayAsleep_When_NoWakeRequest()
    {
        var traits = new BodyTraits();
        var asleep = _engine.Step(new PhysiologyState(), traits, new PhysiologyInput { Sleeping = true }, 1).State;
        var next = _engine.Step(asleep, traits, new PhysiologyInput(), 1).State;
        Assert.That(next.IsSleeping, Is.True);
    }

    [Test]
    public void Should_WakeUp_When_WakeRequested()
    {
        var traits = new BodyTraits();
        var asleep = _engine.Step(new PhysiologyState(), traits, new PhysiologyInput { Sleeping = true }, 1).State;
        var awake = _engine.Step(asleep, traits, new PhysiologyInput { WakeRequested = true }, 1).State;
        Assert.That(awake.IsSleeping, Is.False);
    }
}
