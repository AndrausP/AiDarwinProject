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

    /// <summary>Prefix of the motivation loci (reward weights), shown to the brain apart from the body genes.</summary>
    public const string MotivationPrefix = "m_";
    /// <summary>Motivation order of the protocol and of the brain reward.</summary>
    public static readonly string[] MotivationOrder =
        { "m_energy", "m_water", "m_temperature", "m_integrity", "m_fatigue", "m_libido", "m_pain", "m_fear", "m_care" };
    /// <summary>Indices of the body/life genes (everything but motivations), in catalog order.</summary>
    public int[] BodyGeneIndices() => Genes.Select((g, i) => (g, i)).Where(p => !p.g.Name.StartsWith(MotivationPrefix)).Select(p => p.i).ToArray();
    public int[] MotivationIndices() => MotivationOrder.Select(IndexOf).ToArray();

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
        // Motivations (D3, T2.2e): the individual's reward weights, prefixed "m_" (protocol v3 motivations block).
        // Defaults = brain RewardConfig; libido has setpoint 0 (mating satisfies), care rewards feeding own young.
        new GeneDefinition("m_energy", 0f, 2f, 1f, Chromosome: 4, Position: 0f),
        new GeneDefinition("m_water", 0f, 2f, 1f, Chromosome: 4, Position: 0.1f),
        new GeneDefinition("m_temperature", 0f, 0.5f, 0.08f, Chromosome: 4, Position: 0.2f),
        new GeneDefinition("m_integrity", 0f, 3f, 1.25f, Chromosome: 4, Position: 0.3f),
        new GeneDefinition("m_fatigue", 0f, 1f, 0.35f, Chromosome: 4, Position: 0.4f),
        new GeneDefinition("m_libido", 0f, 2f, 1f, Chromosome: 4, Position: 0.5f),
        new GeneDefinition("m_pain", 0f, 1.5f, 0.5f, Chromosome: 4, Position: 0.6f),
        new GeneDefinition("m_fear", 0f, 1f, 0.3f, Chromosome: 4, Position: 0.7f),
        new GeneDefinition("m_care", 0f, 2f, 0.5f, Chromosome: 4, Position: 0.8f),
        // Diet (T2.5): 0 herbivore … 1 carnivore; digestion of plants falls as meat digestion rises.
        new GeneDefinition("carnivory", 0f, 1f, 0f, Chromosome: 10, Position: 0f),
        // Reproduction (T2.2c): litter size, gestation in days, breeding season peak (year phase) and width (1 = all year).
        new GeneDefinition("litter_size", 1f, 6f, 2f, Chromosome: 11, Position: 0f),
        new GeneDefinition("gestation_days", 0.1f, 4f, 0.5f, Chromosome: 11, Position: 0.3f),
        new GeneDefinition("breeding_peak", 0f, 1f, 0.4f, Chromosome: 11, Position: 0.6f),
        new GeneDefinition("breeding_width", 0.1f, 1f, 1f, Chromosome: 11, Position: 0.8f),
        // Torpor (T2.2c): asleep in the cold the metabolism drops by up to 60 %; dormant at 0, upkeep when present.
        new GeneDefinition("torpor", 0f, 1f, 0f, Chromosome: 10, Position: 0.5f),
        // Body parts (T2.2b). Dormant parts start at 0 and only work above BodyPlan.FunctionalThreshold.
        new GeneDefinition("claws", 0f, 1f, 0f, Chromosome: 5, Position: 0f),
        new GeneDefinition("carapace", 0f, 1f, 0f, Chromosome: 5, Position: 0.3f),
        new GeneDefinition("horns", 0f, 1f, 0f, Chromosome: 5, Position: 0.6f),
        new GeneDefinition("fins", 0f, 1f, 0f, Chromosome: 6, Position: 0f),
        new GeneDefinition("teeth", 0f, 1f, 0.2f, Chromosome: 6, Position: 0.4f),
        // Sexual dimorphism: males are (1 + bias) times the female size.
        new GeneDefinition("male_size_bias", -0.3f, 0.5f, 0f, Chromosome: 7, Position: 0f),
        // Sense acuity (T2.2d): defaults reproduce the etapa-1 senses exactly (1 = etapa-1 value).
        new GeneDefinition("vision_range", 0.4f, 2f, 1f, Chromosome: 8, Position: 0f),
        new GeneDefinition("vision_fov", 0.5f, 1.6f, 1f, Chromosome: 8, Position: 0.3f),
        new GeneDefinition("night_vision", 0f, 1f, 0f, Chromosome: 8, Position: 0.6f),
        new GeneDefinition("smell", 0.3f, 3f, 1f, Chromosome: 9, Position: 0f),
        new GeneDefinition("hearing", 0.3f, 3f, 1f, Chromosome: 9, Position: 0.4f),
    });
}
