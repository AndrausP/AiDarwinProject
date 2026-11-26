using CreatureIA.Core.Physiology;
using CreatureIA.Core.Voxel;
using Godot;
using CreatureIA.Godot.Net;
using CreatureIA.Godot.World;

namespace CreatureIA.Godot.Creature;

/// <summary>Observation v2 (Task 002 etapa 1): 1000 floats. See contracts/protocol-v2.json.</summary>
public static class ObservationLayout
{
    public const int Rays = 72;
    public const int FeaturesPerRay = 13;
    public const int VisionOffset = 0;
    public const int VisionLength = Rays * FeaturesPerRay; // 936
    /// <summary>4 sectors × (r, g, b, relative size, carnivore) of creatures seen on the eye-level row.</summary>
    public const int CreatureSectorOffset = 936;
    public const int CreatureSectorLength = 20;
    public const int AudioOffset = 956;
    public const int AudioLength = 8;
    /// <summary>Gradients (body, nose − body, right − body) of plants, water, creatures, threat.</summary>
    public const int OlfactionOffset = 964;
    public const int OlfactionLength = 12;
    public const int ProprioceptionOffset = 976;
    public const int ProprioceptionLength = 10;
    public const int InteroceptionOffset = 986;
    public const int InteroceptionLength = 12;
    public const int TimeOffset = 998;
    public const int TimeLength = 2;
    public const int Total = 1000;
}

/// <summary>Per-ray class one-hot index (after the distance feature).</summary>
public static class VisionClass
{
    public const int None = 0;
    public const int Soil = 1;
    public const int Sand = 2;
    public const int Stone = 3;
    public const int Snow = 4;
    public const int Water = 5;
    public const int Wood = 6;
    public const int Leaves = 7;
    public const int Plant = 8;
    public const int Thorny = 9;
    public const int Meat = 10;
    public const int Creature = 11;
    public const int Count = 12;

    public static int OfBlock(BlockType block) => block switch
    {
        BlockType.Grass or BlockType.Dirt => Soil,
        BlockType.Sand => Sand,
        BlockType.Stone or BlockType.Bedrock or BlockType.Marble => Stone,
        BlockType.Snow => Snow,
        BlockType.Water => Water,
        BlockType.Wood or BlockType.BirchWood or BlockType.RedWood => Wood,
        // Cactus reads as foliage: a green plant body (its own class would change the protocol).
        BlockType.Leaves or BlockType.PineLeaves or BlockType.BirchLeaves or BlockType.AcaciaLeaves or BlockType.Cactus => Leaves,
        _ => None
    };
}

public partial class Senses : Node
{
    private const int HorizontalRays = 24;
    private const int VerticalLevels = 3;

    [Export] public float VisionRange { get; set; } = 25f;
    [Export] public float FieldOfViewDegrees { get; set; } = 210f;
    [Export] public bool DebugRays { get; set; }
    [Export] public NodePath SensoryFieldPath { get; set; } = "../../../SensoryField";
    public IReadOnlyList<(Vector3 From, Vector3 To, bool Hit)> DebugSegments => _debugSegments;

    /// <summary>Eye height above the feet, metres.</summary>
    public const float EyeHeight = 1.1f;

    /// <summary>Strongest threat perceived (seen or smelled) at the last observation, 0..1. Feeds fear.</summary>
    public float LastThreat { get; private set; }

    private readonly List<(Vector3 From, Vector3 To, bool Hit)> _debugSegments = new();
    private readonly List<(Vector3 Base, bool Thorny)> _nearPlants = new();
    private readonly List<CreatureBody> _nearCreatures = new();
    private SensoryField? _field;

    public override void _Ready()
    {
        _field = GetNodeOrNull<SensoryField>(SensoryFieldPath)
            ?? GetTree().GetFirstNodeInGroup("sensory_field") as SensoryField;
        if (_field is null)
            GD.PushError("Senses could not resolve the world's SensoryField; audio and olfaction would be unavailable.");
    }

    /// <summary>Profiling only: ray hits per vision class.</summary>
    public static readonly long[] HitCounts = new long[VisionClass.Count];

    public float[] Observe(CreatureBody creature, float timeOfDay01, float visionMultiplier)
    {
        var observation = new float[ObservationLayout.Total];
        Span<float> sectorSums = stackalloc float[4 * 5];
        Span<int> sectorHits = stackalloc int[4];
        var origin = creature.GlobalPosition + Vector3.Up * EyeHeight;
        var basis = creature.GlobalTransform.Basis;
        var headPitch = creature.HeadPitchRadians;
        var range = VisionRange * visionMultiplier;
        _debugSegments.Clear();
        var p = ProfileCounters.Start();
        var grid = creature.Biomes?.Grid;
        // Candidates once per observation; each ray then tests only these few.
        creature.Plants?.CollectNear(origin, range + PlantManager.VisionRadius, _nearPlants);
        _field?.CollectCreaturesNear(origin, range + CreatureBody.VisionRadius, creature.CreatureId, _nearCreatures);
        var threat = 0f;

        for (var level = 0; level < VerticalLevels; level++)
        {
            var pitch = Mathf.DegToRad((level - 1) * 13f) + headPitch;
            var (sinPitch, cosPitch) = Mathf.SinCos(pitch);
            for (var ray = 0; ray < HorizontalRays; ray++)
            {
                var yaw = Mathf.DegToRad(-FieldOfViewDegrees * 0.5f + FieldOfViewDegrees * ray / (HorizontalRays - 1f));
                var (sinYaw, cosYaw) = Mathf.SinCos(yaw);
                var local = new Vector3(sinYaw * cosPitch, sinPitch, -cosYaw * cosPitch);
                var direction = (basis * local).Normalized();
                var offset = ObservationLayout.VisionOffset + (level * HorizontalRays + ray) * ObservationLayout.FeaturesPerRay;

                var bestDistance = float.PositiveInfinity;
                var type = VisionClass.None;
                CreatureBody? seen = null;
                if (grid is not null)
                {
                    var hit = VoxelRaycast.Cast(grid, origin.X, origin.Y, origin.Z, direction.X, direction.Y, direction.Z, range);
                    if (hit.Hit)
                    {
                        bestDistance = hit.Distance;
                        type = VisionClass.OfBlock(hit.Block);
                    }
                }
                foreach (var (plantBase, thorny) in _nearPlants)
                {
                    var t = VisionGeometry.RayVerticalCylinder(origin.X, origin.Y, origin.Z, direction.X, direction.Y, direction.Z,
                        plantBase.X, plantBase.Y, plantBase.Z, PlantManager.VisionRadius, PlantManager.VisionHeight, range);
                    if (t is not { } d || d >= bestDistance) continue;
                    bestDistance = d;
                    type = thorny ? VisionClass.Thorny : VisionClass.Plant;
                }
                foreach (var other in _nearCreatures)
                {
                    var b = other.GlobalPosition;
                    var t = VisionGeometry.RayVerticalCylinder(origin.X, origin.Y, origin.Z, direction.X, direction.Y, direction.Z,
                        b.X, b.Y, b.Z, CreatureBody.VisionRadius * other.SizeScale, CreatureBody.Shape.Height * other.SizeScale, range);
                    if (t is not { } d || d >= bestDistance) continue;
                    bestDistance = d;
                    type = VisionClass.Creature;
                    seen = other;
                }

                if (type == VisionClass.None)
                {
                    if (DebugRays) _debugSegments.Add((origin, origin + direction * range, false));
                    observation[offset] = 1f;
                    observation[offset + 1] = 1f;
                    continue;
                }
                if (DebugRays) _debugSegments.Add((origin, origin + direction * bestDistance, true));
                observation[offset] = Mathf.Clamp(bestDistance / range, 0f, 1f);
                observation[offset + 1 + type] = 1f;
                if (ProfileCounters.Enabled) HitCounts[type]++;

                if (seen is not null)
                {
                    var threatness = FearModel.Threatness(seen.SizeScale, creature.SizeScale, seen.IsCarnivore);
                    threat = Mathf.Max(threat, FearModel.Perceived(threatness, bestDistance, range));
                    if (level == 1)
                    {
                        var sector = ray * 4 / HorizontalRays;
                        sectorSums[sector * 5] += seen.BodyColor.R;
                        sectorSums[sector * 5 + 1] += seen.BodyColor.G;
                        sectorSums[sector * 5 + 2] += seen.BodyColor.B;
                        sectorSums[sector * 5 + 3] += seen.SizeScale / Mathf.Max(1e-3f, creature.SizeScale);
                        sectorSums[sector * 5 + 4] += seen.IsCarnivore ? 1f : 0f;
                        sectorHits[sector]++;
                    }
                }
            }
        }
        ProfileCounters.Stop("vision", p);

        for (var sector = 0; sector < 4; sector++)
        {
            if (sectorHits[sector] == 0) continue;
            for (var k = 0; k < 5; k++)
                observation[ObservationLayout.CreatureSectorOffset + sector * 5 + k] = sectorSums[sector * 5 + k] / sectorHits[sector];
        }
        // Relative size is unbounded above; keep it in a sane range for the network.
        for (var sector = 0; sector < 4; sector++)
        {
            var i = ObservationLayout.CreatureSectorOffset + sector * 5 + 3;
            observation[i] = Mathf.Clamp(observation[i], 0f, 4f);
        }

        p = ProfileCounters.Start();
        _field?.WriteAudio(observation, creature);
        if (_field is not null) threat = Mathf.Max(threat, _field.WriteOlfaction(observation, creature));
        ProfileCounters.Stop("audio_smell", p);
        LastThreat = threat;
        WriteProprioception(observation, creature);
        WriteInteroception(observation, creature);
        observation[ObservationLayout.TimeOffset] = Mathf.Sin(timeOfDay01 * Mathf.Tau);
        observation[ObservationLayout.TimeOffset + 1] = Mathf.Cos(timeOfDay01 * Mathf.Tau);
        return observation;
    }

    private static void WriteProprioception(float[] observation, CreatureBody creature)
    {
        var localVelocity = creature.GlobalTransform.Basis.Inverse() * creature.Velocity;
        var i = ObservationLayout.ProprioceptionOffset;
        observation[i++] = localVelocity.X / 10f;
        observation[i++] = localVelocity.Y / 10f;
        observation[i++] = localVelocity.Z / 10f;
        observation[i++] = creature.LastTurnAction;
        observation[i++] = creature.IsOnFloor() ? 1f : 0f;
        observation[i++] = creature.HeadPitchRadians;
        observation[i++] = creature.State.Sleeping ? 1f : 0f;
        observation[i++] = creature.InWater ? 1f : 0f;
        observation[i++] = creature.IsClimbing ? 1f : 0f;
        observation[i] = creature.IsBlocked ? 1f : 0f;
    }

    private static void WriteInteroception(float[] observation, CreatureBody creature)
    {
        var state = creature.State;
        var i = ObservationLayout.InteroceptionOffset;
        observation[i++] = state.Energy;
        observation[i++] = state.Water;
        observation[i++] = state.BodyTemperatureCelsius;
        observation[i++] = state.HeadIntegrity;
        observation[i++] = state.TorsoIntegrity;
        observation[i++] = state.LegsIntegrity;
        observation[i++] = state.Fatigue;
        observation[i++] = state.Pain;
        observation[i++] = state.Libido;
        observation[i++] = Mathf.Clamp(state.AgeSeconds / state.MaximumAgeSeconds, 0f, 1f);
        observation[i++] = state.IsMature ? 1f : 0f;
        observation[i] = creature.Fear;
    }
}
