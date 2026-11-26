namespace CreatureIA.Core.Voxel;

public readonly record struct VoxelHit(bool Hit, BlockType Block, float Distance, int X, int Y, int Z, int NormalX, int NormalY, int NormalZ);

/// <summary>Amanatides–Woo grid traversal: exact first block a ray enters, no physics engine involved.</summary>
public static class VoxelRaycast
{
    public static VoxelHit Cast(VoxelGrid grid, float ox, float oy, float oz, float dx, float dy, float dz,
        float maxDistance, Func<BlockType, bool>? stopsAt = null)
    {
        stopsAt ??= BlockTypes.IsOpaqueToVision;
        var length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (length < 1e-8f) return default;
        dx /= length;
        dy /= length;
        dz /= length;

        var s = grid.BlockSize;
        // Work in block units.
        var px = (ox - grid.OriginX) / s;
        var py = (oy - grid.OriginY) / s;
        var pz = (oz - grid.OriginZ) / s;
        var maxT = maxDistance / s;

        var x = (int)MathF.Floor(px);
        var y = (int)MathF.Floor(py);
        var z = (int)MathF.Floor(pz);
        var stepX = dx > 0 ? 1 : dx < 0 ? -1 : 0;
        var stepY = dy > 0 ? 1 : dy < 0 ? -1 : 0;
        var stepZ = dz > 0 ? 1 : dz < 0 ? -1 : 0;
        var tDeltaX = stepX == 0 ? float.PositiveInfinity : MathF.Abs(1f / dx);
        var tDeltaY = stepY == 0 ? float.PositiveInfinity : MathF.Abs(1f / dy);
        var tDeltaZ = stepZ == 0 ? float.PositiveInfinity : MathF.Abs(1f / dz);
        var tMaxX = stepX == 0 ? float.PositiveInfinity : (stepX > 0 ? x + 1 - px : px - x) * tDeltaX;
        var tMaxY = stepY == 0 ? float.PositiveInfinity : (stepY > 0 ? y + 1 - py : py - y) * tDeltaY;
        var tMaxZ = stepZ == 0 ? float.PositiveInfinity : (stepZ > 0 ? z + 1 - pz : pz - z) * tDeltaZ;

        // The ray may start inside a block (e.g. eye in leaves): that block counts at distance 0.
        var start = grid.Get(x, y, z);
        if (stopsAt(start)) return new VoxelHit(true, start, 0f, x, y, z, 0, 0, 0);

        var t = 0f;
        int nx = 0, ny = 0, nz = 0;
        while (true)
        {
            if (tMaxX < tMaxY && tMaxX < tMaxZ)
            {
                x += stepX;
                t = tMaxX;
                tMaxX += tDeltaX;
                nx = -stepX; ny = 0; nz = 0;
            }
            else if (tMaxY < tMaxZ)
            {
                y += stepY;
                t = tMaxY;
                tMaxY += tDeltaY;
                nx = 0; ny = -stepY; nz = 0;
            }
            else
            {
                z += stepZ;
                t = tMaxZ;
                tMaxZ += tDeltaZ;
                nx = 0; ny = 0; nz = -stepZ;
            }
            if (t > maxT) return default;
            // Leaving the grid sideways or upwards: nothing more to see. Downwards: bedrock.
            if (y >= grid.SizeY && stepY >= 0) return default;
            if ((x < 0 && stepX <= 0) || (x >= grid.SizeX && stepX >= 0) || (z < 0 && stepZ <= 0) || (z >= grid.SizeZ && stepZ >= 0))
                return default;
            var block = grid.Get(x, y, z);
            if (stopsAt(block)) return new VoxelHit(true, block, t * s, x, y, z, nx, ny, nz);
        }
    }
}
