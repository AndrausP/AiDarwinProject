using CreatureIA.Core.Simulation;

namespace CreatureIA.Core.Genetics;

/// <summary>
/// Meiosis with crossover, mutation and fertilisation. Deterministic for a given <see cref="SimRandom"/>.
/// </summary>
public static class Reproduction
{
    /// <summary>Distance at which the child's viability has dropped to e^-1 (~37 %).</summary>
    public const float DefaultSpeciesDistance = 0.15f;

    /// <summary>Haploid gamete: per chromosome, copy one parental strand, switching strands at crossovers.</summary>
    public static Allele[] Gamete(Genome parent, SimRandom rng, MutationStats? stats = null)
    {
        var catalog = parent.Catalog;
        var gamete = new Allele[catalog.Count];
        foreach (var (start, end) in catalog.ChromosomeRanges)
        {
            var fromMaternal = rng.NextFloat() < 0.5f;
            // Crossover chance between neighbouring loci, proportional to their distance along the chromosome.
            for (var i = start; i < end; i++)
            {
                if (i > start)
                {
                    var gap = Math.Max(0f, catalog.Genes[i].Position - catalog.Genes[i - 1].Position);
                    if (rng.NextFloat() < Math.Min(0.5f, gap * catalog.CrossoversPerChromosome)) fromMaternal = !fromMaternal;
                }
                gamete[i] = fromMaternal ? parent.Maternal[i] : parent.Paternal[i];
            }
        }
        Mutate(gamete, catalog, parent.MutationRate, rng, stats);
        return gamete;
    }

    /// <summary>
    /// New alleles: value shifted by a normal step (scale × range), dominance drawn mostly low, so new
    /// mutations tend to be recessive: they spread hidden and show up when two carriers mate.
    /// </summary>
    public static void Mutate(Allele[] haplotype, GeneCatalog catalog, float rate, SimRandom rng, MutationStats? stats = null)
    {
        if (rate <= 0f) return;
        for (var i = 0; i < haplotype.Length; i++)
        {
            if (rng.NextFloat() >= rate) continue;
            var gene = catalog.Genes[i];
            var step = Normal(rng) * gene.MutationScale * gene.Range;
            var u = rng.NextFloat();
            var dominance = u * u * u; // E = 0.25: mostly recessive
            haplotype[i] = new Allele(gene.Clamp(haplotype[i].Value + step), dominance);
            if (stats is not null) stats.Mutations++;
        }
        if (stats is not null) stats.AllelesCopied += haplotype.Length;
    }

    /// <summary>Child of a mother and a father; sex from the father's gamete (X or Y, 50 %).</summary>
    public static Genome Child(Genome mother, Genome father, SimRandom rng, MutationStats? stats = null)
    {
        if (mother.Sex != Sex.Female || father.Sex != Sex.Male) throw new ArgumentException("needs a female and a male parent");
        if (!ReferenceEquals(mother.Catalog, father.Catalog)) throw new ArgumentException("parents use different catalogs");
        var maternal = Gamete(mother, rng, stats);
        var paternal = Gamete(father, rng, stats);
        var sex = rng.NextFloat() < 0.5f ? Sex.Male : Sex.Female;
        return new Genome(mother.Catalog, maternal, paternal, sex);
    }

    /// <summary>Mean absolute difference of the two individuals' allele means, each locus normalised by its range (0..1).</summary>
    public static float Distance(Genome a, Genome b)
    {
        if (!ReferenceEquals(a.Catalog, b.Catalog)) throw new ArgumentException("different catalogs");
        var total = 0f;
        for (var i = 0; i < a.Catalog.Count; i++)
        {
            var meanA = (a.Maternal[i].Value + a.Paternal[i].Value) * 0.5f;
            var meanB = (b.Maternal[i].Value + b.Paternal[i].Value) * 0.5f;
            total += MathF.Abs(meanA - meanB) / a.Catalog.Genes[i].Range;
        }
        return total / a.Catalog.Count;
    }

    /// <summary>Chance that a fertilised child develops: 1 between identical parents, falling smoothly with distance (speciation).</summary>
    public static float Viability(float distance, float speciesDistance = DefaultSpeciesDistance) =>
        MathF.Exp(-(distance / speciesDistance) * (distance / speciesDistance));

    /// <summary>Standard normal via Box–Muller.</summary>
    internal static float Normal(SimRandom rng)
    {
        var u1 = MathF.Max(1e-7f, rng.NextFloat());
        var u2 = rng.NextFloat();
        return MathF.Sqrt(-2f * MathF.Log(u1)) * MathF.Cos(MathF.Tau * u2);
    }
}

/// <summary>Counts for tests and population reports.</summary>
public sealed class MutationStats
{
    public long Mutations;
    public long AllelesCopied;
    public double Rate => AllelesCopied == 0 ? 0 : (double)Mutations / AllelesCopied;
}
