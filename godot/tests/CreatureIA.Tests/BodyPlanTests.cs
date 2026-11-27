using CreatureIA.Core.Genetics;
using NUnit.Framework;

namespace CreatureIA.Tests;

[TestFixture]
public sealed class BodyPlanTests
{
    private static readonly BodyPlan Reference = new() { Size = 1f, LegLength = 0.5f, Fur = 0.3f, Teeth = 0.2f };

    private static readonly (string Name, Func<BodyPlan, BodyPlan> Grow)[] Traits =
    {
        ("size", b => b with { Size = b.Size + 0.2f }),
        ("leg_length", b => b with { LegLength = b.LegLength + 0.2f }),
        ("fur", b => b with { Fur = b.Fur + 0.2f }),
        ("claws", b => b with { Claws = b.Claws + 0.4f }),
        ("carapace", b => b with { Carapace = b.Carapace + 0.4f }),
        ("horns", b => b with { Horns = b.Horns + 0.4f }),
        ("fins", b => b with { Fins = b.Fins + 0.4f }),
        ("teeth", b => b with { Teeth = b.Teeth + 0.4f }),
    };

    [Test]
    public void Should_CostMoreMetabolism_When_AnyTraitGrows()
    {
        foreach (var (name, grow) in Traits)
            Assert.That(grow(Reference).BasalMetabolicRate, Is.GreaterThan(Reference.BasalMetabolicRate), $"{name} must not be free");
    }

    [Test]
    public void Should_BuySomeAbility_When_AnyTraitGrows()
    {
        foreach (var (name, grow) in Traits)
        {
            var b = grow(Reference);
            var gains = new[]
            {
                b.MaxSpeed > Reference.MaxSpeed, b.StepHeight > Reference.StepHeight, b.ClimbFactor > Reference.ClimbFactor,
                b.DigFactor > Reference.DigFactor, b.SwimFactor > Reference.SwimFactor, b.Armor > Reference.Armor,
                b.BiteDamage > Reference.BiteDamage, b.ToTraits().Insulation > Reference.ToTraits().Insulation,
            };
            Assert.That(gains.Any(g => g), Is.True, $"{name} must buy something");
        }
    }

    [Test]
    public void Should_FollowKleiber_When_MassDoubles()
    {
        var small = new BodyPlan { Size = 1f };
        var big = new BodyPlan { Size = MathF.Pow(2f, 1f / 3f) }; // twice the volume

        var ratio = big.BasalMetabolicRate / small.BasalMetabolicRate;

        Assert.That(big.Mass / small.Mass, Is.EqualTo(2.0).Within(1e-5));
        Assert.That(ratio, Is.EqualTo(Math.Pow(2.0, 0.75)).Within(1e-4));
    }

    [Test]
    public void Should_GiveNoFunctionButCost_When_PartIsBelowThreshold()
    {
        var dormant = Reference with { Claws = BodyPlan.FunctionalThreshold * 0.9f };

        Assert.That(dormant.ClimbFactor, Is.EqualTo(0f));
        Assert.That(dormant.BasalMetabolicRate, Is.GreaterThan(Reference.BasalMetabolicRate));
        Assert.That((Reference with { Claws = 0.6f }).ClimbFactor, Is.GreaterThan(0f));
    }

    [Test]
    public void Should_TradeSpeedForArmor_When_CarapaceGrows()
    {
        var armored = Reference with { Carapace = 0.8f };

        Assert.That(armored.Armor, Is.GreaterThan(Reference.Armor));
        Assert.That(armored.MaxSpeed, Is.LessThan(Reference.MaxSpeed));
    }

    [Test]
    public void Should_MakeMalesBigger_When_DimorphismGeneIsPositive()
    {
        var catalog = GeneCatalog.Basic();
        var female = Genome.Founder(catalog, Sex.Female);
        var male = Genome.Founder(catalog, Sex.Male);
        var bias = catalog.IndexOf("male_size_bias");
        foreach (var g in new[] { female, male })
        {
            g.Maternal[bias] = new Allele(0.3f, 0.5f);
            g.Paternal[bias] = new Allele(0.3f, 0.5f);
        }

        Assert.That(BodyPlan.FromGenome(male).Size, Is.EqualTo(BodyPlan.FromGenome(female).Size * 1.3f).Within(1e-5f));
    }
}
