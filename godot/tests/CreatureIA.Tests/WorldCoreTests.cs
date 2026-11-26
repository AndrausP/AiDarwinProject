using CreatureIA.Core.World;
using NUnit.Framework;

namespace CreatureIA.Tests;

[TestFixture]
public sealed class WorldCoreTests
{
    [Test]
    public void Should_FollowLogisticSolution_When_PlantGrows()
    {
        var result = new PlantResource(0.5).Grow(0.1, 10);
        var expected = 1 / (1 + Math.Exp(-1));
        Assert.That(result.Biomass, Is.EqualTo(expected).Within(1e-12));
    }

    [Test]
    public void Should_NotConsumeMoreBiomassThanPlantHas()
    {
        var result = new PlantResource(0.2).Consume(0.5);
        Assert.Multiple(() =>
        {
            Assert.That(result.Consumed, Is.EqualTo(0.2));
            Assert.That(result.Plant.Biomass, Is.Zero);
        });
    }

    [Test]
    public void Should_RiseFastAndFadeSlowly_When_ThreatComesAndGoes()
    {
        var fear = 0f;
        for (var i = 0; i < 30; i++) fear = CreatureIA.Core.Physiology.FearModel.Step(fear, 1f, 0f, 1f / 60f);
        var afterHalfSecond = fear;
        for (var i = 0; i < 180; i++) fear = CreatureIA.Core.Physiology.FearModel.Step(fear, 0f, 0f, 1f / 60f);
        Assert.Multiple(() =>
        {
            Assert.That(afterHalfSecond, Is.GreaterThan(0.8f));
            // 3 s later with no threat: e^-1 of the peak remains.
            Assert.That(fear, Is.EqualTo(afterHalfSecond * MathF.Exp(-1f)).Within(0.01f));
        });
    }

    [Test]
    public void Should_FearOnlyBiggerOrCarnivores_When_RatingThreat()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CreatureIA.Core.Physiology.FearModel.Threatness(1f, 1f, false), Is.Zero);
            Assert.That(CreatureIA.Core.Physiology.FearModel.Threatness(0.5f, 1f, true), Is.EqualTo(1f));
            Assert.That(CreatureIA.Core.Physiology.FearModel.Threatness(1.65f, 1f, false), Is.EqualTo(0.5f).Within(1e-5f));
        });
    }

    [Test]
    public void Should_ApplyNightVisionMultiplier_When_LightIsLow()
    {
        var midnight = WorldCycles.At(0);
        Assert.That(midnight.VisionMultiplier, Is.EqualTo(0.4));
    }
}
