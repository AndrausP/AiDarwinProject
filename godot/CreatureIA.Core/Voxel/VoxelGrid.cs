namespace CreatureIA.Core.Voxel;

/// <summary>
/// Block storage in cubic chunks of <see cref="ChunkSize"/>³, allocated lazily (all-air chunks
/// cost nothing). World position of block (bx, by, bz) min corner = Origin + b·BlockSize.
/// </summary>
public sealed class VoxelGrid
{
    public const int ChunkSize = 16;
    public const int ChunkVolume = ChunkSize * ChunkSize * ChunkSize;

    public int SizeX { get; }
    public int SizeY { get; }
    public int SizeZ { get; }
    public int ChunksX { get; }
    public int ChunksY { get; }
    public int ChunksZ { get; }
    public float BlockSize { get; }
    public float OriginX { get; }
    public float OriginY { get; }
    public float OriginZ { get; }

    private readonly byte[]?[] _chunks;
    private readonly bool[] _dirty;

    /// <summary>Raised when a block changes; arguments are block coordinates.</summary>
    public event Action<int, int, int>? BlockChanged;

    public VoxelGrid(int sizeX, int sizeY, int sizeZ, float blockSize, float originX, float originY, float originZ)
    {
        if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0) throw new ArgumentOutOfRangeException(nameof(sizeX));
        if (sizeX % ChunkSize != 0 || sizeY % ChunkSize != 0 || sizeZ % ChunkSize != 0)
            throw new ArgumentException($"Grid dimensions must be multiples of {ChunkSize}.");
        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;
        ChunksX = sizeX / ChunkSize;
        ChunksY = sizeY / ChunkSize;
        ChunksZ = sizeZ / ChunkSize;
        BlockSize = blockSize;
        OriginX = originX;
        OriginY = originY;
        OriginZ = originZ;
        _chunks = new byte[]?[ChunksX * ChunksY * ChunksZ];
        _dirty = new bool[_chunks.Length];
    }

    public int ChunkCount => _chunks.Length;
    public int AllocatedChunkCount => _chunks.Count(c => c is not null);

    public bool InBounds(int x, int y, int z) =>
        (uint)x < (uint)SizeX && (uint)y < (uint)SizeY && (uint)z < (uint)SizeZ;

    public int ChunkIndex(int cx, int cy, int cz) => (cy * ChunksZ + cz) * ChunksX + cx;

    public (int Cx, int Cy, int Cz) ChunkCoordinates(int chunkIndex)
    {
        var cx = chunkIndex % ChunksX;
        var rest = chunkIndex / ChunksX;
        return (cx, rest / ChunksZ, rest % ChunksZ);
    }

    public bool IsChunkEmpty(int chunkIndex) => _chunks[chunkIndex] is null;

    /// <summary>Outside the grid: below the floor reads as bedrock, everything else as air.</summary>
    public BlockType Get(int x, int y, int z)
    {
        if (!InBounds(x, y, z)) return y < 0 ? BlockType.Bedrock : BlockType.Air;
        var chunk = _chunks[ChunkIndex(x >> 4, y >> 4, z >> 4)];
        return chunk is null ? BlockType.Air : (BlockType)chunk[Local(x, y, z)];
    }

    /// <summary>Writes a block during generation (no change notification, no dirty flag).</summary>
    public void SetRaw(int x, int y, int z, BlockType block)
    {
        if (!InBounds(x, y, z)) return;
        var index = ChunkIndex(x >> 4, y >> 4, z >> 4);
        var chunk = _chunks[index];
        if (chunk is null)
        {
            if (block == BlockType.Air) return;
            chunk = _chunks[index] = new byte[ChunkVolume];
        }
        chunk[Local(x, y, z)] = (byte)block;
    }

    /// <summary>Runtime edit (digging): marks the chunk and touching neighbours dirty and notifies.</summary>
    public bool Set(int x, int y, int z, BlockType block)
    {
        if (!InBounds(x, y, z) || Get(x, y, z) == block) return false;
        SetRaw(x, y, z, block);
        MarkDirty(x, y, z);
        BlockChanged?.Invoke(x, y, z);
        return true;
    }

    public IReadOnlyList<int> TakeDirtyChunks()
    {
        var list = new List<int>();
        for (var i = 0; i < _dirty.Length; i++)
        {
            if (!_dirty[i]) continue;
            _dirty[i] = false;
            list.Add(i);
        }
        return list;
    }

    public (int X, int Y, int Z) WorldToBlock(float wx, float wy, float wz) => (
        (int)MathF.Floor((wx - OriginX) / BlockSize),
        (int)MathF.Floor((wy - OriginY) / BlockSize),
        (int)MathF.Floor((wz - OriginZ) / BlockSize));

    public BlockType GetAtWorld(float wx, float wy, float wz)
    {
        var (x, y, z) = WorldToBlock(wx, wy, wz);
        return Get(x, y, z);
    }

    /// <summary>Highest non-air block in a column, or -1 when the column is empty.</summary>
    public int TopBlockY(int x, int z, bool solidOnly = false)
    {
        for (var y = SizeY - 1; y >= 0; y--)
        {
            var block = Get(x, y, z);
            if (block == BlockType.Air) continue;
            if (solidOnly && !BlockTypes.IsSolid(block)) continue;
            return y;
        }
        return -1;
    }

    /// <summary>World Y of the top face of the highest solid block in the column under (wx, wz).</summary>
    public float SurfaceHeightAt(float wx, float wz)
    {
        var (x, _, z) = WorldToBlock(wx, 0f, wz);
        var top = TopBlockY(x, z, solidOnly: true);
        return OriginY + (top + 1) * BlockSize;
    }

    private void MarkDirty(int x, int y, int z)
    {
        int cx = x >> 4, cy = y >> 4, cz = z >> 4;
        _dirty[ChunkIndex(cx, cy, cz)] = true;
        int lx = x & 15, ly = y & 15, lz = z & 15;
        if (lx == 0 && cx > 0) _dirty[ChunkIndex(cx - 1, cy, cz)] = true;
        if (lx == 15 && cx < ChunksX - 1) _dirty[ChunkIndex(cx + 1, cy, cz)] = true;
        if (ly == 0 && cy > 0) _dirty[ChunkIndex(cx, cy - 1, cz)] = true;
        if (ly == 15 && cy < ChunksY - 1) _dirty[ChunkIndex(cx, cy + 1, cz)] = true;
        if (lz == 0 && cz > 0) _dirty[ChunkIndex(cx, cy, cz - 1)] = true;
        if (lz == 15 && cz < ChunksZ - 1) _dirty[ChunkIndex(cx, cy, cz + 1)] = true;
    }

    private static int Local(int x, int y, int z) => ((y & 15) * ChunkSize + (z & 15)) * ChunkSize + (x & 15);
}
