using CreatureIA.Core.Voxel;
using Godot;

namespace CreatureIA.Godot.World;

public readonly record struct BiomeSample(
    float Height,
    float TemperatureCelsius,
    float Humidity,
    float VegetationDensity,
    float WaterDepth);

/// <summary>
/// Owner of the voxel world (Task 002, etapa 1). Generated in _Ready — first child of World, so every
/// other node can read it from its own _Ready. Climate queries keep the Task 001 API.
/// </summary>
public partial class BiomeMap : Node
{
    [Export] public int WorldSeed { get; set; } = 1337;
    [Export] public int LakeCount { get; set; } = 6;

    public VoxelWorld World { get; private set; } = null!;
    public VoxelGrid Grid => World.Grid;
    public float MapSize => World.Settings.WorldSizeX;

    public override void _Ready()
    {
        var started = Time.GetTicksMsec();
        World = RunSettings.Showcase
            ? ShowcaseGenerator.Generate(RunSettings.Seed(WorldSeed))
            : VoxelWorldGenerator.Generate(RunSettings.Seed(WorldSeed), RunSettings.Island
                ? new VoxelWorldSettings { Island = true, SizeX = 1536, SizeZ = 1536, SizeY = 128, WaterLevel = 32, LakeCount = 8, RiverCount = 10 }
                : new VoxelWorldSettings { LakeCount = RunSettings.Lakes(LakeCount) });
        GD.Print($"[voxel] world {World.Settings.WorldSizeX}x{World.Settings.WorldSizeZ} m generated in {Time.GetTicksMsec() - started} ms, lakes={World.Lakes.Count}");
    }

    /// <summary>Surface (top of the highest solid block), climate and water depth at a world XZ.</summary>
    public BiomeSample Sample(Vector2 worldPosition)
    {
        var half = MapSize / 2f - 0.01f;
        var x = Mathf.Clamp(worldPosition.X, -half, half);
        var z = Mathf.Clamp(worldPosition.Y, -half, half);
        var height = Grid.SurfaceHeightAt(x, z);
        var climate = World.Climate(x, z);
        var waterDepth = Mathf.Max(0f, World.WaterSurfaceY - height);
        var temperature = World.AmbientTemperature(x, height, z);
        return new BiomeSample(height, temperature, climate.Humidity, waterDepth > 0f ? 0f : climate.VegetationDensity, waterDepth);
    }

    /// <summary>A random dry, walkable spot (not water, not a tree top) inside <paramref name="extent"/> of the centre.</summary>
    public Vector3 RandomLandPoint(RandomNumberGenerator rng, float extent, Func<BiomeSample, bool>? accept = null)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var x = rng.RandfRange(-extent, extent);
            var z = rng.RandfRange(-extent, extent);
            var sample = Sample(new Vector2(x, z));
            if (sample.WaterDepth > 0f) continue;
            var (bx, _, bz) = Grid.WorldToBlock(x, 0f, z);
            var top = Grid.TopBlockY(bx, bz, solidOnly: true);
            if (BlockTypes.IsTreePart(Grid.Get(bx, top, bz))) continue;
            if (accept is not null && !accept(sample)) continue;
            return new Vector3(x, sample.Height, z);
        }
        return new Vector3(0f, Sample(Vector2.Zero).Height, 0f);
    }
}
