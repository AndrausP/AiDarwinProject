using System.Numerics;
using CreatureIA.Core.Physiology;
using CreatureIA.Core.Voxel;

namespace CreatureIA.Core.Simulation;

public enum SimEventKind : byte { Death = 1, Ate = 3, Damaged = 4 }

/// <summary>World event for the brain (protocol v2 DEATH / ATE / DAMAGED).</summary>
public readonly record struct SimEvent(SimEventKind Kind, uint CreatureId, byte Code, float Quantity, uint SourceId);

public sealed record SimWorldSettings
{
    public int Seed { get; init; } = 1337;
    public int Population { get; init; } = 48;
    public int Plants { get; init; } = PlantField.DefaultCapacity;
    public int Lakes { get; init; } = 6;
    /// <summary>Half-size of the square where creatures are born.</summary>
    public float SpawnExtentMeters { get; init; } = 120f;
}

/// <summary>
/// One headless world: voxel terrain, clock, plants, sounds and creatures, stepped at 60 Hz with no
/// engine. Port of the Godot World scene of etapa 1 (same update order: clock, plants, creatures,
/// sound expiry). Not thread-safe; different worlds can step on different threads.
/// </summary>
public sealed class SimWorld
{
    public const int PhysicsHz = 60;
    public const int PhysicsTicksPerBrainTick = 6;
    public const float PhysicsDelta = 1f / PhysicsHz;
    public const float VisionRange = 25f;
    public const float FieldOfViewDegrees = 210f;
    public const float HearingRange = 25f;
    public const float ThreatSmellRange = 15f;
    /// <summary>Sound lifetime in physics ticks (0.5 s).</summary>
    public const int SoundLifetimeTicks = 30;

    private readonly record struct SoundPulse(uint SourceId, Vector3 Position, float Intensity, float Tone, long CreatedTick);

    public SimWorldSettings Settings { get; }
    public SimTerrain Terrain { get; }
    public SimClock Clock { get; } = new();
    public PlantField Plants { get; }
    public long PhysicsTick { get; private set; }
    public IReadOnlyList<SimCreature> Creatures => _creatures;

    private readonly List<SimCreature> _creatures = new();
    private readonly HashSet<uint> _usedIds = new();
    private readonly SimRandom _spawnRng;
    private readonly Dictionary<uint, SoundPulse> _sounds = new();
    private readonly List<uint> _expired = new();
    private readonly List<SimEvent> _events = new();
    private readonly List<(Vector3 Base, bool Thorny)> _nearPlants = new();
    private readonly List<SimCreature> _nearCreatures = new();

    public SimWorld(SimWorldSettings settings, IEnumerable<uint>? initialIds = null)
    {
        Settings = settings;
        Terrain = new SimTerrain(VoxelWorldGenerator.Generate(settings.Seed, new VoxelWorldSettings { LakeCount = settings.Lakes }));
        Plants = new PlantField(Terrain, settings.Seed, settings.Plants);
        _spawnRng = new SimRandom((ulong)settings.Seed, stream: 7);
        foreach (var id in initialIds ?? Enumerable.Range(1, settings.Population).Select(i => (uint)i)) Spawn(id, null);
    }

    /// <summary>Creates a creature with a never-used id; null when the id was already used.</summary>
    public SimCreature? Spawn(uint id, Vector3? position)
    {
        if (!_usedIds.Add(id)) return null;
        var color = Hsv(_spawnRng.NextFloat(), 0.55f, 0.9f);
        var place = position ?? Terrain.RandomLandPoint(_spawnRng, Settings.SpawnExtentMeters) + Vector3.UnitY * 0.05f;
        var creature = new SimCreature(id, place, color);
        _creatures.Add(creature);
        return creature;
    }

    /// <summary>Runs the physics ticks of one brain tick (6 × 1/60 s).</summary>
    public void StepBrainTick()
    {
        for (var i = 0; i < PhysicsTicksPerBrainTick; i++) StepPhysics();
    }

    public void StepPhysics()
    {
        var dt = PhysicsDelta;
        Clock.Advance(dt);
        Plants.Step(dt, Clock.LightFactor, Clock.GrowthMultiplier);
        foreach (var creature in _creatures) creature.Step(this, dt);
        _creatures.RemoveAll(creature => !creature.IsAlive);
        PhysicsTick++;
        _expired.Clear();
        foreach (var (sourceId, sound) in _sounds)
            if (PhysicsTick - sound.CreatedTick >= SoundLifetimeTicks) _expired.Add(sourceId);
        foreach (var sourceId in _expired) _sounds.Remove(sourceId);
    }

    /// <summary>Events since the last call, in the order they happened.</summary>
    public SimEvent[] TakeEvents()
    {
        var events = _events.ToArray();
        _events.Clear();
        return events;
    }

    internal void RecordDeath(uint id, DeathCause cause) => _events.Add(new SimEvent(SimEventKind.Death, id, (byte)cause, 0f, 0));
    internal void RecordAte(uint id, byte resource, float amount) => _events.Add(new SimEvent(SimEventKind.Ate, id, resource, amount, 0));
    internal void RecordDamage(uint id, BodyZone zone, uint source) => _events.Add(new SimEvent(SimEventKind.Damaged, id, (byte)zone, 0f, source));

    internal void EmitSound(uint sourceId, Vector3 position, float intensity, float tone = 0f)
    {
        intensity = Math.Clamp(intensity, 0f, 1f);
        if (intensity <= 0.01f) return;
        _sounds[sourceId] = new SoundPulse(sourceId, position, intensity, Math.Clamp(tone, -1f, 1f), PhysicsTick);
    }

    /// <summary>Writes the 1000-float observation v2 of <paramref name="creature"/> and updates its perceived threat.</summary>
    public void Observe(SimCreature creature, Span<float> observation)
    {
        if (observation.Length != ObservationLayout.Total) throw new ArgumentException("observation must have 1000 floats");
        observation.Clear();
        Span<float> sectorSums = stackalloc float[4 * 5];
        Span<int> sectorHits = stackalloc int[4];
        var origin = creature.Position + Vector3.UnitY * SimCreature.EyeHeight;
        var range = VisionRange * Clock.VisionMultiplier;
        var grid = Terrain.Grid;
        Plants.CollectNear(origin, range + PlantField.VisionRadius, _nearPlants);
        CollectCreaturesNear(origin, range + SimCreature.VisionRadius, creature.Id, _nearCreatures);
        var threat = 0f;
        const int rays = ObservationLayout.HorizontalRays;

        for (var level = 0; level < ObservationLayout.VerticalLevels; level++)
        {
            var pitch = (level - 1) * 13f * (MathF.PI / 180f) + creature.HeadPitchRadians;
            var (sinPitch, cosPitch) = MathF.SinCos(pitch);
            for (var ray = 0; ray < rays; ray++)
            {
                var yaw = (-FieldOfViewDegrees * 0.5f + FieldOfViewDegrees * ray / (rays - 1f)) * (MathF.PI / 180f);
                var (sinYaw, cosYaw) = MathF.SinCos(yaw);
                var direction = Vector3.Normalize(creature.ToWorld(new Vector3(sinYaw * cosPitch, sinPitch, -cosYaw * cosPitch)));
                var offset = ObservationLayout.VisionOffset + (level * rays + ray) * ObservationLayout.FeaturesPerRay;

                var bestDistance = float.PositiveInfinity;
                var type = VisionClass.None;
                SimCreature? seen = null;
                var hit = VoxelRaycast.Cast(grid, origin.X, origin.Y, origin.Z, direction.X, direction.Y, direction.Z, range);
                if (hit.Hit)
                {
                    bestDistance = hit.Distance;
                    type = VisionClass.OfBlock(hit.Block);
                }
                foreach (var (plantBase, thorny) in _nearPlants)
                {
                    var t = VisionGeometry.RayVerticalCylinder(origin.X, origin.Y, origin.Z, direction.X, direction.Y, direction.Z,
                        plantBase.X, plantBase.Y, plantBase.Z, PlantField.VisionRadius, PlantField.VisionHeight, range);
                    if (t is not { } d || d >= bestDistance) continue;
                    bestDistance = d;
                    type = thorny ? VisionClass.Thorny : VisionClass.Plant;
                }
                foreach (var other in _nearCreatures)
                {
                    var b = other.Position;
                    var t = VisionGeometry.RayVerticalCylinder(origin.X, origin.Y, origin.Z, direction.X, direction.Y, direction.Z,
                        b.X, b.Y, b.Z, SimCreature.VisionRadius * other.SizeScale, SimCreature.Shape.Height * other.SizeScale, range);
                    if (t is not { } d || d >= bestDistance) continue;
                    bestDistance = d;
                    type = VisionClass.Creature;
                    seen = other;
                }

                if (type == VisionClass.None)
                {
                    observation[offset] = 1f;
                    observation[offset + 1] = 1f;
                    continue;
                }
                observation[offset] = Math.Clamp(bestDistance / range, 0f, 1f);
                observation[offset + 1 + type] = 1f;
                if (seen is null) continue;
                var threatness = FearModel.Threatness(seen.SizeScale, creature.SizeScale, seen.IsCarnivore);
                threat = MathF.Max(threat, FearModel.Perceived(threatness, bestDistance, range));
                if (level != 1) continue;
                var sector = ray * 4 / rays;
                sectorSums[sector * 5] += seen.Color.R;
                sectorSums[sector * 5 + 1] += seen.Color.G;
                sectorSums[sector * 5 + 2] += seen.Color.B;
                sectorSums[sector * 5 + 3] += seen.SizeScale / MathF.Max(1e-3f, creature.SizeScale);
                sectorSums[sector * 5 + 4] += seen.IsCarnivore ? 1f : 0f;
                sectorHits[sector]++;
            }
        }

        for (var sector = 0; sector < 4; sector++)
        {
            if (sectorHits[sector] == 0) continue;
            for (var k = 0; k < 5; k++)
                observation[ObservationLayout.CreatureSectorOffset + sector * 5 + k] = sectorSums[sector * 5 + k] / sectorHits[sector];
            // Relative size is unbounded above; keep it in a sane range for the network.
            var i = ObservationLayout.CreatureSectorOffset + sector * 5 + 3;
            observation[i] = Math.Clamp(observation[i], 0f, 4f);
        }

        WriteAudio(observation, creature);
        threat = MathF.Max(threat, WriteOlfaction(observation, creature));
        creature.LastThreat = threat;
        WriteProprioception(observation, creature);
        WriteInteroception(observation, creature);
        observation[ObservationLayout.TimeOffset] = MathF.Sin(Clock.TimeOfDay01 * MathF.Tau);
        observation[ObservationLayout.TimeOffset + 1] = MathF.Cos(Clock.TimeOfDay01 * MathF.Tau);
    }

    private void CollectCreaturesNear(Vector3 point, float range, uint excludeId, List<SimCreature> output)
    {
        output.Clear();
        var r2 = range * range;
        foreach (var other in _creatures)
        {
            if (other.Id == excludeId || !other.IsAlive) continue;
            var dx = other.Position.X - point.X;
            var dz = other.Position.Z - point.Z;
            if (dx * dx + dz * dz <= r2) output.Add(other);
        }
    }

    private void WriteAudio(Span<float> observation, SimCreature listener)
    {
        Span<float> intensities = stackalloc float[4];
        Span<float> weightedTone = stackalloc float[4];
        foreach (var sound in _sounds.Values)
        {
            if (sound.SourceId == listener.Id) continue;
            var delta = sound.Position - listener.Position;
            var distance = delta.Length();
            if (distance > HearingRange || distance <= 0.001f) continue;
            var local = listener.ToLocal(delta);
            var quadrant = local.Z < 0f ? (local.X < 0f ? 0 : 1) : (local.X < 0f ? 2 : 3);
            var perceived = sound.Intensity * (1f - distance / HearingRange);
            intensities[quadrant] += perceived;
            weightedTone[quadrant] += sound.Tone * perceived;
        }
        for (var quadrant = 0; quadrant < 4; quadrant++)
        {
            var intensity = Math.Clamp(intensities[quadrant], 0f, 1f);
            observation[ObservationLayout.AudioOffset + quadrant * 2] = intensity;
            observation[ObservationLayout.AudioOffset + quadrant * 2 + 1] = intensity > 0f ? weightedTone[quadrant] / intensities[quadrant] : 0f;
        }
    }

    /// <summary>Writes the four scent gradients; returns the strongest threat smelled (0..1).</summary>
    private float WriteOlfaction(Span<float> observation, SimCreature listener)
    {
        var body = listener.Position;
        var nose = body + listener.Forward * 0.6f;
        var side = body + listener.Right * 0.6f;
        var o = ObservationLayout.OlfactionOffset;
        WriteGradient(observation, o, Plants.Scent(body), Plants.Scent(nose), Plants.Scent(side));
        WriteGradient(observation, o + 3, Terrain.WaterScent(body), Terrain.WaterScent(nose), Terrain.WaterScent(side));
        WriteGradient(observation, o + 6, CreatureScent(body, listener, false), CreatureScent(nose, listener, false), CreatureScent(side, listener, false));
        WriteGradient(observation, o + 9, CreatureScent(body, listener, true), CreatureScent(nose, listener, true), CreatureScent(side, listener, true));
        var strongest = 0f;
        foreach (var other in _creatures)
        {
            if (other.Id == listener.Id || !other.IsAlive) continue;
            var threatness = FearModel.Threatness(other.SizeScale, listener.SizeScale, other.IsCarnivore);
            if (threatness <= 0f) continue;
            strongest = MathF.Max(strongest, FearModel.Perceived(threatness, Vector3.Distance(body, other.Position), ThreatSmellRange));
        }
        return strongest;
    }

    /// <summary>Scent of other live creatures; with <paramref name="threatOnly"/>, weighted by how threatening each is.</summary>
    private float CreatureScent(Vector3 position, SimCreature listener, bool threatOnly)
    {
        var scent = 0f;
        foreach (var other in _creatures)
        {
            if (other.Id == listener.Id || !other.IsAlive) continue;
            var weight = 1f;
            if (threatOnly)
            {
                weight = FearModel.Threatness(other.SizeScale, listener.SizeScale, other.IsCarnivore);
                if (weight <= 0f) continue;
            }
            scent += weight / MathF.Max(4f, Vector3.DistanceSquared(position, other.Position));
        }
        return scent;
    }

    private static void WriteGradient(Span<float> observation, int offset, float body, float nose, float right)
    {
        observation[offset] = Math.Clamp(body, 0f, 1f);
        observation[offset + 1] = Math.Clamp(nose - body, -1f, 1f);
        observation[offset + 2] = Math.Clamp(right - body, -1f, 1f);
    }

    private static void WriteProprioception(Span<float> observation, SimCreature creature)
    {
        var local = creature.ToLocal(creature.Velocity);
        var i = ObservationLayout.ProprioceptionOffset;
        observation[i++] = local.X / 10f;
        observation[i++] = local.Y / 10f;
        observation[i++] = local.Z / 10f;
        observation[i++] = creature.LastTurnAction;
        observation[i++] = creature.OnFloor ? 1f : 0f;
        observation[i++] = creature.HeadPitchRadians;
        observation[i++] = creature.Sleeping ? 1f : 0f;
        observation[i++] = creature.LastMove.InWater ? 1f : 0f;
        observation[i++] = creature.Climbing ? 1f : 0f;
        observation[i] = creature.LastMove.HitWall ? 1f : 0f;
    }

    private static void WriteInteroception(Span<float> observation, SimCreature creature)
    {
        var s = creature.Physiology;
        var age = (float)s.AgeSeconds;
        var i = ObservationLayout.InteroceptionOffset;
        observation[i++] = (float)s.Energy;
        observation[i++] = (float)s.Water;
        observation[i++] = (float)s.BodyTemperature;
        observation[i++] = (float)s.HeadIntegrity;
        observation[i++] = (float)s.TorsoIntegrity;
        observation[i++] = (float)s.LegsIntegrity;
        observation[i++] = (float)s.Fatigue;
        observation[i++] = (float)s.Pain;
        observation[i++] = (float)s.Libido;
        observation[i++] = Math.Clamp(age / creature.MaximumAgeSeconds, 0f, 1f);
        observation[i++] = age >= creature.MaximumAgeSeconds * 0.2f ? 1f : 0f;
        observation[i] = creature.Fear;
    }

    private static (float R, float G, float B) Hsv(float h, float s, float v)
    {
        var sector = h * 6f;
        var i = (int)MathF.Floor(sector) % 6;
        var f = sector - MathF.Floor(sector);
        var p = v * (1f - s);
        var q = v * (1f - f * s);
        var t = v * (1f - (1f - f) * s);
        return i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q)
        };
    }
}
