namespace CreatureIA.Core.Voxel;

/// <summary>
/// Etapa-2 island (T2.1a): one land mass in a salt ocean that reaches every map edge (no wall), a
/// mountain range along a random axis, beaches, rivers from high sources down to the sea, fresh
/// lakes inland and biomes from the regional climate. Deterministic per seed.
/// </summary>
public static class IslandGenerator
{
    public static VoxelWorld Generate(int seed, VoxelWorldSettings s)
    {
        var grid = new VoxelGrid(s.SizeX, s.SizeY, s.SizeZ, s.BlockSize, -s.WorldSizeX * 0.5f, 0f, -s.WorldSizeZ * 0.5f);
        var heights = BuildHeights(seed, s, grid);
        var lakes = CarveLakes(seed, s, grid, heights);
        var world = new VoxelWorld(grid, s, seed, lakes);
        var ocean = OceanColumns(heights, s);
        var filled = PriorityFlood(heights, s, ocean);
        var rivers = TraceRivers(seed, s, heights, filled, ocean);
        FillColumns(world, heights, ocean);
        world.Rivers = rivers.Select(r => (IReadOnlyList<(int X, int Z)>)r.Path).ToArray();
        foreach (var river in rivers) FillRiver(grid, s, river);
        VoxelWorldGenerator.PlantTrees(world, heights, seed);
        return world;
    }

    private sealed record River(List<(int X, int Z)> Path, int[] WaterTop);

    /// <summary>Terrain height (top solid block) per column.</summary>
    private static int[] BuildHeights(int seed, VoxelWorldSettings s, VoxelGrid grid)
    {
        var coast = new PerlinNoise(seed + 501, 0.012f, 3);
        var baseNoise = new PerlinNoise(seed + 502, 0.008f, 4);
        var hill = new PerlinNoise(seed + 503, 0.03f, 3);
        var ridgeNoise = new PerlinNoise(seed + 504, 0.02f, 3);
        var state = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL + 99;
        var angle = VoxelWorldGenerator.Next01(ref state) * MathF.PI;
        var (ax, az) = (MathF.Cos(angle), MathF.Sin(angle));
        var half = MathF.Min(s.WorldSizeX, s.WorldSizeZ) * 0.5f;
        var peak = (s.SizeY - 14 - s.WaterLevel) * 0.85f;     // tallest ridge, in blocks above sea level
        var heights = new int[s.SizeX * s.SizeZ];
        for (var z = 0; z < s.SizeZ; z++)
        for (var x = 0; x < s.SizeX; x++)
        {
            var wx = grid.OriginX + (x + 0.5f) * s.BlockSize;
            var wz = grid.OriginZ + (z + 0.5f) * s.BlockSize;
            var r = MathF.Sqrt(wx * wx + wz * wz) / half + coast.Sample(wx, wz) * 0.12f;
            var land = SmoothStep(0.86f, 0.62f, r);                  // 1 inland, 0 open sea
            // Mountain range: a ridge along the axis through the centre, fading towards the coast.
            var across = MathF.Abs(-az * wx + ax * wz) / half;        // distance to the axis, in island radii
            var along = MathF.Abs(ax * wx + az * wz) / half;
            var ridge = MathF.Exp(-across * across / 0.02f) * SmoothStep(0.75f, 0.2f, along)
                      * (0.65f + 0.35f * MathF.Abs(ridgeNoise.Sample(wx, wz)));
            var landHeight = 3f + (baseNoise.Sample(wx, wz) + 1f) * 4f + hill.Sample(wx, wz) * 2f + ridge * peak * land;
            var seaFloor = -2f - (1f - land) * 10f;
            var h = s.WaterLevel + seaFloor + (landHeight - seaFloor) * land;
            heights[z * s.SizeX + x] = Math.Clamp((int)MathF.Round(h), 2, s.SizeY - 14);
        }
        // Map edges are always sea (no wall to lean on).
        for (var i = 0; i < s.SizeX; i++) { Sink(heights, s, i, 0); Sink(heights, s, i, s.SizeZ - 1); }
        for (var i = 0; i < s.SizeZ; i++) { Sink(heights, s, 0, i); Sink(heights, s, s.SizeX - 1, i); }
        return heights;
    }

    private static void Sink(int[] heights, VoxelWorldSettings s, int x, int z) =>
        heights[z * s.SizeX + x] = Math.Min(heights[z * s.SizeX + x], s.WaterLevel - 4);

    /// <summary>Fresh lakes: bowls dug down to sea level well inside the land (enclosed, so never salty).</summary>
    private static List<LakeBasin> CarveLakes(int seed, VoxelWorldSettings s, VoxelGrid grid, int[] heights)
    {
        var lakes = new List<LakeBasin>();
        var state = (ulong)(uint)seed * 0xD1B54A32D192ED03UL + 17;
        for (var attempt = 0; attempt < s.LakeCount * 60 && lakes.Count < s.LakeCount; attempt++)
        {
            var x = (VoxelWorldGenerator.Next01(ref state) * 2f - 1f) * s.WorldSizeX * 0.3f;
            var z = (VoxelWorldGenerator.Next01(ref state) * 2f - 1f) * s.WorldSizeZ * 0.3f;
            var radius = 4f + VoxelWorldGenerator.Next01(ref state) * 6f;
            if (lakes.Any(l => MathF.Sqrt((l.CenterX - x) * (l.CenterX - x) + (l.CenterZ - z) * (l.CenterZ - z)) < l.RadiusMeters + radius + 20f))
                continue;
            // The whole bowl rim must stand above the sea, or the lake would open onto the ocean.
            var (cx, _, cz) = grid.WorldToBlock(x, 0f, z);
            var rimBlocks = (int)MathF.Ceiling(radius * 1.8f / s.BlockSize);
            var dry = true;
            for (var dz = -rimBlocks; dz <= rimBlocks && dry; dz++)
            for (var dx = -rimBlocks; dx <= rimBlocks && dry; dx++)
            {
                var bx = cx + dx;
                var bz = cz + dz;
                if (bx < 0 || bz < 0 || bx >= s.SizeX || bz >= s.SizeZ || heights[bz * s.SizeX + bx] <= s.WaterLevel + 1) dry = false;
            }
            if (!dry) continue;
            lakes.Add(new LakeBasin(x, z, radius));
            for (var dz = -rimBlocks; dz <= rimBlocks; dz++)
            for (var dx = -rimBlocks; dx <= rimBlocks; dx++)
            {
                var i = (cz + dz) * s.SizeX + cx + dx;
                var d = MathF.Sqrt(dx * dx + dz * dz) * s.BlockSize / radius;
                if (d >= 1.6f) continue;
                var t = Math.Clamp(d / 1.6f, 0f, 1f);
                var bowl = s.WaterLevel - 3f + (heights[i] - (s.WaterLevel - 3f)) * (t * t * (3f - 2f * t));
                heights[i] = (int)MathF.Round(MathF.Min(heights[i], d < 1f ? MathF.Min(bowl, s.WaterLevel - 1f + d) : bowl));
            }
        }
        return lakes;
    }

    /// <summary>
    /// Priority-flood (Barnes et al.): raises every pit to its spill level (+epsilon per step), so
    /// following the steepest descent from any column always reaches the sea.
    /// </summary>
    private static float[] PriorityFlood(int[] heights, VoxelWorldSettings s, bool[] ocean)
    {
        var n = heights.Length;
        var filled = new float[n];
        var done = new bool[n];
        var queue = new PriorityQueue<int, float>();
        for (var i = 0; i < n; i++)
        {
            if (ocean[i])
            {
                filled[i] = heights[i];
                done[i] = true;
                queue.Enqueue(i, filled[i]);
            }
        }
        while (queue.TryDequeue(out var cell, out var level))
        {
            var cx = cell % s.SizeX;
            var cz = cell / s.SizeX;
            foreach (var (dx, dz) in Neighbours4)
            {
                var nx = cx + dx;
                var nz = cz + dz;
                if (nx < 0 || nz < 0 || nx >= s.SizeX || nz >= s.SizeZ) continue;
                var j = nz * s.SizeX + nx;
                if (done[j]) continue;
                done[j] = true;
                filled[j] = MathF.Max(heights[j], level + 1e-3f);
                queue.Enqueue(j, filled[j]);
            }
        }
        return filled;
    }

    private static readonly (int Dx, int Dz)[] Neighbours4 = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    /// <summary>Rivers from high sources, following the steepest descent of the flooded surface to the sea.</summary>
    private static List<River> TraceRivers(int seed, VoxelWorldSettings s, int[] heights, float[] filled, bool[] ocean)
    {
        var rivers = new List<River>();
        var minSourceHeight = s.WaterLevel + 14; // >= 7 m above the sea
        var candidates = Enumerable.Range(0, heights.Length)
            .Where(i => heights[i] >= minSourceHeight)
            .OrderBy(i => PerlinNoise.Hash01(seed + 601, i % s.SizeX, i / s.SizeX))
            .ToList();
        var used = new bool[heights.Length];
        foreach (var source in candidates)
        {
            if (rivers.Count >= s.RiverCount) break;
            var sx = source % s.SizeX;
            var sz = source / s.SizeX;
            if (rivers.Any(r => r.Path.Any(p => Math.Abs(p.X - sx) + Math.Abs(p.Z - sz) < 60))) continue;
            var path = new List<(int X, int Z)> { (sx, sz) };
            var current = source;
            var ok = false;
            for (var step = 0; step < heights.Length; step++)
            {
                var cx = current % s.SizeX;
                var cz = current / s.SizeX;
                if (ocean[current]) { ok = true; break; }
                var best = -1;
                var bestLevel = filled[current];
                foreach (var (dx, dz) in Neighbours4)
                {
                    var nx = cx + dx;
                    var nz = cz + dz;
                    if (nx < 0 || nz < 0 || nx >= s.SizeX || nz >= s.SizeZ) continue;
                    var j = nz * s.SizeX + nx;
                    if (filled[j] < bestLevel) { bestLevel = filled[j]; best = j; }
                }
                if (best < 0 || used[best]) break; // joined another river: keep rivers separate for now
                current = best;
                path.Add((current % s.SizeX, current / s.SizeX));
            }
            if (!ok || path.Count < 20) continue;
            foreach (var (x, z) in path) used[z * s.SizeX + x] = true;
            // Water surface never rises downstream: one block under the bank, at least sea level.
            var waterTop = new int[path.Count];
            var level = int.MaxValue;
            for (var i = 0; i < path.Count; i++)
            {
                var (x, z) = path[i];
                level = Math.Max(s.WaterLevel, Math.Min(level, heights[z * s.SizeX + x] - 1));
                waterTop[i] = level;
            }
            rivers.Add(new River(path, waterTop));
        }
        return rivers;
    }

    /// <summary>Columns with water on top (below sea level) connected to the map edge: the ocean (salt). The rest is fresh.</summary>
    private static bool[] OceanColumns(int[] heights, VoxelWorldSettings s)
    {
        var ocean = new bool[heights.Length];
        var stack = new Stack<int>();
        for (var x = 0; x < s.SizeX; x++) { stack.Push(x); stack.Push((s.SizeZ - 1) * s.SizeX + x); }
        for (var z = 0; z < s.SizeZ; z++) { stack.Push(z * s.SizeX); stack.Push(z * s.SizeX + s.SizeX - 1); }
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            if (ocean[i] || heights[i] >= s.WaterLevel) continue; // a column at sea level is dry beach
            ocean[i] = true;
            var x = i % s.SizeX;
            var z = i / s.SizeX;
            if (x > 0) stack.Push(i - 1);
            if (x < s.SizeX - 1) stack.Push(i + 1);
            if (z > 0) stack.Push(i - s.SizeX);
            if (z < s.SizeZ - 1) stack.Push(i + s.SizeX);
        }
        return ocean;
    }

    private static void FillColumns(VoxelWorld world, int[] heights, bool[] ocean)
    {
        var grid = world.Grid;
        var s = world.Settings;
        for (var z = 0; z < s.SizeZ; z++)
        for (var x = 0; x < s.SizeX; x++)
        {
            var i = z * s.SizeX + x;
            var top = heights[i];
            var wx = grid.OriginX + (x + 0.5f) * s.BlockSize;
            var wz = grid.OriginZ + (z + 0.5f) * s.BlockSize;
            var surface = VoxelWorldGenerator.SurfaceBlock(world, heights, x, z, top, wx, wz);
            if (top <= s.WaterLevel + 2 && NearOcean(ocean, s, x, z)) surface = BlockType.Sand; // beaches
            var under = surface is BlockType.Sand ? BlockType.Sand : BlockType.Dirt;
            grid.SetRaw(x, 0, z, BlockType.Bedrock);
            for (var y = 1; y <= top; y++) grid.SetRaw(x, y, z, y == top ? surface : y >= top - 3 ? under : BlockType.Stone);
            var liquid = ocean[i] ? BlockType.SaltWater : BlockType.Water;
            for (var y = top + 1; y <= s.WaterLevel; y++) grid.SetRaw(x, y, z, liquid);
        }
    }

    private static bool NearOcean(bool[] ocean, VoxelWorldSettings s, int x, int z)
    {
        for (var dz = -3; dz <= 3; dz++)
        for (var dx = -3; dx <= 3; dx++)
        {
            var nx = x + dx;
            var nz = z + dz;
            if (nx >= 0 && nz >= 0 && nx < s.SizeX && nz < s.SizeZ && ocean[nz * s.SizeX + nx]) return true;
        }
        return false;
    }

    /// <summary>River bed: fresh water one block deep (two at the source pool) below the banks.</summary>
    private static void FillRiver(VoxelGrid grid, VoxelWorldSettings s, River river)
    {
        for (var i = 0; i < river.Path.Count; i++)
        {
            var (x, z) = river.Path[i];
            var water = river.WaterTop[i];
            if (grid.Get(x, water, z) == BlockType.SaltWater) continue; // the mouth already meets the sea
            var bed = water - (i == 0 ? 2 : 1);
            for (var y = bed + 1; y < s.SizeY; y++)
            {
                var block = grid.Get(x, y, z);
                if (y <= water) grid.SetRaw(x, y, z, BlockType.Water);
                else if (block != BlockType.Air) grid.SetRaw(x, y, z, BlockType.Air);
                else break;
            }
            if (bed >= 1 && !BlockTypes.IsSolid(grid.Get(x, bed, z))) grid.SetRaw(x, bed, z, BlockType.Sand);
        }
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
