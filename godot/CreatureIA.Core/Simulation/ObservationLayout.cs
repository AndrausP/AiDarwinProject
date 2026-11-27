using CreatureIA.Core.Voxel;

namespace CreatureIA.Core.Simulation;

/// <summary>Observation v2 (Task 002 etapa 1): 1000 floats. See contracts/protocol-v2.json.</summary>
public static class ObservationLayout
{
    public const int HorizontalRays = 24;
    public const int VerticalLevels = 3;
    public const int Rays = HorizontalRays * VerticalLevels;
    public const int FeaturesPerRay = 13;
    public const int VisionOffset = 0;
    public const int VisionLength = Rays * FeaturesPerRay; // 936
    /// <summary>4 sectors × (r, g, b, relative size, carnivore) of creatures seen on the eye-level row.</summary>
    public const int CreatureSectorOffset = 936;
    public const int CreatureSectorLength = 20;
    public const int AudioOffset = 956;
    public const int AudioLength = 8;
    /// <summary>Gradients (body, nose − body, right − body) of plants, water, creatures, threat.</summary>
    public const int OlfactionOffset = 964;
    public const int OlfactionLength = 12;
    public const int ProprioceptionOffset = 976;
    public const int ProprioceptionLength = 10;
    public const int InteroceptionOffset = 986;
    public const int InteroceptionLength = 12;
    public const int TimeOffset = 998;
    public const int TimeLength = 2;
    public const int Total = 1000;
    public const int ActionCount = 12;
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
    public const int Count = 12;

    public static int OfBlock(BlockType block) => block switch
    {
        BlockType.Grass or BlockType.Dirt => Soil,
        BlockType.Sand => Sand,
        BlockType.Stone or BlockType.Bedrock or BlockType.Marble => Stone,
        BlockType.Snow => Snow,
        BlockType.Water => Water,
        BlockType.Wood or BlockType.BirchWood or BlockType.RedWood => Wood,
        // Cactus reads as foliage: a green plant body (its own class would change the protocol).
        BlockType.Leaves or BlockType.PineLeaves or BlockType.BirchLeaves or BlockType.AcaciaLeaves or BlockType.Cactus => Leaves,
        _ => None
    };
}
