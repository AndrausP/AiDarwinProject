using CreatureIA.Core.Genetics;
using CreatureIA.Core.Simulation;
using NUnit.Framework;

namespace CreatureIA.Tests;

[TestFixture]
public sealed class MatingTests
{
    private static readonly GeneCatalog Catalog = GeneCatalog.Basic();
    private const double Adult = 1.0 * CreatureIA.Core.Genetics.LifeCycle.SecondsPerYear;

    private static MateCandidate Candidate(Sex sex, float x = 0f, double age = Adult, double energy = 0.8, bool wants = true,
        Genome? genome = null) => new(genome ?? Genome.Founder(Catalog, sex), age, energy, x, 0f, wants);

    [Test]
    public void Should_Conceive_When_CompatibleAdultsMateClose()
    {
        Assert.That(Mating.Check(Candidate(Sex.Female), Candidate(Sex.Male, x: 1.5f)), Is.EqualTo(MateOutcome.Conceived));
    }

    [TestCase(false, 1f, 1.0, 0.8, MateOutcome.NotBothWilling)]
    [TestCase(true, 3f, 1.0, 0.8, MateOutcome.TooFar)]
    [TestCase(true, 1f, 0.1, 0.8, MateOutcome.Immature)]
    [TestCase(true, 1f, 1.0, 0.3, MateOutcome.TooHungry)]
    public void Should_RefuseMating_When_AConditionFails(bool wants, float distance, double ageYears, double energy, MateOutcome expected)
    {
        var male = Candidate(Sex.Male, x: distance, age: ageYears * CreatureIA.Core.Genetics.LifeCycle.SecondsPerYear, energy: energy, wants: wants);

        Assert.That(Mating.Check(Candidate(Sex.Female), male), Is.EqualTo(expected));
    }

    [Test]
    public void Should_RefuseMating_When_BothHaveTheSameSex()
    {
        Assert.That(Mating.Check(Candidate(Sex.Female), Candidate(Sex.Female)), Is.EqualTo(MateOutcome.SameSex));
    }

    [Test]
    public void Should_CarryGenesOfBothParents_When_ChildIsConceived()
    {
        var mother = Genome.Founder(Catalog, Sex.Female);
        var father = Genome.Founder(Catalog, Sex.Male);
        var fur = Catalog.IndexOf("fur");
        mother.Maternal[fur] = mother.Paternal[fur] = new Allele(0.1f, 0.5f);
        father.Maternal[fur] = father.Paternal[fur] = new Allele(0.9f, 0.5f);
        var rng = new SimRandom(4);

        Genome? child = null;
        for (var i = 0; i < 20 && child is null; i++)
            child = Mating.Conceive(Candidate(Sex.Female, genome: mother), Candidate(Sex.Male, genome: father), rng);

        Assert.That(child, Is.Not.Null);
        // Mutation can nudge a value, so compare with tolerance: one copy from each parent.
        Assert.That(child!.Maternal[fur].Value, Is.EqualTo(0.1f).Within(0.2f));
        Assert.That(child.Paternal[fur].Value, Is.EqualTo(0.9f).Within(0.2f));
    }

    [Test]
    public void Should_RarelyProduceViableChild_When_ParentsAreGeneticallyFar()
    {
        var mother = Genome.Founder(Catalog, Sex.Female);
        var father = Genome.Founder(Catalog, Sex.Male);
        for (var i = 0; i < Catalog.Count; i++)
        {
            var g = Catalog.Genes[i];
            mother.Maternal[i] = mother.Paternal[i] = new Allele(g.Min, 0.5f);
            father.Maternal[i] = father.Paternal[i] = new Allele(g.Max, 0.5f);
        }
        var rng = new SimRandom(9);

        var viable = Enumerable.Range(0, 300).Count(_ =>
            Mating.Conceive(Candidate(Sex.Female, genome: mother), Candidate(Sex.Male, genome: father), rng) is not null);

        Assert.That(viable, Is.LessThan(3), "different species do not interbreed");
    }
}
