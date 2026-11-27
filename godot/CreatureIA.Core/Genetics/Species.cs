using CreatureIA.Core.Simulation;

namespace CreatureIA.Core.Genetics;

/// <summary>
/// Founding species of etapa 2 (decision D1): only the starting genomes differ. After the first
/// generation "species" is just a label of descent; real species are whatever stays reproductively
/// compatible (genetic distance, <see cref="Reproduction.Viability"/>).
/// </summary>
public enum FounderSpecies : byte
{
    /// <summary>Small herbivore, r strategy: lives ~3 years, matures fast, big litters.</summary>
    A = 0,
    /// <summary>Large herbivore, K strategy: lives ~20 years, matures late, one young at a time.</summary>
    B = 1,
    /// <summary>Medium carnivore (~8 years), a minority.</summary>
    C = 2,
}

public static class Species
{
    /// <summary>Shared catalog of the simulator (one instance: genomes of one world share it).</summary>
    public static readonly GeneCatalog Catalog = GeneCatalog.Basic();

    /// <summary>Founder population shares A : B : C.</summary>
    public static readonly float[] Shares = { 0.55f, 0.3f, 0.15f };

    /// <summary>Gene values that differ from the catalog defaults, per founder species.</summary>
    public static IReadOnlyDictionary<string, float> Overrides(FounderSpecies species) => species switch
    {
        FounderSpecies.A => new Dictionary<string, float>
        {
            ["size"] = 0.8f, ["longevity_years"] = 3f, ["maturity_years"] = 0.15f, ["litter_size"] = 3f,
            ["gestation_days"] = 0.4f, ["color_hue"] = 0.3f, ["fur"] = 0.3f,
        },
        FounderSpecies.B => new Dictionary<string, float>
        {
            ["size"] = 1.6f, ["longevity_years"] = 20f, ["maturity_years"] = 1.5f, ["litter_size"] = 1f,
            ["gestation_days"] = 2f, ["color_hue"] = 0.08f, ["fur"] = 0.5f, ["horns"] = 0.3f, ["m_care"] = 1f,
        },
        FounderSpecies.C => new Dictionary<string, float>
        {
            ["size"] = 1.2f, ["longevity_years"] = 8f, ["maturity_years"] = 0.6f, ["litter_size"] = 2f,
            ["gestation_days"] = 1f, ["color_hue"] = 0.75f, ["carnivory"] = 0.9f, ["teeth"] = 0.8f, ["claws"] = 0.4f,
            ["leg_length"] = 0.7f, ["m_fear"] = 0.15f,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(species)),
    };

    /// <summary>Founder genome: species values on both haplotypes plus standing variation (mutations at 30 % per allele).</summary>
    public static Genome Founder(FounderSpecies species, Sex sex, SimRandom rng)
    {
        var catalog = Catalog;
        var overrides = Overrides(species);
        Allele[] Haplotype()
        {
            var alleles = catalog.Genes.Select(g => new Allele(overrides.TryGetValue(g.Name, out var v) ? g.Clamp(v) : g.Default, 0.5f)).ToArray();
            Reproduction.Mutate(alleles, catalog, 0.3f, rng);
            return alleles;
        }
        return new Genome(catalog, Haplotype(), Haplotype(), sex);
    }

    public static FounderSpecies Pick(SimRandom rng)
    {
        var x = rng.NextFloat();
        for (var i = 0; i < Shares.Length; i++)
        {
            if (x < Shares[i]) return (FounderSpecies)i;
            x -= Shares[i];
        }
        return FounderSpecies.A;
    }
}
