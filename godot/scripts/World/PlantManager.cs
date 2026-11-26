using System;
using CreatureIA.Godot.Net;
using Godot;

namespace CreatureIA.Godot.World;

public partial class PlantManager : Node3D
{
    private sealed class Plant
    {
        public Vector3 Position;
        public float Biomass;
        public float GrowthRate;
        public bool Thorny;
        public float DepletedSeconds;
    }

    /// <summary>A plant eaten down to nothing disappears and regrows elsewhere after this delay (plan 3.2).</summary>
    public const float RespawnDelaySeconds = 60f;

    /// <summary>Vision proxy of a bush: vertical cylinder from the ground (so the 1.1 m eye-level ray sees it).</summary>
    public const float VisionRadius = 0.45f;
    public const float VisionHeight = 1.4f;
    private const float CellSize = 8f;

    private RandomNumberGenerator _rng = null!;

    // 700 on 250 m (Task 001) -> 1400 on the 384 m voxel map keeps roughly the same density.
    [Export] public int Capacity { get; set; } = 1400;
    [Export] public int WorldSeed { get; set; } = 1337;
    [Export] public NodePath BiomeMapPath { get; set; } = "../BiomeMap";
    [Export] public NodePath DayNightPath { get; set; } = "../DayNightCycle";
    [Export] public NodePath SeasonPath { get; set; } = "../SeasonCycle";

    private Plant[] _plants = Array.Empty<Plant>();
    private List<int>[] _cells = Array.Empty<List<int>>();
    private int[] _cellOf = Array.Empty<int>();
    private int _cellsPerSide;
    private float _halfMap;
    private MultiMesh _multiMesh = null!;
    private BiomeMap _biomes = null!;
    private DayNightCycle _dayNight = null!;
    private SeasonCycle _season = null!;

    public override void _Ready()
    {
        _biomes = GetNode<BiomeMap>(BiomeMapPath);
        _dayNight = GetNode<DayNightCycle>(DayNightPath);
        _season = GetNode<SeasonCycle>(SeasonPath);
        BuildPool();
    }

    public override void _PhysicsProcess(double delta)
    {
        var p = ProfileCounters.Start();
        var dt = (float)delta;
        var light = Mathf.Max(0.05f, _dayNight.LightFactor);
        for (var i = 0; i < _plants.Length; i++)
        {
            var plant = _plants[i];
            var rate = plant.GrowthRate * light * _season.GrowthMultiplier;
            plant.Biomass = Mathf.Clamp(plant.Biomass + dt * rate * plant.Biomass * (1f - plant.Biomass), 0f, 1f);
            // Logistic growth is stuck at B = 0, so depleted plants would never come back and the
            // world's food would run out for good. Relocate them to a fertile spot instead.
            if (plant.Biomass <= 0.01f)
            {
                plant.DepletedSeconds += dt;
                if (plant.DepletedSeconds >= RespawnDelaySeconds) Relocate(i, plant);
            }
            UpdateTransform(i, plant);
        }
        ProfileCounters.Stop("plants", p);
    }

    public float ConsumeNearest(Vector3 position, float radius, float maximumAmount, out bool thorny)
    {
        thorny = false;
        var best = -1;
        var distance = radius;
        var minX = CellIndex(position.X - radius);
        var maxX = CellIndex(position.X + radius);
        var minZ = CellIndex(position.Z - radius);
        var maxZ = CellIndex(position.Z + radius);
        for (var cz = minZ; cz <= maxZ; cz++)
        for (var cx = minX; cx <= maxX; cx++)
            foreach (var i in _cells[cz * _cellsPerSide + cx])
            {
                if (_plants[i].Biomass <= 0.01f) continue;
                var candidate = position.DistanceTo(_plants[i].Position);
                if (candidate >= distance) continue;
                distance = candidate;
                best = i;
            }

        if (best < 0) return 0f;
        var amount = Mathf.Min(maximumAmount, _plants[best].Biomass);
        _plants[best].Biomass -= amount;
        thorny = _plants[best].Thorny;
        UpdateTransform(best, _plants[best]);
        return amount;
    }

    /// <summary>Edible plants whose base lies within <paramref name="range"/> of <paramref name="eye"/> (horizontal).</summary>
    public void CollectNear(Vector3 eye, float range, List<(Vector3 Base, bool Thorny)> output)
    {
        output.Clear();
        var r2 = range * range;
        var minX = CellIndex(eye.X - range);
        var maxX = CellIndex(eye.X + range);
        var minZ = CellIndex(eye.Z - range);
        var maxZ = CellIndex(eye.Z + range);
        for (var cz = minZ; cz <= maxZ; cz++)
        for (var cx = minX; cx <= maxX; cx++)
            foreach (var index in _cells[cz * _cellsPerSide + cx])
            {
                var plant = _plants[index];
                if (plant.Biomass <= 0.01f) continue;
                var dx = plant.Position.X - eye.X;
                var dz = plant.Position.Z - eye.Z;
                if (dx * dx + dz * dz > r2) continue;
                output.Add((plant.Position, plant.Thorny));
            }
    }

    public float SampleScent(Vector3 position)
    {
        // Beyond ~40 m a plant contributes < 1/1600 of a nearby one; skipping far cells keeps this O(local).
        const float range = 40f;
        var scent = 0f;
        var minX = CellIndex(position.X - range);
        var maxX = CellIndex(position.X + range);
        var minZ = CellIndex(position.Z - range);
        var maxZ = CellIndex(position.Z + range);
        for (var cz = minZ; cz <= maxZ; cz++)
        for (var cx = minX; cx <= maxX; cx++)
            foreach (var i in _cells[cz * _cellsPerSide + cx])
            {
                var plant = _plants[i];
                if (plant.Biomass <= 0.01f) continue;
                scent += plant.Biomass / Mathf.Max(4f, position.DistanceSquaredTo(plant.Position));
            }
        return scent;
    }

    private int CellIndex(float coordinate) =>
        Mathf.Clamp((int)((coordinate + _halfMap) / CellSize), 0, _cellsPerSide - 1);

    private void PlaceInCell(int index)
    {
        var position = _plants[index].Position;
        var cell = CellIndex(position.Z) * _cellsPerSide + CellIndex(position.X);
        if (_cellOf[index] == cell) return;
        if (_cellOf[index] >= 0) _cells[_cellOf[index]].Remove(index);
        _cells[cell].Add(index);
        _cellOf[index] = cell;
    }

    private Vector3 FertileSpot(RandomNumberGenerator rng) =>
        _biomes.RandomLandPoint(rng, _halfMap - 12f, sample => sample.VegetationDensity >= rng.Randf());

    private void BuildPool()
    {
        var rng = _rng = new RandomNumberGenerator { Seed = (ulong)(RunSettings.Seed(WorldSeed) + 401) };
        Capacity = RunSettings.Plants(RunSettings.Showcase ? 30 : Capacity);
        _plants = new Plant[Capacity];
        _halfMap = _biomes.MapSize / 2f;
        _cellsPerSide = Mathf.CeilToInt(_biomes.MapSize / CellSize);
        _cells = new List<int>[_cellsPerSide * _cellsPerSide];
        for (var i = 0; i < _cells.Length; i++) _cells[i] = new List<int>();
        _cellOf = Enumerable.Repeat(-1, Capacity).ToArray();
        _multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            InstanceCount = Capacity,
            // Voxel bush: a 0.9 m cube resting on the ground, scaled by biomass. Colour separates
            // normal (green) from thorny (red-brown) plants.
            Mesh = BushMesh()
        };

        for (var i = 0; i < Capacity; i++)
        {
            var position = FertileSpot(rng);
            var sample = _biomes.Sample(new Vector2(position.X, position.Z));
            _plants[i] = new Plant
            {
                Position = position,
                Biomass = rng.RandfRange(0.25f, 1f),
                GrowthRate = Mathf.Lerp(0.002f, 0.012f, sample.Humidity),
                Thorny = rng.Randf() < 0.1f
            };
            _multiMesh.SetInstanceColor(i, _plants[i].Thorny ? new Color(0.55f, 0.18f, 0.12f) : new Color(0.25f, 0.72f, 0.2f));
            PlaceInCell(i);
            UpdateTransform(i, _plants[i]);
        }

        AddChild(new MultiMeshInstance3D
        {
            Multimesh = _multiMesh,
            Name = "PlantInstances",
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.9f }
        });
    }

    private void Relocate(int index, Plant plant)
    {
        var position = FertileSpot(_rng);
        var sample = _biomes.Sample(new Vector2(position.X, position.Z));
        plant.Position = position;
        plant.Biomass = 0.3f;
        plant.GrowthRate = Mathf.Lerp(0.002f, 0.012f, sample.Humidity);
        plant.DepletedSeconds = 0f;
        PlaceInCell(index);
    }

    private void UpdateTransform(int index, Plant plant)
    {
        var scale = Mathf.Max(0.001f, plant.Biomass);
        _multiMesh.SetInstanceTransform(index, new Transform3D(Basis.Identity.Scaled(new Vector3(scale, scale, scale)), plant.Position));
    }

    private static Mesh BushMesh()
    {
        var box = new BoxMesh { Size = new Vector3(0.9f, 0.9f, 0.9f) };
        var arrays = box.GetMeshArrays();
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        for (var i = 0; i < vertices.Length; i++) vertices[i] += Vector3.Up * 0.45f;
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
