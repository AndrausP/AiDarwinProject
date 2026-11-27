namespace CreatureIA.Core.Genetics;

/// <summary>
/// One heritable trait. <see cref="Chromosome"/> groups loci that are inherited together unless a
/// crossover falls between them (<see cref="Position"/> orders loci along the chromosome).
/// </summary>
public sealed record GeneDefinition(
    string Name,
    float Min,
    float Max,
    float Default,
    int Chromosome,
    float Position,
    float MutationScale = 0.05f)
{
    public float Range => Max - Min;
    public float Clamp(float value) => Math.Clamp(value, Min, Max);
}

/// <summary>
/// The fixed list of loci every genome carries (dormant parts included, from etapa 2 on). Loci are
/// stored chromosome by chromosome, ordered by position, so crossovers are contiguous index ranges.
/// </summary>
public sealed class GeneCatalog
{
    /// <summary>Name of the locus whose phenotype is the per-allele mutation probability (plan 8.1).</summary>
    public const string MutationRateGene = "mutation_rate";

    public IReadOnlyList<GeneDefinition> Genes { get; }
    /// <summary>Expected crossovers per chromosome per meiosis (≈ 1 morgan per chromosome).</summary>
    public float CrossoversPerChromosome { get; }
    public int MutationRateIndex { get; }
    internal (int Start, int End)[] ChromosomeRanges { get; }
    private readonly Dictionary<string, int> _index;

    public GeneCatalog(IEnumerable<GeneDefinition> genes, float crossoversPerChromosome = 1f)
    {
        var ordered = genes.OrderBy(g => g.Chromosome).ThenBy(g => g.Position).ToArray();
        if (ordered.Length == 0) throw new ArgumentException("catalog needs at least one gene");
        if (ordered.Select(g => g.Name).Distinct().Count() != ordered.Length) throw new ArgumentException("duplicate gene name");
        foreach (var g in ordered)
            if (!(g.Max > g.Min) || g.Default < g.Min || g.Default > g.Max || g.MutationScale < 0f)
                throw new ArgumentException($"invalid gene {g.Name}");
        Genes = ordered;
        CrossoversPerChromosome = crossoversPerChromosome;
        _index = ordered.Select((g, i) => (g.Name, i)).ToDictionary(p => p.Name, p => p.i);
        MutationRateIndex = _index.TryGetValue(MutationRateGene, out var m) ? m : -1;
        ChromosomeRanges = ordered.Select((g, i) => (g.Chromosome, i))
            .GroupBy(p => p.Chromosome)
            .Select(group => (group.Min(p => p.i), group.Max(p => p.i) + 1))
            .ToArray();
    }

    public int Count => Genes.Count;
    public int IndexOf(string name) => _index.TryGetValue(name, out var i) ? i : throw new KeyNotFoundException(name);

    /// <summary>
    /// Starting catalog for etapa 2 (body and life-cycle loci are added by T2.2b/T2.2c). Values in
    /// natural units; the mutation rate is itself heritable and starts at 1 % per allele.
    /// </summary>
    public static GeneCatalog Basic() => new(new[]
    {
        new GeneDefinition(MutationRateGene, 0.0005f, 0.1f, 0.01f, Chromosome: 0, Position: 0f, MutationScale: 0.1f),
        new GeneDefinition("size", 0.5f, 3f, 1f, Chromosome: 1, Position: 0f),
        new GeneDefinition("leg_length", 0f, 1f, 0.5f, Chromosome: 1, Position: 0.5f),
        new GeneDefinition("fur", 0f, 1f, 0.3f, Chromosome: 2, Position: 0f),
        new GeneDefinition("color_hue", 0f, 1f, 0.5f, Chromosome: 2, Position: 0.7f),
        new GeneDefinition("longevity_years", 0.5f, 30f, 3f, Chromosome: 3, Position: 0f),
        new GeneDefinition("maturity_years", 0.1f, 6f, 0.4f, Chromosome: 3, Position: 0.4f),
        new GeneDefinition("fear_weight", 0f, 1f, 0.3f, Chromosome: 4, Position: 0f),
        // Body parts (T2.2b). Dormant parts start at 0 and only work above BodyPlan.FunctionalThreshold.
        new GeneDefinition("claws", 0f, 1f, 0f, Chromosome: 5, Position: 0f),
        new GeneDefinition("carapace", 0f, 1f, 0f, Chromosome: 5, Position: 0.3f),
        new GeneDefinition("horns", 0f, 1f, 0f, Chromosome: 5, Position: 0.6f),
        new GeneDefinition("fins", 0f, 1f, 0f, Chromosome: 6, Position: 0f),
        new GeneDefinition("teeth", 0f, 1f, 0.2f, Chromosome: 6, Position: 0.4f),
        // Sexual dimorphism: males are (1 + bias) times the female size.
        new GeneDefinition("male_size_bias", -0.3f, 0.5f, 0f, Chromosome: 7, Position: 0f),
    });
}
