using System.Numerics;

namespace CreatureIA.Core.Simulation;

/// <summary>Bushes: logistic growth by light and season, eaten down, relocated after depletion (Godot PlantManager).</summary>
public sealed class PlantField
{
    public sealed class Plant
    {
        public Vector3 Position;
        public float Biomass;
        public float GrowthRate;
        public bool Thorny;
        public float DepletedSeconds;
    }

    /// <summary>A plant eaten down to nothing disappears and regrows elsewhere after this delay.</summary>
    public const float RespawnDelaySeconds = 60f;
    /// <summary>Vision proxy of a bush: vertical cylinder from the ground.</summary>
    public const float VisionRadius = 0.45f;
    public const float VisionHeight = 1.4f;
    public const int DefaultCapacity = 1400;
    private const float CellSize = 8f;
    private const float Depleted = 0.01f;

    private readonly SimTerrain _terrain;
    private readonly SimRandom _rng;
    private readonly Plant[] _plants;
    private readonly List<int>[] _cells;
    private readonly int[] _cellOf;
    private readonly int _cellsPerSide;
    private readonly float _halfMap;

    public IReadOnlyList<Plant> Plants => _plants;

    public PlantField(SimTerrain terrain, int seed, int capacity = DefaultCapacity)
    {
        _terrain = terrain;
        _rng = new SimRandom((ulong)(seed + 401), stream: 401);
        _plants = new Plant[capacity];
        _halfMap = terrain.MapSize / 2f;
        _cellsPerSide = (int)MathF.Ceiling(terrain.MapSize / CellSize);
        _cells = new List<int>[_cellsPerSide * _cellsPerSide];
        for (var i = 0; i < _cells.Length; i++) _cells[i] = new List<int>();
        _cellOf = Enumerable.Repeat(-1, capacity).ToArray();
        for (var i = 0; i < capacity; i++)
        {
            var position = FertileSpot();
            var sample = terrain.Sample(position.X, position.Z);
            _plants[i] = new Plant
            {
                Position = position,
                Biomass = _rng.Range(0.25f, 1f),
                GrowthRate = SimClock.Lerp(0.002f, 0.012f, sample.Humidity),
                Thorny = _rng.NextFloat() < 0.1f
            };
            PlaceInCell(i);
        }
    }

    public void Step(float dt, float lightFactor, float growthMultiplier)
    {
        var light = MathF.Max(0.05f, lightFactor);
        for (var i = 0; i < _plants.Length; i++)
        {
            var plant = _plants[i];
            var rate = plant.GrowthRate * light * growthMultiplier;
            plant.Biomass = Math.Clamp(plant.Biomass + dt * rate * plant.Biomass * (1f - plant.Biomass), 0f, 1f);
            // Logistic growth is stuck at 0, so depleted plants relocate to a fertile spot instead.
            if (plant.Biomass <= Depleted)
            {
                plant.DepletedSeconds += dt;
                if (plant.DepletedSeconds >= RespawnDelaySeconds) Relocate(i, plant);
            }
        }
    }

    /// <summary>True when a live plant is within <paramref name="radius"/> (probe only; eats nothing).</summary>
    public bool HasPlantWithin(Vector3 position, float radius)
    {
        var (minX, maxX, minZ, maxZ) = CellRange(position, radius);
        for (var cz = minZ; cz <= maxZ; cz++)
        for (var cx = minX; cx <= maxX; cx++)
            foreach (var i in _cells[cz * _cellsPerSide + cx])
                if (_plants[i].Biomass > Depleted && Vector3.Distance(position, _plants[i].Position) < radius) return true;
        return false;
    }

    /// <summary>Total live biomass within <paramref name="radius"/> (reports).</summary>
    public float BiomassWithin(Vector3 position, float radius)
    {
        var total = 0f;
        var (minX, maxX, minZ, maxZ) = CellRange(position, radius);
        for (var cz = minZ; cz <= maxZ; cz++)
        for (var cx = minX; cx <= maxX; cx++)
            foreach (var i in _cells[cz * _cellsPerSide + cx])
                if (Vector3.Distance(position, _plants[i].Position) < radius) total += _plants[i].Biomass;
        return total;
    }

    public float ConsumeNearest(Vector3 position, float radius, float maximumAmount, out bool thorny)
    {
        thorny = false;
        var best = -1;
        var distance = radius;
        var (minX, maxX, minZ, maxZ) = CellRange(position, radius);
        for (var cz = minZ; cz <= maxZ; cz++)
        for (var cx = minX; cx <= maxX; cx++)
            foreach (var i in _cells[cz * _cellsPerSide + cx])
            {
                if (_plants[i].Biomass <= Depleted) continue;
                var candidate = Vector3.Distance(position, _plants[i].Position);
                if (candidate >= distance) continue;
                distance = candidate;
                best = i;
            }
        if (best < 0) return 0f;
        var amount = MathF.Min(maximumAmount, _plants[best].Biomass);
        _plants[best].Biomass -= amount;
        thorny = _plants[best].Thorny;
        return amount;
    }

    /// <summary>Edible plants whose base lies within <paramref name="range"/> of <paramref name="eye"/> (horizontal).</summary>
    public void CollectNear(Vector3 eye, float range, List<(Vector3 Base, bool Thorny)> output)
    {
        output.Clear();
        var r2 = range * range;
        var (minX, maxX, minZ, maxZ) = CellRange(eye, range);
        for (var cz = minZ; cz <= maxZ; cz++)
        for (var cx = minX; cx <= maxX; cx++)
            foreach (var index in _cells[cz * _cellsPerSide + cx])
            {
                var plant = _plants[index];
                if (plant.Biomass <= Depleted) continue;
                var dx = plant.Position.X - eye.X;
                var dz = plant.Position.Z - eye.Z;
                if (dx * dx + dz * dz > r2) continue;
                output.Add((plant.Position, plant.Thorny));
            }
    }

    public float Scent(Vector3 position)
    {
        // Beyond ~40 m a plant contributes < 1/1600 of a nearby one; skipping far cells keeps this O(local).
        const float range = 40f;
        var scent = 0f;
        var (minX, maxX, minZ, maxZ) = CellRange(position, range);
        for (var cz = minZ; cz <= maxZ; cz++)
        for (var cx = minX; cx <= maxX; cx++)
            foreach (var i in _cells[cz * _cellsPerSide + cx])
            {
                var plant = _plants[i];
                if (plant.Biomass <= Depleted) continue;
                scent += plant.Biomass / MathF.Max(4f, Vector3.DistanceSquared(position, plant.Position));
            }
        return scent;
    }

    private (int MinX, int MaxX, int MinZ, int MaxZ) CellRange(Vector3 centre, float radius) =>
        (CellIndex(centre.X - radius), CellIndex(centre.X + radius), CellIndex(centre.Z - radius), CellIndex(centre.Z + radius));

    private int CellIndex(float coordinate) =>
        Math.Clamp((int)((coordinate + _halfMap) / CellSize), 0, _cellsPerSide - 1);

    private void PlaceInCell(int index)
    {
        var position = _plants[index].Position;
        var cell = CellIndex(position.Z) * _cellsPerSide + CellIndex(position.X);
        if (_cellOf[index] == cell) return;
        if (_cellOf[index] >= 0) _cells[_cellOf[index]].Remove(index);
        // Sorted by plant index: iteration (and float sums) never depends on relocation history,
        // so a restored snapshot continues exactly like the original.
        var list = _cells[cell];
        var at = list.BinarySearch(index);
        list.Insert(at < 0 ? ~at : at, index);
        _cellOf[index] = cell;
    }

    internal void Save(BinaryWriter writer)
    {
        var (state, increment) = _rng.Save();
        writer.Write(state);
        writer.Write(increment);
        writer.Write(_plants.Length);
        foreach (var p in _plants)
        {
            writer.Write(p.Position.X); writer.Write(p.Position.Y); writer.Write(p.Position.Z);
            writer.Write(p.Biomass); writer.Write(p.GrowthRate); writer.Write(p.Thorny); writer.Write(p.DepletedSeconds);
        }
    }

    internal void Restore(BinaryReader reader)
    {
        _rng.Restore(new SimRandom((reader.ReadUInt64(), reader.ReadUInt64())));
        var count = reader.ReadInt32();
        if (count != _plants.Length) throw new InvalidDataException($"snapshot has {count} plants, world has {_plants.Length}");
        foreach (var list in _cells) list.Clear();
        Array.Fill(_cellOf, -1);
        for (var i = 0; i < count; i++)
        {
            var p = _plants[i];
            p.Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            p.Biomass = reader.ReadSingle();
            p.GrowthRate = reader.ReadSingle();
            p.Thorny = reader.ReadBoolean();
            p.DepletedSeconds = reader.ReadSingle();
            PlaceInCell(i);
        }
    }

    private Vector3 FertileSpot() =>
        _terrain.RandomLandPoint(_rng, _halfMap - 12f, sample => sample.VegetationDensity >= _rng.NextFloat());

    private void Relocate(int index, Plant plant)
    {
        var position = FertileSpot();
        var sample = _terrain.Sample(position.X, position.Z);
        plant.Position = position;
        plant.Biomass = 0.3f;
        plant.GrowthRate = SimClock.Lerp(0.002f, 0.012f, sample.Humidity);
        plant.DepletedSeconds = 0f;
        PlaceInCell(index);
    }
}
