namespace CreatureIA.Core.Genetics;

/// <summary>One copy of a gene: its value and how strongly it is expressed against the other copy (0..1).</summary>
public readonly record struct Allele(float Value, float Dominance);

public enum Sex : byte { Female, Male }

/// <summary>
/// Diploid genome (Mendelian): two haplotypes, one from each parent, over a <see cref="GeneCatalog"/>.
/// Sex chromosomes are XY: the mother always gives X, the father X or Y. Only this genome is inherited;
/// nothing learned in life is ever written here (Darwin rule of the project).
/// </summary>
public sealed class Genome
{
    public GeneCatalog Catalog { get; }
    public Allele[] Maternal { get; }
    public Allele[] Paternal { get; }
    /// <summary>The paternal sex chromosome is Y (male) or X (female).</summary>
    public Sex Sex { get; }

    public Genome(GeneCatalog catalog, Allele[] maternal, Allele[] paternal, Sex sex)
    {
        if (maternal.Length != catalog.Count || paternal.Length != catalog.Count)
            throw new ArgumentException("haplotype length must match the catalog");
        Catalog = catalog;
        Maternal = maternal;
        Paternal = paternal;
        Sex = sex;
    }

    /// <summary>Founder with every locus at its default value, fully codominant.</summary>
    public static Genome Founder(GeneCatalog catalog, Sex sex) =>
        new(catalog, catalog.Genes.Select(g => new Allele(g.Default, 0.5f)).ToArray(),
            catalog.Genes.Select(g => new Allele(g.Default, 0.5f)).ToArray(), sex);

    /// <summary>
    /// Expressed value: alleles averaged with their dominance as weights (a fully dominant allele
    /// hides a fully recessive one; two recessive copies express themselves).
    /// </summary>
    public float Phenotype(int locus)
    {
        var a = Maternal[locus];
        var b = Paternal[locus];
        var weight = a.Dominance + b.Dominance;
        var value = weight > 1e-6f ? (a.Value * a.Dominance + b.Value * b.Dominance) / weight : (a.Value + b.Value) * 0.5f;
        return Catalog.Genes[locus].Clamp(value);
    }

    public float Phenotype(string name) => Phenotype(Catalog.IndexOf(name));

    public float[] Phenotypes()
    {
        var values = new float[Catalog.Count];
        for (var i = 0; i < values.Length; i++) values[i] = Phenotype(i);
        return values;
    }

    /// <summary>Per-allele mutation probability of this individual's gametes (heritable, plan 8.1).</summary>
    public float MutationRate => Catalog.MutationRateIndex >= 0 ? Phenotype(Catalog.MutationRateIndex) : 0f;
}
