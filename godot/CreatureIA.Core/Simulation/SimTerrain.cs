using System.Numerics;
using CreatureIA.Core.Voxel;

namespace CreatureIA.Core.Simulation;

public readonly record struct BiomeSample(
    float Height,
    float TemperatureCelsius,
    float Humidity,
    float VegetationDensity,
    float WaterDepth);

/// <summary>Climate, spawn-point and water queries over a <see cref="VoxelWorld"/> (Godot BiomeMap + WaterBodies).</summary>
public sealed class SimTerrain
{
    /// <summary>Horizontal reach of the muzzle for drinking, metres (3 blocks).</summary>
    public const float DrinkReachMeters = 1.5f;

    public VoxelWorld World { get; }
    public VoxelGrid Grid => World.Grid;
    public float MapSize => World.Settings.WorldSizeX;
    private readonly (float X, float Z, float Radius)[] _lakes;

    public SimTerrain(VoxelWorld world)
    {
        World = world;
        _lakes = world.Lakes.Select(l => (l.CenterX, l.CenterZ, l.RadiusMeters)).ToArray();
    }

    /// <summary>Surface (top of the highest solid block), climate and water depth at a world XZ.</summary>
    public BiomeSample Sample(float worldX, float worldZ)
    {
        var half = MapSize / 2f - 0.01f;
        var x = Math.Clamp(worldX, -half, half);
        var z = Math.Clamp(worldZ, -half, half);
        var height = Grid.SurfaceHeightAt(x, z);
        var climate = World.Climate(x, z);
        var waterDepth = MathF.Max(0f, World.WaterSurfaceY - height);
        var temperature = World.AmbientTemperature(x, height, z);
        return new BiomeSample(height, temperature, climate.Humidity, waterDepth > 0f ? 0f : climate.VegetationDensity, waterDepth);
    }

    /// <summary>A random dry, walkable spot (not water, not a tree top) inside <paramref name="extent"/> of the centre.</summary>
    public Vector3 RandomLandPoint(SimRandom rng, float extent, Func<BiomeSample, bool>? accept = null)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var x = rng.Range(-extent, extent);
            var z = rng.Range(-extent, extent);
            var sample = Sample(x, z);
            if (sample.WaterDepth > 0f) continue;
            var (bx, _, bz) = Grid.WorldToBlock(x, 0f, z);
            var top = Grid.TopBlockY(bx, bz, solidOnly: true);
            if (BlockTypes.IsTreePart(Grid.Get(bx, top, bz))) continue;
            if (accept is not null && !accept(sample)) continue;
            return new Vector3(x, sample.Height, z);
        }
        return new Vector3(0f, Sample(0f, 0f).Height, 0f);
    }

    public bool IsNearDrinkableWater(Vector3 position) =>
        World.IsNearWater(position.X, position.Y, position.Z, DrinkReachMeters);

    public float WaterScent(Vector3 position)
    {
        var scent = 0f;
        foreach (var (x, z, radius) in _lakes)
        {
            var dx = position.X - x;
            var dz = position.Z - z;
            var edgeDistance = MathF.Max(0f, MathF.Sqrt(dx * dx + dz * dz) - radius);
            scent += 1f / (1f + edgeDistance * edgeDistance);
        }
        return scent;
    }
}
