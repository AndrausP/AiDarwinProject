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
    /// <summary>Lakes smaller than this are shallow and dry out in the height of summer (T2.1c).</summary>
    public const float ShallowLakeRadiusMeters = 9f;
    /// <summary>Photoperiod above which shallow lakes are dry (late summer).</summary>
    public const float DroughtPhotoperiod = 0.85f;
    private const float SaltCellMeters = 8f;

    public VoxelWorld World { get; }
    public VoxelGrid Grid => World.Grid;
    public float MapSize => World.Settings.WorldSizeX;
    public bool HasSea { get; }
    /// <summary>True while the shallow lakes are dry (set from the season by the world).</summary>
    public bool Drought { get; private set; }
    private readonly (float X, float Z, float Radius, bool Shallow)[] _lakes;
    private readonly Vector2[] _riverPoints;
    // Nearest sea point per coarse cell (feature transform), NaN where no sea exists: continuous distance.
    private readonly Vector2[] _nearestSea = Array.Empty<Vector2>();
    private readonly int _seaCells;

    public SimTerrain(VoxelWorld world)
    {
        World = world;
        _lakes = world.Lakes.Select(l => (l.CenterX, l.CenterZ, l.RadiusMeters, l.RadiusMeters < ShallowLakeRadiusMeters)).ToArray();
        var grid = world.Grid;
        _riverPoints = world.Rivers.SelectMany(r => r.Where((_, i) => i % 6 == 0))
            .Select(c => new Vector2(grid.OriginX + (c.X + 0.5f) * grid.BlockSize, grid.OriginZ + (c.Z + 0.5f) * grid.BlockSize))
            .ToArray();
        if (world.Settings.Island)
        {
            _seaCells = Math.Max(1, (int)MathF.Ceiling(MapSize / SaltCellMeters));
            _nearestSea = BuildSeaField(world, _seaCells);
            HasSea = true;
        }
    }

    /// <summary>Breadth-first feature transform: every cell learns the nearest salt-water surface point.</summary>
    private static Vector2[] BuildSeaField(VoxelWorld world, int cells)
    {
        var grid = world.Grid;
        var field = new Vector2[cells * cells];
        Array.Fill(field, new Vector2(float.NaN));
        var queue = new Queue<int>();
        var half = world.Settings.WorldSizeX * 0.5f;
        for (var cz = 0; cz < cells; cz++)
        for (var cx = 0; cx < cells; cx++)
        {
            var wx = -half + (cx + 0.5f) * SaltCellMeters;
            var wz = -half + (cz + 0.5f) * SaltCellMeters;
            var (bx, _, bz) = grid.WorldToBlock(wx, 0f, wz);
            var top = grid.TopBlockY(bx, bz, solidOnly: false);
            if (grid.Get(bx, top, bz) != BlockType.SaltWater) continue;
            field[cz * cells + cx] = new Vector2(wx, wz);
            queue.Enqueue(cz * cells + cx);
        }
        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            var (cx, cz) = (index % cells, index / cells);
            var source = field[index];
            foreach (var (nx, nz) in new[] { (cx + 1, cz), (cx - 1, cz), (cx, cz + 1), (cx, cz - 1) })
            {
                if (nx < 0 || nz < 0 || nx >= cells || nz >= cells) continue;
                var n = nz * cells + nx;
                var centre = new Vector2(-half + (nx + 0.5f) * SaltCellMeters, -half + (nz + 0.5f) * SaltCellMeters);
                if (!float.IsNaN(field[n].X) && Vector2.DistanceSquared(field[n], centre) <= Vector2.DistanceSquared(source, centre)) continue;
                field[n] = source;
                queue.Enqueue(n);
            }
        }
        return field;
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
    /// <summary>A dry land point within <paramref name="radius"/> of (cx, cz); falls back to the centre.</summary>
    public Vector3 RandomLandPointAround(SimRandom rng, float cx, float cz, float radius)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var x = cx + rng.Range(-radius, radius);
            var z = cz + rng.Range(-radius, radius);
            var sample = Sample(x, z);
            if (sample.WaterDepth > 0f) continue;
            var (bx, _, bz) = Grid.WorldToBlock(x, 0f, z);
            var top = Grid.TopBlockY(bx, bz, solidOnly: true);
            if (BlockTypes.IsTreePart(Grid.Get(bx, top, bz))) continue;
            return new Vector3(x, sample.Height, z);
        }
        return new Vector3(cx, Sample(cx, cz).Height, cz);
    }

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

    /// <summary>Season update: shallow lakes dry in late summer and refill afterwards.</summary>
    public void SetSeason(float photoperiod) => Drought = photoperiod >= DroughtPhotoperiod;

    private bool InDryLake(Vector3 position)
    {
        if (!Drought) return false;
        foreach (var (x, z, radius, shallow) in _lakes)
        {
            if (!shallow) continue;
            var dx = position.X - x;
            var dz = position.Z - z;
            if (dx * dx + dz * dz <= (radius + 3f) * (radius + 3f)) return true;
        }
        return false;
    }

    /// <summary>Fresh water in reach (salt water never quenches thirst; dry shallow lakes give nothing).</summary>
    public bool IsNearDrinkableWater(Vector3 position) =>
        World.IsNearWater(position.X, position.Y, position.Z, DrinkReachMeters) && !InDryLake(position);

    /// <summary>Fresh-water scent: lakes (by edge distance) and river reaches, Σ 1/(1+d²).</summary>
    public float WaterScent(Vector3 position)
    {
        var scent = 0f;
        foreach (var (x, z, radius, shallow) in _lakes)
        {
            if (shallow && Drought) continue;
            var dx = position.X - x;
            var dz = position.Z - z;
            var edgeDistance = MathF.Max(0f, MathF.Sqrt(dx * dx + dz * dz) - radius);
            scent += 1f / (1f + edgeDistance * edgeDistance);
        }
        var p = new Vector2(position.X, position.Z);
        foreach (var point in _riverPoints)
        {
            var d2 = Vector2.DistanceSquared(p, point);
            if (d2 < 3600f) scent += 0.3f / (1f + d2);
        }
        return scent;
    }

    /// <summary>Sea scent from the nearest salt-water surface (0 without a sea).</summary>
    public float SaltScent(Vector3 position)
    {
        if (!HasSea) return 0f;
        var half = MapSize * 0.5f;
        var cx = Math.Clamp((int)((position.X + half) / SaltCellMeters), 0, _seaCells - 1);
        var cz = Math.Clamp((int)((position.Z + half) / SaltCellMeters), 0, _seaCells - 1);
        var nearest = _nearestSea[cz * _seaCells + cx];
        if (float.IsNaN(nearest.X)) return 0f;
        var d2 = Vector2.DistanceSquared(new Vector2(position.X, position.Z), nearest);
        return 1f / (1f + d2);
    }
}
