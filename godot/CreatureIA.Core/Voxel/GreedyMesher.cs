namespace CreatureIA.Core.Voxel;

/// <summary>Engine-agnostic triangle soup for one chunk layer (solid or water).</summary>
public sealed class VoxelMeshData
{
    public List<float> Positions { get; } = new();
    public List<float> Normals { get; } = new();
    public List<float> Colors { get; } = new();
    public List<int> Indices { get; } = new();
    public int QuadCount => Indices.Count / 6;
    public bool IsEmpty => Indices.Count == 0;

    internal void AddQuad(ReadOnlySpan<float> corners, int nx, int ny, int nz, (float R, float G, float B) color)
    {
        var start = Positions.Count / 3;
        for (var i = 0; i < 4; i++)
        {
            Positions.Add(corners[i * 3]);
            Positions.Add(corners[i * 3 + 1]);
            Positions.Add(corners[i * 3 + 2]);
            Normals.Add(nx);
            Normals.Add(ny);
            Normals.Add(nz);
            Colors.Add(color.R);
            Colors.Add(color.G);
            Colors.Add(color.B);
        }
        // Corners arrive counter-clockwise seen from the normal side; Godot treats clockwise as front.
        Indices.Add(start);
        Indices.Add(start + 2);
        Indices.Add(start + 1);
        Indices.Add(start);
        Indices.Add(start + 3);
        Indices.Add(start + 2);
    }
}

public sealed record ChunkMesh(VoxelMeshData Solid, VoxelMeshData Water);

/// <summary>
/// Greedy meshing: visible faces of equal block type on the same slice are merged into maximal
/// rectangles, so a flat 16×16 meadow costs one quad instead of 256.
/// </summary>
public static class GreedyMesher
{
    private const int N = VoxelGrid.ChunkSize;

    public static ChunkMesh Build(VoxelGrid grid, int cx, int cy, int cz)
    {
        var solid = new VoxelMeshData();
        var water = new VoxelMeshData();
        var mask = new byte[N * N];
        var x0 = cx * N;
        var y0 = cy * N;
        var z0 = cz * N;
        Span<int> pos = stackalloc int[3];
        Span<int> q = stackalloc int[3];
        Span<float> corners = stackalloc float[12];

        for (var axis = 0; axis < 3; axis++)
        {
            var u = (axis + 1) % 3;
            var v = (axis + 2) % 3;
            for (var sign = -1; sign <= 1; sign += 2)
            {
                q.Clear();
                q[axis] = sign;
                for (var slice = 0; slice < N; slice++)
                {
                    // Build the face mask for this slice.
                    var any = false;
                    for (var j = 0; j < N; j++)
                    for (var i = 0; i < N; i++)
                    {
                        pos[axis] = slice;
                        pos[u] = i;
                        pos[v] = j;
                        var bx = x0 + pos[0];
                        var by = y0 + pos[1];
                        var bz = z0 + pos[2];
                        var block = grid.Get(bx, by, bz);
                        var neighbour = grid.Get(bx + q[0], by + q[1], bz + q[2]);
                        var visible = block != BlockType.Air && FaceVisible(block, neighbour);
                        mask[j * N + i] = visible ? (byte)block : (byte)0;
                        any |= visible;
                    }
                    if (!any) continue;

                    // Merge rectangles.
                    for (var j = 0; j < N; j++)
                    for (var i = 0; i < N;)
                    {
                        var type = mask[j * N + i];
                        if (type == 0)
                        {
                            i++;
                            continue;
                        }
                        var width = 1;
                        while (i + width < N && mask[j * N + i + width] == type) width++;
                        var height = 1;
                        while (j + height < N)
                        {
                            var rowOk = true;
                            for (var k = 0; k < width; k++)
                                if (mask[(j + height) * N + i + k] != type) { rowOk = false; break; }
                            if (!rowOk) break;
                            height++;
                        }
                        for (var h = 0; h < height; h++)
                        for (var k = 0; k < width; k++)
                            mask[(j + h) * N + i + k] = 0;

                        EmitQuad((BlockType)type == BlockType.Water ? water : solid, grid, corners,
                            axis, u, v, sign, x0, y0, z0, slice, i, j, width, height, (BlockType)type);
                        i += width;
                    }
                }
            }
        }
        EmitDecorations(grid, solid, x0, y0, z0);
        return new ChunkMesh(solid, water);
    }

    /// <summary>Face of <paramref name="block"/> towards <paramref name="neighbour"/> is drawn.</summary>
    public static bool FaceVisible(BlockType block, BlockType neighbour)
    {
        if (block == BlockType.Air || BlockTypes.IsDecoration(block)) return false;
        var open = neighbour == BlockType.Air || BlockTypes.IsDecoration(neighbour);
        if (block == BlockType.Water) return open;
        return open || neighbour == BlockType.Water;
    }

    /// <summary>Tall grass and flowers: two crossed vertical quads, drawn from both sides.</summary>
    private static void EmitDecorations(VoxelGrid grid, VoxelMeshData target, int x0, int y0, int z0)
    {
        Span<float> corners = stackalloc float[12];
        var s = grid.BlockSize;
        for (var y = 0; y < N; y++)
        for (var z = 0; z < N; z++)
        for (var x = 0; x < N; x++)
        {
            var block = grid.Get(x0 + x, y0 + y, z0 + z);
            if (!BlockTypes.IsDecoration(block)) continue;
            var wx = grid.OriginX + (x0 + x) * s;
            var wy = grid.OriginY + (y0 + y) * s;
            var wz = grid.OriginZ + (z0 + z) * s;
            var height = block == BlockType.TallGrass ? s * 0.9f : s * 0.7f;
            var (r, g, b) = BlockTypes.Color(block);
            for (var diagonal = 0; diagonal < 2; diagonal++)
            {
                var ax = diagonal == 0 ? 0.1f : 0.9f;
                var bx = diagonal == 0 ? 0.9f : 0.1f;
                for (var side = 0; side < 2; side++)
                {
                    // (a, bottom) → (b, bottom) → (b, top) → (a, top); the second side swaps a and b.
                    var p = side == 0 ? (ax, bx) : (bx, ax);
                    var pz0 = side == 0 ? 0.1f : 0.9f;
                    var pz1 = side == 0 ? 0.9f : 0.1f;
                    corners[0] = wx + p.Item1 * s; corners[1] = wy; corners[2] = wz + pz0 * s;
                    corners[3] = wx + p.Item2 * s; corners[4] = wy; corners[5] = wz + pz1 * s;
                    corners[6] = wx + p.Item2 * s; corners[7] = wy + height; corners[8] = wz + pz1 * s;
                    corners[9] = wx + p.Item1 * s; corners[10] = wy + height; corners[11] = wz + pz0 * s;
                    target.AddQuad(corners, 0, 1, 0, (r * 0.9f, g * 0.9f, b * 0.9f));
                }
            }
        }
    }

    private static void EmitQuad(VoxelMeshData target, VoxelGrid grid, Span<float> corners,
        int axis, int u, int v, int sign, int x0, int y0, int z0,
        int slice, int i, int j, int width, int height, BlockType type)
    {
        Span<float> origin = stackalloc float[3] { x0, y0, z0 };
        Span<float> a = stackalloc float[3];
        Span<float> du = stackalloc float[3];
        Span<float> dv = stackalloc float[3];
        a[axis] = origin[axis] + slice + (sign > 0 ? 1 : 0);
        a[u] = origin[u] + i;
        a[v] = origin[v] + j;
        du[u] = width;
        dv[v] = height;
        // Counter-clockwise from the normal side: for +axis (u→v) is CCW, for −axis swap.
        Span<float> e1 = sign > 0 ? du : dv;
        Span<float> e2 = sign > 0 ? dv : du;
        for (var c = 0; c < 3; c++)
        {
            corners[c] = a[c];
            corners[3 + c] = a[c] + e1[c];
            corners[6 + c] = a[c] + e1[c] + e2[c];
            corners[9 + c] = a[c] + e2[c];
        }
        var s = grid.BlockSize;
        for (var k = 0; k < 4; k++)
        {
            corners[k * 3] = grid.OriginX + corners[k * 3] * s;
            corners[k * 3 + 1] = grid.OriginY + corners[k * 3 + 1] * s;
            corners[k * 3 + 2] = grid.OriginZ + corners[k * 3 + 2] * s;
        }
        var n = new int[3];
        n[axis] = sign;
        var shade = axis == 1 ? (sign > 0 ? 1f : 0.5f) : axis == 0 ? 0.8f : 0.68f;
        var (r, g, b) = BlockTypes.Color(type);
        target.AddQuad(corners, n[0], n[1], n[2], (r * shade, g * shade, b * shade));
    }
}
