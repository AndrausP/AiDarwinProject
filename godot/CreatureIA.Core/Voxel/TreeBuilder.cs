namespace CreatureIA.Core.Voxel;

public enum TreeSpecies
{
    Oak,
    BigOak,
    Birch,
    Spruce,
    Sequoia,
    Acacia,
    Cactus
}

/// <summary>
/// Block-built plants. Each builder starts at the ground block (x, ground, z) — the surface block
/// under the trunk becomes dirt — and only writes foliage into air so trees do not cut each other.
/// <paramref name="r"/> is a deterministic value in [0, 1) that varies size and shape.
/// </summary>
public static class TreeBuilder
{
    public static void Build(VoxelGrid grid, TreeSpecies species, int x, int ground, int z, float r)
    {
        switch (species)
        {
            case TreeSpecies.Oak: Oak(grid, x, ground, z, r, big: false); break;
            case TreeSpecies.BigOak: Oak(grid, x, ground, z, r, big: true); break;
            case TreeSpecies.Birch: Birch(grid, x, ground, z, r); break;
            case TreeSpecies.Spruce: Spruce(grid, x, ground, z, r); break;
            case TreeSpecies.Sequoia: Sequoia(grid, x, ground, z, r); break;
            case TreeSpecies.Acacia: Acacia(grid, x, ground, z, r); break;
            case TreeSpecies.Cactus: Cactus(grid, x, ground, z, r); break;
        }
    }

    /// <summary>Trunk height in blocks a builder will produce for this r (tests and clearance checks).</summary>
    public static int SequoiaHeight(VoxelGrid grid, int ground, float r) =>
        Math.Min(40 + (int)(r * 24f), grid.SizeY - 6 - ground);

    private static void Oak(VoxelGrid grid, int x, int ground, int z, float r, bool big)
    {
        var trunk = big ? 9 + (int)(r * 5) : 6 + (int)(r * 6);
        var radius = big ? 4 : 2 + (int)(r * 2f);
        var thick = big ? 2 : 1;
        Trunk(grid, x, ground, z, trunk, thick, BlockType.Wood);
        var cy = ground + trunk;
        Blob(grid, x + (thick - 1) * 0.5f, cy, z + (thick - 1) * 0.5f, radius, radius * 0.8f, BlockType.Leaves);
        if (big)
        {
            // Side lobes make the crown irregular.
            Blob(grid, x + 3, cy - 2, z, 2.5f, 2f, BlockType.Leaves);
            Blob(grid, x - 2, cy - 1, z + 2, 2.5f, 2f, BlockType.Leaves);
        }
    }

    private static void Birch(VoxelGrid grid, int x, int ground, int z, float r)
    {
        var trunk = 9 + (int)(r * 6);
        Trunk(grid, x, ground, z, trunk, 1, BlockType.BirchWood);
        Blob(grid, x, ground + trunk - 1, z, 2.2f, 3.2f, BlockType.BirchLeaves);
    }

    private static void Spruce(VoxelGrid grid, int x, int ground, int z, float r)
    {
        var trunk = 12 + (int)(r * 8);
        Trunk(grid, x, ground, z, trunk, 1, BlockType.Wood);
        var bottom = ground + 3;
        var top = ground + trunk + 2;
        for (var y = bottom; y <= top; y++)
        {
            var t = (y - bottom) / (float)(top - bottom);
            // Cone with alternating tiers: wide, narrow, wide...
            var radius = (1f - t) * 4.2f + 0.6f - ((y - bottom) % 2 == 1 ? 1f : 0f);
            Disc(grid, x, y, z, MathF.Max(0.6f, radius), BlockType.PineLeaves);
        }
        Put(grid, x, top + 1, z, BlockType.PineLeaves);
    }

    /// <summary>Giant: 2×2 (3×3 for the largest) red trunk 20–32 m tall, crown only in the top third.</summary>
    private static void Sequoia(VoxelGrid grid, int x, int ground, int z, float r)
    {
        var height = SequoiaHeight(grid, ground, r);
        var thick = r > 0.6f ? 3 : 2;
        Trunk(grid, x, ground, z, height, thick, BlockType.RedWood);
        // Root flare at the base.
        for (var dz = -1; dz <= thick; dz++)
        for (var dx = -1; dx <= thick; dx++)
        {
            var corner = (dx == -1 || dx == thick) && (dz == -1 || dz == thick);
            if (corner) continue;
            if (dx >= 0 && dx < thick && dz >= 0 && dz < thick) continue;
            for (var y = ground + 1; y <= ground + 2; y++) Put(grid, x + dx, y, z + dz, BlockType.RedWood, overwriteAir: true);
        }
        var cx = x + (thick - 1) * 0.5f;
        var cz = z + (thick - 1) * 0.5f;
        var crownBottom = ground + (int)(height * 0.6f);
        var top = ground + height;
        for (var y = crownBottom; y <= top + 2; y++)
        {
            var t = (y - crownBottom) / (float)(top + 2 - crownBottom);
            var radius = 1.5f + (1f - t) * 4.5f + (y % 3 == 0 ? 0.8f : 0f);
            Disc(grid, cx, y, cz, radius, BlockType.PineLeaves);
        }
    }

    private static void Acacia(VoxelGrid grid, int x, int ground, int z, float r)
    {
        var straight = 3 + (int)(r * 3);
        Trunk(grid, x, ground, z, straight, 1, BlockType.Wood);
        // Leaning upper trunk.
        var dir = r < 0.5f ? 1 : -1;
        var tx = x;
        var y = ground + straight;
        for (var i = 1; i <= 3; i++)
        {
            tx += dir;
            Put(grid, tx, y + i, z, BlockType.Wood, overwriteAir: false);
        }
        var canopyY = y + 4;
        Disc(grid, tx, canopyY, z, 3.6f, BlockType.AcaciaLeaves);
        Disc(grid, tx, canopyY + 1, z, 2.2f, BlockType.AcaciaLeaves);
    }

    private static void Cactus(VoxelGrid grid, int x, int ground, int z, float r)
    {
        var height = 3 + (int)(r * 4);
        for (var y = ground + 1; y <= ground + height; y++) grid.SetRaw(x, y, z, BlockType.Cactus);
        if (height >= 5)
        {
            var side = r < 0.8f ? 1 : -1;
            Put(grid, x + side, ground + 3, z, BlockType.Cactus);
            Put(grid, x + side, ground + 4, z, BlockType.Cactus);
        }
    }

    /// <summary>Low leaf shrub, 1–2 blocks tall (decorative cover, not food).</summary>
    public static void Shrub(VoxelGrid grid, int x, int ground, int z, float r, BlockType leaves = BlockType.Leaves)
    {
        Put(grid, x, ground + 1, z, leaves);
        if (r > 0.4f) Put(grid, x + 1, ground + 1, z, leaves);
        if (r > 0.6f) Put(grid, x, ground + 1, z + 1, leaves);
        if (r > 0.85f) Put(grid, x, ground + 2, z, leaves);
    }

    private static void Trunk(VoxelGrid grid, int x, int ground, int z, int height, int thick, BlockType wood)
    {
        for (var dz = 0; dz < thick; dz++)
        for (var dx = 0; dx < thick; dx++)
        {
            if (BlockTypes.IsSolid(grid.Get(x + dx, ground, z + dz))) grid.SetRaw(x + dx, ground, z + dz, BlockType.Dirt);
            for (var y = ground + 1; y <= ground + height; y++) grid.SetRaw(x + dx, y, z + dz, wood);
        }
    }

    private static void Blob(VoxelGrid grid, float cx, int cy, float cz, float radius, float verticalRadius, BlockType leaves)
    {
        var ri = (int)MathF.Ceiling(radius);
        var rv = (int)MathF.Ceiling(verticalRadius);
        for (var dy = -rv; dy <= rv + 1; dy++)
        for (var dz = -ri; dz <= ri + 1; dz++)
        for (var dx = -ri; dx <= ri + 1; dx++)
        {
            var px = (int)MathF.Floor(cx) + dx;
            var pz = (int)MathF.Floor(cz) + dz;
            var ex = (px - cx) / radius;
            var ez = (pz - cz) / radius;
            var ey = dy / verticalRadius;
            if (ex * ex + ey * ey + ez * ez > 1.05f) continue;
            Put(grid, px, cy + dy, pz, leaves);
        }
    }

    private static void Disc(VoxelGrid grid, float cx, int y, float cz, float radius, BlockType leaves)
    {
        var ri = (int)MathF.Ceiling(radius);
        for (var dz = -ri; dz <= ri + 1; dz++)
        for (var dx = -ri; dx <= ri + 1; dx++)
        {
            var px = (int)MathF.Floor(cx) + dx;
            var pz = (int)MathF.Floor(cz) + dz;
            var ddx = px - cx;
            var ddz = pz - cz;
            if (ddx * ddx + ddz * ddz > radius * radius) continue;
            Put(grid, px, y, pz, leaves);
        }
    }

    private static void Put(VoxelGrid grid, int x, int y, int z, BlockType block, bool overwriteAir = true)
    {
        var current = grid.Get(x, y, z);
        if (current == BlockType.Air || BlockTypes.IsDecoration(current) || (!overwriteAir && !BlockTypes.IsSolid(current)))
            grid.SetRaw(x, y, z, block);
    }
}
