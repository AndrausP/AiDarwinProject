namespace CreatureIA.Core.Voxel;

/// <summary>Block identifiers. Stored as one byte per block; 0 is always empty space.</summary>
public enum BlockType : byte
{
    Air = 0,
    Grass = 1,
    Dirt = 2,
    Sand = 3,
    Stone = 4,
    Snow = 5,
    Water = 6,
    Wood = 7,
    Leaves = 8,
    Bedrock = 9,
    PineLeaves = 10,
    BirchWood = 11,
    BirchLeaves = 12,
    RedWood = 13,
    Cactus = 14,
    TallGrass = 15,
    FlowerYellow = 16,
    FlowerRed = 17,
    AcaciaLeaves = 18,
    /// <summary>White floor/walls of the showcase room.</summary>
    Marble = 19,
    /// <summary>Sea water (etapa 2 island): swimmable and visible like water, but never quenches thirst.</summary>
    SaltWater = 20
}

public static class BlockTypes
{
    public const int Count = 21;

    /// <summary>Fresh or salt water: not solid, buoyant, drawn as a transparent surface.</summary>
    public static bool IsLiquid(BlockType block) => block is BlockType.Water or BlockType.SaltWater;

    /// <summary>Small plants drawn as crossed quads: no collision, invisible to vision rays.</summary>
    public static bool IsDecoration(BlockType block) =>
        block is BlockType.TallGrass or BlockType.FlowerYellow or BlockType.FlowerRed;

    public static bool IsTrunk(BlockType block) => block is BlockType.Wood or BlockType.BirchWood or BlockType.RedWood;

    public static bool IsFoliage(BlockType block) =>
        block is BlockType.Leaves or BlockType.PineLeaves or BlockType.BirchLeaves or BlockType.AcaciaLeaves;

    /// <summary>Part of a tree or cactus (spawning never lands on these).</summary>
    public static bool IsTreePart(BlockType block) => IsTrunk(block) || IsFoliage(block) || block == BlockType.Cactus;

    /// <summary>Blocks that stop movement (water, air and decorations do not).</summary>
    public static bool IsSolid(BlockType block) =>
        block is not (BlockType.Air or BlockType.Water or BlockType.SaltWater) && !IsDecoration(block);

    /// <summary>Blocks a vision ray stops at. Water is visible (the animal sees the lake surface).</summary>
    public static bool IsOpaqueToVision(BlockType block) => block != BlockType.Air && !IsDecoration(block);

    /// <summary>Removable by digging. Bedrock (world floor) and water are not.</summary>
    public static bool IsDiggable(BlockType block) => IsSolid(block) && block != BlockType.Bedrock;

    /// <summary>Base albedo, display only (linear-ish sRGB, 0..1).</summary>
    public static (float R, float G, float B) Color(BlockType block) => block switch
    {
        BlockType.Grass => (0.30f, 0.58f, 0.24f),
        BlockType.Dirt => (0.47f, 0.34f, 0.22f),
        BlockType.Sand => (0.86f, 0.79f, 0.55f),
        BlockType.Stone => (0.52f, 0.53f, 0.55f),
        BlockType.Snow => (0.93f, 0.95f, 0.98f),
        BlockType.Water => (0.22f, 0.45f, 0.78f),
        BlockType.Wood => (0.42f, 0.29f, 0.17f),
        BlockType.Leaves => (0.19f, 0.45f, 0.17f),
        BlockType.Bedrock => (0.20f, 0.20f, 0.22f),
        BlockType.PineLeaves => (0.10f, 0.30f, 0.19f),
        BlockType.BirchWood => (0.86f, 0.84f, 0.78f),
        BlockType.BirchLeaves => (0.45f, 0.64f, 0.22f),
        BlockType.RedWood => (0.55f, 0.25f, 0.14f),
        BlockType.Cactus => (0.26f, 0.55f, 0.24f),
        BlockType.TallGrass => (0.38f, 0.66f, 0.26f),
        BlockType.FlowerYellow => (0.96f, 0.84f, 0.18f),
        BlockType.FlowerRed => (0.86f, 0.18f, 0.20f),
        BlockType.AcaciaLeaves => (0.47f, 0.55f, 0.16f),
        BlockType.Marble => (0.95f, 0.95f, 0.96f),
        BlockType.SaltWater => (0.12f, 0.33f, 0.62f),
        _ => (0f, 0f, 0f)
    };
}
