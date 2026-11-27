using CreatureIA.Core.Genetics;
using CreatureIA.Core.Simulation;
using NUnit.Framework;

namespace CreatureIA.Tests;

[TestFixture]
public sealed class GeneticsTests
{
    // One locus "trait" (0..1) plus a heritable mutation rate on its own chromosome.
    private static GeneCatalog Catalog(float mutationRate = 0f) => new(new[]
    {
        new GeneDefinition(GeneCatalog.MutationRateGene, 0f, 0.5f, mutationRate, Chromosome: 0, Position: 0f, MutationScale: 0f),
        new GeneDefinition("trait", 0f, 1f, 0.5f, Chromosome: 1, Position: 0f),
        new GeneDefinition("linked", 0f, 1f, 0.5f, Chromosome: 1, Position: 0.2f),
    });

    /// <summary>Heterozygote Aa: A = 1 fully dominant, a = 0 fully recessive.</summary>
    private static Genome Heterozygote(GeneCatalog catalog, Sex sex)
    {
        var founder = Genome.Founder(catalog, sex);
        var maternal = (Allele[])founder.Maternal.Clone();
        var paternal = (Allele[])founder.Paternal.Clone();
        var trait = catalog.IndexOf("trait");
        maternal[trait] = new Allele(1f, 1f);
        paternal[trait] = new Allele(0f, 0f);
        // Mutation-rate locus fully dominant so its phenotype is exactly the catalog default.
        maternal[catalog.MutationRateIndex] = new Allele(catalog.Genes[catalog.MutationRateIndex].Default, 1f);
        paternal[catalog.MutationRateIndex] = new Allele(catalog.Genes[catalog.MutationRateIndex].Default, 1f);
        return new Genome(catalog, maternal, paternal, sex);
    }

    [Test]
    public void Should_GiveThreeToOneRatio_When_TwoHeterozygotesMate()
    {
        var catalog = Catalog();
        var mother = Heterozygote(catalog, Sex.Female);
        var father = Heterozygote(catalog, Sex.Male);
        var rng = new SimRandom(11);
        const int n = 8000;
        var dominantPhenotype = 0;
        for (var i = 0; i < n; i++)
            if (Reproduction.Child(mother, father, rng).Phenotype("trait") > 0.99f) dominantPhenotype++;

        // Expected 3:1 -> 6000 : 2000. Chi-square with 1 dof below 10.83 (p = 0.001).
        var expectedDominant = n * 0.75;
        var expectedRecessive = n * 0.25;
        var chi2 = Math.Pow(dominantPhenotype - expectedDominant, 2) / expectedDominant
                 + Math.Pow(n - dominantPhenotype - expectedRecessive, 2) / expectedRecessive;
        Assert.That(chi2, Is.LessThan(10.83), $"dominant {dominantPhenotype} of {n}");
    }

    [Test]
    public void Should_ExpressRecessiveTrait_When_BothCopiesAreRecessive()
    {
        var catalog = Catalog();
        var founder = Genome.Founder(catalog, Sex.Female);
        var trait = catalog.IndexOf("trait");
        var maternal = (Allele[])founder.Maternal.Clone();
        var paternal = (Allele[])founder.Paternal.Clone();
        maternal[trait] = new Allele(0.1f, 0f);
        paternal[trait] = new Allele(0.3f, 0f);

        var genome = new Genome(catalog, maternal, paternal, Sex.Female);

        Assert.That(genome.Phenotype(trait), Is.EqualTo(0.2f).Within(1e-6f));
    }

    [Test]
    public void Should_GiveBalancedSexes_When_ManyChildrenAreBorn()
    {
        var catalog = Catalog();
        var rng = new SimRandom(5);
        var males = 0;
        const int n = 6000;
        for (var i = 0; i < n; i++)
            if (Reproduction.Child(Genome.Founder(catalog, Sex.Female), Genome.Founder(catalog, Sex.Male), rng).Sex == Sex.Male) males++;

        Assert.That(males / (double)n, Is.EqualTo(0.5).Within(0.03));
    }

    [Test]
    public void Should_RecombineLinkedLoci_When_CrossoverFallsBetweenThem()
    {
        // Mother: strand M carries (trait=1, linked=1), strand P carries (0, 0). Gap 0.2 -> ~20 % recombinants.
        var catalog = Catalog();
        var founder = Genome.Founder(catalog, Sex.Female);
        var maternal = (Allele[])founder.Maternal.Clone();
        var paternal = (Allele[])founder.Paternal.Clone();
        maternal[catalog.IndexOf("trait")] = new Allele(1f, 0.5f);
        maternal[catalog.IndexOf("linked")] = new Allele(1f, 0.5f);
        paternal[catalog.IndexOf("trait")] = new Allele(0f, 0.5f);
        paternal[catalog.IndexOf("linked")] = new Allele(0f, 0.5f);
        var mother = new Genome(catalog, maternal, paternal, Sex.Female);
        var rng = new SimRandom(3);
        const int n = 6000;
        var recombinant = 0;
        for (var i = 0; i < n; i++)
        {
            var gamete = Reproduction.Gamete(mother, rng);
            if (gamete[catalog.IndexOf("trait")].Value != gamete[catalog.IndexOf("linked")].Value) recombinant++;
        }

        Assert.That(recombinant / (double)n, Is.EqualTo(0.2).Within(0.025));
    }

    [Test]
    public void Should_CopyParentsExactly_When_MutationRateIsZero()
    {
        var catalog = Catalog(mutationRate: 0f);
        var mother = Heterozygote(catalog, Sex.Female);
        var father = Heterozygote(catalog, Sex.Male);
        var stats = new MutationStats();
        var rng = new SimRandom(1);

        for (var i = 0; i < 500; i++)
        {
            var child = Reproduction.Child(mother, father, rng, stats);
            Assert.That(child.Maternal.All(a => mother.Maternal.Contains(a) || mother.Paternal.Contains(a)), Is.True);
            Assert.That(child.Paternal.All(a => father.Maternal.Contains(a) || father.Paternal.Contains(a)), Is.True);
        }
        Assert.That(stats.Mutations, Is.EqualTo(0));
    }

    [Test]
    public void Should_MutateMoreAndMostlyRecessive_When_MutationRateGeneIsHigher()
    {
        var low = Catalog(mutationRate: 0.01f);
        var high = Catalog(mutationRate: 0.2f);
        var lowStats = new MutationStats();
        var highStats = new MutationStats();
        var rng = new SimRandom(8);
        var newDominance = new List<float>();
        for (var i = 0; i < 3000; i++)
        {
            Reproduction.Gamete(Heterozygote(low, Sex.Female), rng, lowStats);
            var parent = Heterozygote(high, Sex.Female);
            var gamete = Reproduction.Gamete(parent, rng, highStats);
            var trait = high.IndexOf("linked"); // founder value 0.5, dominance 0.5: any change is a mutation
            if (gamete[trait].Dominance != 0.5f) newDominance.Add(gamete[trait].Dominance);
        }

        Assert.That(lowStats.Rate, Is.EqualTo(0.01).Within(0.004));
        Assert.That(highStats.Rate, Is.EqualTo(0.2).Within(0.02));
        Assert.That(newDominance.Average(), Is.LessThan(0.35), "new mutations tend to be recessive");
    }

    [Test]
    public void Should_LowerViability_When_ParentsAreMoreDistant()
    {
        var catalog = Catalog();
        var a = Genome.Founder(catalog, Sex.Female);
        var shifted = Genome.Founder(catalog, Sex.Male);
        var trait = catalog.IndexOf("trait");
        shifted.Maternal[trait] = new Allele(1f, 0.5f);
        shifted.Paternal[trait] = new Allele(1f, 0.5f);

        var distance = Reproduction.Distance(a, shifted);

        Assert.That(Reproduction.Distance(a, Genome.Founder(catalog, Sex.Male)), Is.EqualTo(0f));
        Assert.That(distance, Is.EqualTo(0.5f / 3f).Within(1e-6f));
        Assert.That(Reproduction.Viability(0f), Is.EqualTo(1f));
        Assert.That(Reproduction.Viability(distance), Is.LessThan(Reproduction.Viability(distance / 2f)));
        Assert.That(Reproduction.Viability(0.6f), Is.LessThan(0.001f));
    }

    [Test]
    public void Should_BreedIdenticalChild_When_SeedIsRepeated()
    {
        var catalog = GeneCatalog.Basic();
        Genome Breed() => Reproduction.Child(Genome.Founder(catalog, Sex.Female), Genome.Founder(catalog, Sex.Male), new SimRandom(77));

        var a = Breed();
        var b = Breed();

        Assert.That(a.Maternal, Is.EqualTo(b.Maternal));
        Assert.That(a.Paternal, Is.EqualTo(b.Paternal));
        Assert.That(a.Sex, Is.EqualTo(b.Sex));
    }

    [Test]
    public void Should_RejectParents_When_SexesDoNotMatch()
    {
        var catalog = Catalog();

        Assert.Throws<ArgumentException>(() =>
            Reproduction.Child(Genome.Founder(catalog, Sex.Male), Genome.Founder(catalog, Sex.Male), new SimRandom(1)));
    }
}
