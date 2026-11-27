namespace CreatureIA.Core.Voxel;

public sealed record VoxelWorldSettings
{
    /// <summary>Plan: 0.5 m blocks, 384 × 384 m, 48 m tall.</summary>
    public float BlockSize { get; init; } = 0.5f;
    public int SizeX { get; init; } = 768;
    public int SizeY { get; init; } = 96;
    public int SizeZ { get; init; } = 768;
    /// <summary>Blocks at or below this level that are not ground become water.</summary>
    public int WaterLevel { get; init; } = 24;
    /// <summary>Explicit lake basins carved in addition to low noise terrain (guarantees drinkable water).</summary>
    public int LakeCount { get; init; } = 6;
    public float TreeDensity { get; init; } = 1f;
    /// <summary>Showcase room: flat white floor, fixed mild climate, examples laid out side by side.</summary>
    public bool Showcase { get; init; }
    /// <summary>Etapa 2 island (T2.1a): salt ocean all around (no wall), mountain range, rivers to the sea.</summary>
    public bool Island { get; init; }
    public int RiverCount { get; init; } = 6;

    public float WorldSizeX => SizeX * BlockSize;
    public float WorldSizeZ => SizeZ * BlockSize;
}

public readonly record struct ClimateSample(float TemperatureCelsius, float Humidity, float VegetationDensity);

public readonly record struct LakeBasin(float CenterX, float CenterZ, float RadiusMeters);

/// <summary>A generated world: block grid plus the climate field the physiology and plants read.</summary>
public sealed class VoxelWorld
{
    private readonly PerlinNoise _temperature;
    private readonly PerlinNoise _humidity;

    internal VoxelWorld(VoxelGrid grid, VoxelWorldSettings settings, int seed, IReadOnlyList<LakeBasin> lakes)
    {
        Grid = grid;
        Settings = settings;
        Seed = seed;
        Lakes = lakes;
        _temperature = new PerlinNoise(seed + 101, 0.0035f, 3);
        _humidity = new PerlinNoise(seed + 211, 0.0055f, 3);
    }

    public VoxelGrid Grid { get; }
    public VoxelWorldSettings Settings { get; }
    public int Seed { get; }
    public IReadOnlyList<LakeBasin> Lakes { get; }
    /// <summary>Island rivers as block columns from source to mouth (4-connected). Empty on the etapa-1 map.</summary>
    public IReadOnlyList<IReadOnlyList<(int X, int Z)>> Rivers { get; internal set; } = Array.Empty<IReadOnlyList<(int X, int Z)>>();
    public float WaterSurfaceY => Grid.OriginY + (Settings.WaterLevel + 1) * Grid.BlockSize;

    /// <summary>Name tags for the showcase room (text, world position). Empty in normal worlds.</summary>
    public List<(string Text, float X, float Y, float Z)> Labels { get; } = new();

    /// <summary>Sea-level climate at a world XZ: warm equator band at z = 0, colder towards ±Z edges.</summary>
    public ClimateSample Climate(float wx, float wz)
    {
        if (Settings.Showcase) return new ClimateSample(22f, 0.6f, 0.6f);
        var halfZ = Settings.WorldSizeZ * 0.5f;
        var latitude = Math.Clamp(MathF.Abs(wz) / halfZ, 0f, 1f);
        var temperature = 29f - 24f * latitude + _temperature.Sample(wx, wz) * 7f;
        var humidity = Math.Clamp((_humidity.Sample(wx, wz) + 1f) * 0.5f, 0f, 1f);
        var vegetation = Math.Clamp(humidity * (1f - MathF.Abs(temperature - 20f) / 30f), 0f, 1f);
        return new ClimateSample(temperature, humidity, vegetation);
    }

    /// <summary>Ambient temperature at a 3D point: sea-level climate minus 0.15 °C per metre of altitude.</summary>
    public float AmbientTemperature(float wx, float wy, float wz) =>
        Climate(wx, wz).TemperatureCelsius - MathF.Max(0f, wy - WaterSurfaceY) * AltitudeLapseCelsiusPerMeter;

    public const float AltitudeLapseCelsiusPerMeter = 0.15f;

    /// <summary>True when a water block lies within <paramref name="radiusMeters"/> horizontally, at or just below the feet.</summary>
    public bool IsNearWater(float wx, float wy, float wz, float radiusMeters)
    {
        var grid = Grid;
        var (bx, by, bz) = grid.WorldToBlock(wx, wy, wz);
        var r = (int)MathF.Ceiling(radiusMeters / grid.BlockSize);
        var r2 = radiusMeters * radiusMeters;
        for (var y = by - 2; y <= by + 1; y++)
        for (var z = bz - r; z <= bz + r; z++)
        for (var x = bx - r; x <= bx + r; x++)
        {
            if (grid.Get(x, y, z) != BlockType.Water) continue;
            var cx = grid.OriginX + (x + 0.5f) * grid.BlockSize - wx;
            var cz = grid.OriginZ + (z + 0.5f) * grid.BlockSize - wz;
            if (cx * cx + cz * cz <= r2) return true;
        }
        return false;
    }
}

public static class VoxelWorldGenerator
{
    public static VoxelWorld Generate(int seed, VoxelWorldSettings? settings = null)
    {
        settings ??= new VoxelWorldSettings();
        if (settings.Island) return IslandGenerator.Generate(seed, settings);
        var grid = new VoxelGrid(settings.SizeX, settings.SizeY, settings.SizeZ, settings.BlockSize,
            -settings.WorldSizeX * 0.5f, 0f, -settings.WorldSizeZ * 0.5f);
        var lakes = PlaceLakes(seed, settings);
        var world = new VoxelWorld(grid, settings, seed, lakes);
        var heights = BuildHeights(seed, settings, grid, lakes);
        FillColumns(world, heights);
        PlantTrees(world, heights, seed);
        return world;
    }

    private static List<LakeBasin> PlaceLakes(int seed, VoxelWorldSettings settings)
    {
        var lakes = new List<LakeBasin>();
        var state = (ulong)(uint)seed * 0xD1B54A32D192ED03UL + 7;
        var halfX = settings.WorldSizeX * 0.5f;
        var halfZ = settings.WorldSizeZ * 0.5f;
        var attempts = 0;
        while (lakes.Count < settings.LakeCount && attempts++ < settings.LakeCount * 40)
        {
            var x = (Next01(ref state) * 2f - 1f) * halfX * 0.8f;
            var z = (Next01(ref state) * 2f - 1f) * halfZ * 0.8f;
            var radius = 7f + Next01(ref state) * 9f;
            // Keep basins apart so each is a distinct landmark.
            if (lakes.Any(l => MathF.Sqrt((l.CenterX - x) * (l.CenterX - x) + (l.CenterZ - z) * (l.CenterZ - z)) < l.RadiusMeters + radius + 30f))
                continue;
            lakes.Add(new LakeBasin(x, z, radius));
        }
        return lakes;
    }

    private static int[] BuildHeights(int seed, VoxelWorldSettings s, VoxelGrid grid, IReadOnlyList<LakeBasin> lakes)
    {
        var baseNoise = new PerlinNoise(seed, 0.006f, 4);
        var hillNoise = new PerlinNoise(seed + 37, 0.018f, 3);
        var mountainNoise = new PerlinNoise(seed + 73, 0.004f, 3);
        var heights = new int[s.SizeX * s.SizeZ];
        var maxHeight = s.SizeY - 12; // room for trees on top
        for (var z = 0; z < s.SizeZ; z++)
        for (var x = 0; x < s.SizeX; x++)
        {
            var wx = grid.OriginX + (x + 0.5f) * s.BlockSize;
            var wz = grid.OriginZ + (z + 0.5f) * s.BlockSize;
            var mountain = MathF.Max(0f, mountainNoise.Sample(wx, wz) - 0.25f);
            var h = s.WaterLevel + 6f + baseNoise.Sample(wx, wz) * 9f + hillNoise.Sample(wx, wz) * 3f + mountain * mountain * 110f;
            foreach (var lake in lakes)
            {
                var dx = wx - lake.CenterX;
                var dz = wz - lake.CenterZ;
                var d = MathF.Sqrt(dx * dx + dz * dz) / lake.RadiusMeters;
                if (d >= 1.6f) continue;
                // Bowl: bottom 4 blocks under water at the centre, rising smoothly to the terrain.
                var t = Math.Clamp(d / 1.6f, 0f, 1f);
                var bowl = s.WaterLevel - 4f + (h - (s.WaterLevel - 4f)) * (t * t * (3f - 2f * t));
                h = MathF.Min(h, d < 1f ? MathF.Min(bowl, s.WaterLevel - 1f + d) : bowl);
            }
            heights[z * s.SizeX + x] = Math.Clamp((int)MathF.Round(h), 2, maxHeight);
        }
        return heights;
    }

    private static void FillColumns(VoxelWorld world, int[] heights)
    {
        var grid = world.Grid;
        var s = world.Settings;
        for (var z = 0; z < s.SizeZ; z++)
        for (var x = 0; x < s.SizeX; x++)
        {
            var top = heights[z * s.SizeX + x];
            var wx = grid.OriginX + (x + 0.5f) * s.BlockSize;
            var wz = grid.OriginZ + (z + 0.5f) * s.BlockSize;
            var surface = SurfaceBlock(world, heights, x, z, top, wx, wz);
            var under = surface is BlockType.Sand ? BlockType.Sand : BlockType.Dirt;
            grid.SetRaw(x, 0, z, BlockType.Bedrock);
            for (var y = 1; y <= top; y++)
            {
                var block = y == top ? surface : y >= top - 3 ? under : BlockType.Stone;
                grid.SetRaw(x, y, z, block);
            }
            for (var y = top + 1; y <= s.WaterLevel; y++) grid.SetRaw(x, y, z, BlockType.Water);
        }
    }

    internal static BlockType SurfaceBlock(VoxelWorld world, int[] heights, int x, int z, int top, float wx, float wz)
    {
        var s = world.Settings;
        if (top <= s.WaterLevel) return top >= s.WaterLevel - 2 ? BlockType.Sand : BlockType.Dirt;
        var climate = world.Climate(wx, wz);
        var altitude = (top - s.WaterLevel) * s.BlockSize;
        var temperature = climate.TemperatureCelsius - altitude * VoxelWorld.AltitudeLapseCelsiusPerMeter;
        if (temperature < 1.5f) return BlockType.Snow;
        var slope = 0;
        if (x > 0) slope = Math.Max(slope, Math.Abs(heights[z * s.SizeX + x - 1] - top));
        if (x < s.SizeX - 1) slope = Math.Max(slope, Math.Abs(heights[z * s.SizeX + x + 1] - top));
        if (z > 0) slope = Math.Max(slope, Math.Abs(heights[(z - 1) * s.SizeX + x] - top));
        if (z < s.SizeZ - 1) slope = Math.Max(slope, Math.Abs(heights[(z + 1) * s.SizeX + x] - top));
        if (slope >= 3 || altitude > 24f) return BlockType.Stone;
        if (top <= s.WaterLevel + 1) return BlockType.Sand;
        if (climate.TemperatureCelsius > 25f && climate.Humidity < 0.35f) return BlockType.Sand;
        return BlockType.Grass;
    }

    internal static void PlantTrees(VoxelWorld world, int[] heights, int seed)
    {
        var grid = world.Grid;
        var s = world.Settings;
        var giants = new List<(int X, int Z)>();

        // Giants (sequoias): one candidate per 24 m, only in mild humid lowland forest.
        const int giantCell = 48;
        for (var cz = 0; cz < s.SizeZ / giantCell; cz++)
        for (var cx = 0; cx < s.SizeX / giantCell; cx++)
        {
            var x = cx * giantCell + 6 + (int)(PerlinNoise.Hash01(seed + 11, cx, cz) * (giantCell - 12));
            var z = cz * giantCell + 6 + (int)(PerlinNoise.Hash01(seed + 12, cx, cz) * (giantCell - 12));
            var top = heights[z * s.SizeX + x];
            if (grid.Get(x, top, z) != BlockType.Grass) continue;
            var (temperature, humidity, vegetation) = LocalClimate(world, x, z, top);
            if (temperature is < 9f or > 24f || humidity < 0.5f) continue;
            if (PerlinNoise.Hash01(seed + 13, cx, cz) >= 0.25f * s.TreeDensity + vegetation * 0.25f) continue;
            if (TreeBuilder.SequoiaHeight(grid, top, PerlinNoise.Hash01(seed + 14, cx, cz)) < 30) continue;
            TreeBuilder.Build(grid, TreeSpecies.Sequoia, x, top, z, PerlinNoise.Hash01(seed + 14, cx, cz));
            giants.Add((x, z));
        }

        // Ordinary trees: one candidate per 5 m × 5 m; species from the local climate.
        const int cell = 10;
        for (var cz = 0; cz < s.SizeZ / cell; cz++)
        for (var cx = 0; cx < s.SizeX / cell; cx++)
        {
            var x = cx * cell + 2 + (int)(PerlinNoise.Hash01(seed + 1, cx, cz) * (cell - 4));
            var z = cz * cell + 2 + (int)(PerlinNoise.Hash01(seed + 2, cx, cz) * (cell - 4));
            var top = heights[z * s.SizeX + x];
            var surface = grid.Get(x, top, z);
            if (surface is not (BlockType.Grass or BlockType.Snow)) continue;
            if (giants.Any(g => Math.Abs(g.X - x) < 9 && Math.Abs(g.Z - z) < 9)) continue;
            var (temperature, humidity, vegetation) = LocalClimate(world, x, z, top);
            var chance = vegetation * 0.55f * s.TreeDensity;
            if (temperature < 6f) chance = MathF.Max(chance, 0.3f * s.TreeDensity); // boreal forest on cold slopes
            if (PerlinNoise.Hash01(seed + 3, cx, cz) >= chance) continue;
            var pick = PerlinNoise.Hash01(seed + 6, cx, cz);
            TreeSpecies species;
            if (temperature < 9f) species = TreeSpecies.Spruce;
            else if (surface == BlockType.Snow) continue;
            else if (temperature > 24f && humidity < 0.45f) species = TreeSpecies.Acacia;
            else if (temperature < 15f) species = pick < 0.45f ? TreeSpecies.Spruce : pick < 0.75f ? TreeSpecies.Birch : TreeSpecies.Oak;
            else species = pick < 0.12f ? TreeSpecies.BigOak : pick < 0.4f ? TreeSpecies.Birch : TreeSpecies.Oak;
            TreeBuilder.Build(grid, species, x, top, z, PerlinNoise.Hash01(seed + 4, cx, cz));
        }

        // Cacti on hot dry sand.
        const int cactusCell = 8;
        for (var cz = 0; cz < s.SizeZ / cactusCell; cz++)
        for (var cx = 0; cx < s.SizeX / cactusCell; cx++)
        {
            var x = cx * cactusCell + 1 + (int)(PerlinNoise.Hash01(seed + 21, cx, cz) * (cactusCell - 2));
            var z = cz * cactusCell + 1 + (int)(PerlinNoise.Hash01(seed + 22, cx, cz) * (cactusCell - 2));
            var top = heights[z * s.SizeX + x];
            if (top <= s.WaterLevel + 1 || grid.Get(x, top, z) != BlockType.Sand || grid.Get(x, top + 1, z) != BlockType.Air) continue;
            var (temperature, humidity, _) = LocalClimate(world, x, z, top);
            if (temperature < 24f || humidity > 0.4f) continue;
            if (PerlinNoise.Hash01(seed + 23, cx, cz) >= 0.3f) continue;
            TreeBuilder.Build(grid, TreeSpecies.Cactus, x, top, z, PerlinNoise.Hash01(seed + 24, cx, cz));
        }

        // Shrubs: low leaf cover in vegetated land.
        const int shrubCell = 6;
        for (var cz = 0; cz < s.SizeZ / shrubCell; cz++)
        for (var cx = 0; cx < s.SizeX / shrubCell; cx++)
        {
            var x = cx * shrubCell + (int)(PerlinNoise.Hash01(seed + 31, cx, cz) * (shrubCell - 1));
            var z = cz * shrubCell + (int)(PerlinNoise.Hash01(seed + 32, cx, cz) * (shrubCell - 1));
            var top = heights[z * s.SizeX + x];
            if (grid.Get(x, top, z) != BlockType.Grass || grid.Get(x, top + 1, z) != BlockType.Air) continue;
            var (temperature, _, vegetation) = LocalClimate(world, x, z, top);
            if (PerlinNoise.Hash01(seed + 33, cx, cz) >= vegetation * 0.3f) continue;
            TreeBuilder.Shrub(grid, x, top, z, PerlinNoise.Hash01(seed + 34, cx, cz),
                temperature < 12f ? BlockType.PineLeaves : temperature > 24f ? BlockType.AcaciaLeaves : BlockType.Leaves);
        }

        // Tall grass and flowers: decoration only (no collision, invisible to vision).
        for (var z = 0; z < s.SizeZ; z++)
        for (var x = 0; x < s.SizeX; x++)
        {
            var top = heights[z * s.SizeX + x];
            if (grid.Get(x, top, z) != BlockType.Grass || grid.Get(x, top + 1, z) != BlockType.Air) continue;
            var roll = PerlinNoise.Hash01(seed + 41, x, z);
            var vegetation = world.Climate(grid.OriginX + (x + 0.5f) * s.BlockSize, grid.OriginZ + (z + 0.5f) * s.BlockSize).VegetationDensity;
            if (roll < 0.012f * vegetation) grid.SetRaw(x, top + 1, z, roll < 0.006f * vegetation ? BlockType.FlowerYellow : BlockType.FlowerRed);
            else if (roll < 0.11f * vegetation) grid.SetRaw(x, top + 1, z, BlockType.TallGrass);
        }
    }

    /// <summary>Temperature (with altitude lapse), humidity and vegetation at a block column.</summary>
    private static (float Temperature, float Humidity, float Vegetation) LocalClimate(VoxelWorld world, int x, int z, int top)
    {
        var grid = world.Grid;
        var s = world.Settings;
        var climate = world.Climate(grid.OriginX + (x + 0.5f) * s.BlockSize, grid.OriginZ + (z + 0.5f) * s.BlockSize);
        var altitude = MathF.Max(0f, (top - s.WaterLevel) * s.BlockSize);
        return (climate.TemperatureCelsius - altitude * VoxelWorld.AltitudeLapseCelsiusPerMeter, climate.Humidity, climate.VegetationDensity);
    }
    internal static float Next01(ref ulong state) => (PerlinNoise.SplitMix(ref state) >> 40) / (float)(1 << 24);
}
