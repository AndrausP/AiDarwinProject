using CreatureIA.Core.Genetics;
using NUnit.Framework;

namespace CreatureIA.Tests;

[TestFixture]
public sealed class SenseProfileTests
{
    [Test]
    public void Should_ReproduceEtapa1Senses_When_GenesAreAtDefault()
    {
        var profile = SenseProfile.FromGenome(Genome.Founder(GeneCatalog.Basic(), Sex.Female));

        Assert.That(profile, Is.EqualTo(SenseProfile.Etapa1));
        Assert.That(profile.VisionRangeMeters, Is.EqualTo(25f));
        Assert.That(profile.FieldOfViewDegrees, Is.EqualTo(210f));
        Assert.That(profile.LightFactor(0f), Is.EqualTo(0.4f).Within(1e-6f), "etapa-1 night range");
        Assert.That(profile.ExtraBasalRate, Is.EqualTo(0.0));
    }

    [Test]
    public void Should_CostEnergy_When_AnySenseIsSharper()
    {
        var sharper = new[]
        {
            SenseProfile.Etapa1 with { VisionRange = 1.5f }, SenseProfile.Etapa1 with { VisionFov = 1.3f },
            SenseProfile.Etapa1 with { NightVision = 0.6f }, SenseProfile.Etapa1 with { Smell = 2f },
            SenseProfile.Etapa1 with { Hearing = 2f },
        };

        foreach (var profile in sharper) Assert.That(profile.ExtraBasalRate, Is.GreaterThan(0.0), profile.ToString());
        Assert.That((SenseProfile.Etapa1 with { Smell = 0.5f }).ExtraBasalRate, Is.LessThan(0.0), "duller nose saves energy");
    }

    [Test]
    public void Should_SeeFartherInTheDark_When_NightVisionGrows()
    {
        var owl = SenseProfile.Etapa1 with { NightVision = 1f };

        Assert.That(owl.LightFactor(0f), Is.EqualTo(1f));
        Assert.That(owl.LightFactor(0f), Is.GreaterThan(SenseProfile.Etapa1.LightFactor(0f)));
        Assert.That(SenseProfile.Etapa1.LightFactor(1f), Is.EqualTo(1f));
    }
}
