using CreatureIA.Core.Voxel;
using Godot;

namespace CreatureIA.Godot.World;

/// <summary>
/// Presentation of the voxel grid: one MeshInstance3D per non-empty chunk (greedy mesh, vertex
/// colours), rebuilt when blocks change. Collision is not here: bodies collide against blocks in
/// <see cref="VoxelPhysics"/>. Nothing is built in headless runs.
/// </summary>
public partial class TerrainGenerator : Node3D
{
    [Export] public NodePath BiomeMapPath { get; set; } = "../BiomeMap";

    private VoxelGrid _grid = null!;
    private readonly Dictionary<int, MeshInstance3D> _chunks = new();
    private StandardMaterial3D _solidMaterial = null!;
    private StandardMaterial3D _waterMaterial = null!;

    public override void _Ready()
    {
        _grid = GetNode<BiomeMap>(BiomeMapPath).Grid;
        if (DisplayServer.GetName() == "headless")
        {
            SetProcess(false);
            return;
        }
        _solidMaterial = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.95f };
        _waterMaterial = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            AlbedoColor = new Color(1f, 1f, 1f, 0.72f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 0.15f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        var started = Time.GetTicksMsec();
        for (var i = 0; i < _grid.ChunkCount; i++)
            if (!_grid.IsChunkEmpty(i)) Rebuild(i);
        _grid.TakeDirtyChunks();
        GD.Print($"[voxel] meshed {_chunks.Count} chunks in {Time.GetTicksMsec() - started} ms");
        foreach (var (text, x, y, z) in GetNode<BiomeMap>(BiomeMapPath).World.Labels)
            AddChild(new Label3D
            {
                Text = text,
                Position = new Vector3(x, y, z),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                FontSize = 64,
                PixelSize = 0.016f,
                Modulate = new Color(0.15f, 0.17f, 0.2f),
                OutlineModulate = new Color(1f, 1f, 1f),
                OutlineSize = 12
            });
    }

    public override void _Process(double delta)
    {
        foreach (var index in _grid.TakeDirtyChunks()) Rebuild(index);
    }

    private void Rebuild(int index)
    {
        var (cx, cy, cz) = _grid.ChunkCoordinates(index);
        var mesh = GreedyMesher.Build(_grid, cx, cy, cz);
        if (mesh.Solid.IsEmpty && mesh.Water.IsEmpty)
        {
            if (_chunks.Remove(index, out var stale)) stale.QueueFree();
            return;
        }
        var arrayMesh = new ArrayMesh();
        AddSurface(arrayMesh, mesh.Solid, _solidMaterial);
        AddSurface(arrayMesh, mesh.Water, _waterMaterial);
        if (!_chunks.TryGetValue(index, out var instance))
        {
            instance = new MeshInstance3D { Name = $"Chunk{cx}_{cy}_{cz}" };
            AddChild(instance);
            _chunks[index] = instance;
        }
        instance.Mesh = arrayMesh;
    }

    internal static void AddSurface(ArrayMesh target, VoxelMeshData data, Material material)
    {
        if (data.IsEmpty) return;
        var vertexCount = data.Positions.Count / 3;
        var vertices = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var colors = new Color[vertexCount];
        for (var i = 0; i < vertexCount; i++)
        {
            vertices[i] = new Vector3(data.Positions[i * 3], data.Positions[i * 3 + 1], data.Positions[i * 3 + 2]);
            normals[i] = new Vector3(data.Normals[i * 3], data.Normals[i * 3 + 1], data.Normals[i * 3 + 2]);
            colors[i] = new Color(data.Colors[i * 3], data.Colors[i * 3 + 1], data.Colors[i * 3 + 2]);
        }
        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Index] = data.Indices.ToArray();
        target.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        target.SurfaceSetMaterial(target.GetSurfaceCount() - 1, material);
    }
}
