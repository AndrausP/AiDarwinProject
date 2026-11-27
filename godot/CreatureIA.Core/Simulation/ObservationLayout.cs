using CreatureIA.Core.Genetics;
using CreatureIA.Core.Voxel;

namespace CreatureIA.Core.Simulation;

/// <summary>
/// Observation v4 (Task 002 etapa 2, T2.3 + mating pheromone): 1133 floats. See contracts/protocol-v4.json and
/// docs/tasks/008-proposta-protocolo-v3.md (v4 = v3 + 3 olfaction floats). Shared by the Sim and the Godot runtime.
/// </summary>
public static class ObservationLayout
{
    public const int HorizontalRays = 24;
    public const int VerticalLevels = 3;
    public const int Rays = HorizontalRays * VerticalLevels;
    /// <summary>Distance + one-hot of <see cref="VisionClass.Count"/> classes.</summary>
    public const int FeaturesPerRay = 1 + VisionClass.Count; // 14
    public const int VisionOffset = 0;
    public const int VisionLength = Rays * FeaturesPerRay; // 1008
    /// <summary>4 sectors × (r, g, b, relative size, carnivore, kin, female, mature, juvenile), eye-level row.</summary>
    public const int SectorFeatures = 9;
    public const int CreatureSectorOffset = VisionOffset + VisionLength; // 1008
    public const int CreatureSectorLength = 4 * SectorFeatures; // 36
    public const int AudioOffset = CreatureSectorOffset + CreatureSectorLength; // 1044
    public const int AudioLength = 8;
    /// <summary>Scents (body, nose − body, right − body), log scale: plants, fresh water, salt water, creatures, threat,
    /// mating pheromone (v4: receptive females for males, fertile males for receptive females).</summary>
    public const int OlfactionOffset = AudioOffset + AudioLength; // 1052
    public const int OlfactionLength = 18;
    public const int SmellPlants = 0, SmellFreshWater = 3, SmellSaltWater = 6, SmellCreatures = 9, SmellThreat = 12, SmellMate = 15;
    public const int ProprioceptionOffset = OlfactionOffset + OlfactionLength; // 1070
    public const int ProprioceptionLength = 10;
    /// <summary>The 12 of v2 (energy … fear, same order: the reward reads these) + gestation, offspring in need.</summary>
    public const int InteroceptionOffset = ProprioceptionOffset + ProprioceptionLength; // 1080
    public const int InteroceptionLength = 14;
    /// <summary>sin/cos time of day, photoperiod, photoperiod trend, light now, climate temperature offset (see WriteTime).</summary>
    public const int TimeOffset = InteroceptionOffset + InteroceptionLength; // 1094
    public const int TimeLength = 6;
    /// <summary>Expressed body/life genes of <see cref="GeneCatalog.Basic"/> (all but the "m_" motivations), catalog order, each 0..1 by its range.</summary>
    public const int GenesOffset = TimeOffset + TimeLength; // 1100 (24 genes -> motivations at 1124, total 1133)
    public const int GenesLength = 24;
    /// <summary>The individual's reward weights (motivation genes, <see cref="GeneCatalog.MotivationOrder"/>), raw values.</summary>
    public const int MotivationsOffset = GenesOffset + GenesLength; // 1124
    public const int MotivationsLength = 9;
    public const int Total = MotivationsOffset + MotivationsLength; // 1133
    public const int ActionCount = 13;


    /// <summary>
    /// Scent intensity in log scale, 0..1: <c>log1p(k·s)/log1p(k)</c>. A linear <c>Σ 1/(1+d²)</c> is ~0.001 at 30 m
    /// and its nose−body difference vanished in noise (stage-1 water probe); in log scale it survives distance.
    /// </summary>
    public const float ScentLogGain = 10_000f;
    private static readonly float LogNorm = MathF.Log(1f + ScentLogGain);

    public static float LogScent(float scent) => Math.Clamp(MathF.Log(1f + ScentLogGain * MathF.Max(0f, scent)) / LogNorm, 0f, 1f);

    /// <summary>Writes (body, nose − body, right − body) in log scale; differences are scaled back to natural-log units.</summary>
    public static void WriteScent(Span<float> observation, int offset, float body, float nose, float right)
    {
        var b = LogScent(body);
        observation[offset] = b;
        observation[offset + 1] = Math.Clamp((LogScent(nose) - b) * LogNorm, -1f, 1f);
        observation[offset + 2] = Math.Clamp((LogScent(right) - b) * LogNorm, -1f, 1f);
    }

    /// <summary>
    /// Time block: sin/cos of the time of day, photoperiod (0 short winter days … 1 long summer days), its trend
    /// (&gt; 0 days lengthening), light level now (0..1) and the climate's temperature offset /20 °C.
    /// <paramref name="year01"/> follows <see cref="SimClock.YearProgress01"/> (0 = mid-winter).
    /// </summary>
    public static void WriteTime(Span<float> observation, float timeOfDay01, float year01, float light01, float temperatureOffsetCelsius)
    {
        var i = TimeOffset;
        observation[i] = MathF.Sin(timeOfDay01 * MathF.Tau);
        observation[i + 1] = MathF.Cos(timeOfDay01 * MathF.Tau);
        observation[i + 2] = (1f - MathF.Cos(year01 * MathF.Tau)) * 0.5f;
        observation[i + 3] = MathF.Sin(year01 * MathF.Tau);
        observation[i + 4] = Math.Clamp(light01, 0f, 1f);
        observation[i + 5] = Math.Clamp(temperatureOffsetCelsius / 20f, -1f, 1f);
    }

    private static readonly float[] DefaultGenesNormalised = BuildDefaultGenes();
    private static readonly float[] DefaultMotivations = BuildDefaultMotivations();

    private static float[] BuildDefaultGenes()
    {
        var catalog = Species.Catalog;
        var body = catalog.BodyGeneIndices();
        if (body.Length != GenesLength) throw new InvalidOperationException($"protocol v3 carries {GenesLength} genes, catalog has {body.Length}");
        return body.Select(i => (catalog.Genes[i].Default - catalog.Genes[i].Min) / catalog.Genes[i].Range).ToArray();
    }

    private static float[] BuildDefaultMotivations()
    {
        var catalog = Species.Catalog;
        var indices = catalog.MotivationIndices();
        if (indices.Length != MotivationsLength) throw new InvalidOperationException("motivation count mismatch");
        return indices.Select(i => catalog.Genes[i].Default).ToArray();
    }

    /// <summary>Default genome and motivations: the Godot runtime, which has no genomes yet (it only watches).</summary>
    public static void WriteDefaultGenesAndMotivations(Span<float> observation)
    {
        DefaultGenesNormalised.CopyTo(observation.Slice(GenesOffset, GenesLength));
        DefaultMotivations.CopyTo(observation.Slice(MotivationsOffset, MotivationsLength));
    }
}

/// <summary>Per-ray class one-hot index (after the distance feature).</summary>
public static class VisionClass
{
    public const int None = 0;
    public const int Soil = 1;
    public const int Sand = 2;
    public const int Stone = 3;
    public const int Snow = 4;
    public const int Water = 5;
    public const int Wood = 6;
    public const int Leaves = 7;
    public const int Plant = 8;
    public const int Thorny = 9;
    public const int Meat = 10;
    public const int Creature = 11;
    /// <summary>v3: the sea is visible as itself (it does not quench thirst).</summary>
    public const int SaltWater = 12;
    public const int Count = 13;

    public static int OfBlock(BlockType block) => block switch
    {
        BlockType.Grass or BlockType.Dirt => Soil,
        BlockType.Sand => Sand,
        BlockType.Stone or BlockType.Bedrock or BlockType.Marble => Stone,
        BlockType.Snow => Snow,
        BlockType.Water => Water,
        BlockType.SaltWater => SaltWater,
        BlockType.Wood or BlockType.BirchWood or BlockType.RedWood => Wood,
        // Cactus reads as foliage: a green plant body.
        BlockType.Leaves or BlockType.PineLeaves or BlockType.BirchLeaves or BlockType.AcaciaLeaves or BlockType.Cactus => Leaves,
        _ => None
    };
}
