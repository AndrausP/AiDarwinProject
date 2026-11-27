using CreatureIA.Godot.Creature;
using Godot;

namespace CreatureIA.Godot.World;

public partial class SensoryField : Node
{
    private readonly record struct SoundPulse(uint SourceId, Vector3 Position, float Intensity, float Tone, ulong CreatedPhysicsFrame);

    /// <summary>Sound lifetime in simulated physics ticks (0.5 s at 60 Hz), independent of wall-clock speed.</summary>
    public const ulong SoundLifetimePhysicsFrames = 30;

    [Export] public NodePath PlantsPath { get; set; } = "../PlantManager";
    [Export] public NodePath WaterPath { get; set; } = "../WaterBodies";
    [Export] public NodePath CreaturesPath { get; set; } = "../Creatures";
    [Export] public float HearingRange { get; set; } = 25f;

    // One active pulse per source: re-emitting refreshes it instead of stacking duplicates every tick.
    private readonly Dictionary<uint, SoundPulse> _sounds = new();
    private readonly List<uint> _expired = new();
    private PlantManager _plants = null!;
    private WaterBodies _water = null!;
    private Node _creatures = null!;
    private readonly List<(uint Id, Vector3 Position, CreatureBody Body)> _liveCreatures = new();
    private ulong _snapshotFrame = ulong.MaxValue;

    public override void _Ready()
    {
        AddToGroup("sensory_field");
        _plants = GetNode<PlantManager>(PlantsPath);
        _water = GetNode<WaterBodies>(WaterPath);
        _creatures = GetNode(CreaturesPath);
    }

    public override void _PhysicsProcess(double delta)
    {
        var now = Engine.GetPhysicsFrames();
        _expired.Clear();
        foreach (var (sourceId, sound) in _sounds)
            if (now - sound.CreatedPhysicsFrame >= SoundLifetimePhysicsFrames) _expired.Add(sourceId);
        foreach (var sourceId in _expired) _sounds.Remove(sourceId);
    }

    public void EmitSound(uint sourceId, Vector3 position, float intensity, float tone = 0f)
    {
        intensity = Mathf.Clamp(intensity, 0f, 1f);
        if (intensity <= 0.01f) return;
        _sounds[sourceId] = new SoundPulse(sourceId, position, intensity, Mathf.Clamp(tone, -1f, 1f), Engine.GetPhysicsFrames());
    }

    public void WriteAudio(float[] observation, CreatureBody listener)
    {
        Span<float> intensities = stackalloc float[4];
        Span<float> weightedTone = stackalloc float[4];
        var listenerPosition = listener.GlobalPosition;
        var inverseBasis = listener.GlobalTransform.Basis.Inverse();
        foreach (var sound in _sounds.Values)
        {
            if (sound.SourceId == listener.CreatureId) continue;
            var delta = sound.Position - listenerPosition;
            var distance = delta.Length();
            if (distance > HearingRange || distance <= 0.001f) continue;
            var local = inverseBasis * delta;
            var quadrant = local.Z < 0f ? (local.X < 0f ? 0 : 1) : (local.X < 0f ? 2 : 3);
            var perceived = sound.Intensity * (1f - distance / HearingRange);
            intensities[quadrant] += perceived;
            weightedTone[quadrant] += sound.Tone * perceived;
        }
        for (var quadrant = 0; quadrant < 4; quadrant++)
        {
            var intensity = Mathf.Clamp(intensities[quadrant], 0f, 1f);
            observation[ObservationLayout.AudioOffset + quadrant * 2] = intensity;
            observation[ObservationLayout.AudioOffset + quadrant * 2 + 1] = intensity > 0f
                ? weightedTone[quadrant] / intensities[quadrant]
                : 0f;
        }
    }

    /// <summary>Range at which a threatening creature can still be smelled, metres.</summary>
    public const float ThreatSmellRange = 15f;

    /// <summary>Writes the scent gradients (protocol v3, log scale); returns the strongest threat smelled (0..1), for fear.</summary>
    public float WriteOlfaction(float[] observation, CreatureBody listener)
    {
        var basis = listener.GlobalTransform.Basis;
        var forward = -basis.Z;
        var right = basis.X;
        var body = listener.GlobalPosition;
        var nose = body + forward * 0.6f;
        var side = body + right * 0.6f;
        WriteGradient(observation, ObservationLayout.OlfactionOffset + ObservationLayout.SmellPlants, _plants.SampleScent(body),
            _plants.SampleScent(nose), _plants.SampleScent(side));
        WriteGradient(observation, ObservationLayout.OlfactionOffset + ObservationLayout.SmellFreshWater, _water.SampleScent(body),
            _water.SampleScent(nose), _water.SampleScent(side));
        RefreshCreatureSnapshot();
        WriteGradient(observation, ObservationLayout.OlfactionOffset + ObservationLayout.SmellCreatures, CreatureScent(body, listener.CreatureId),
            CreatureScent(nose, listener.CreatureId), CreatureScent(side, listener.CreatureId));
        WriteGradient(observation, ObservationLayout.OlfactionOffset + ObservationLayout.SmellThreat, ThreatScent(body, listener),
            ThreatScent(nose, listener), ThreatScent(side, listener));
        var strongest = 0f;
        foreach (var (id, position, other) in _liveCreatures)
        {
            if (id == listener.CreatureId) continue;
            var threatness = CreatureIA.Core.Physiology.FearModel.Threatness(other.SizeScale, listener.SizeScale, other.IsCarnivore);
            if (threatness <= 0f) continue;
            strongest = Mathf.Max(strongest,
                CreatureIA.Core.Physiology.FearModel.Perceived(threatness, body.DistanceTo(position), ThreatSmellRange));
        }
        return strongest;
    }

    /// <summary>Like creature scent, but only from animals that threaten the listener (bigger or carnivore).</summary>
    private float ThreatScent(Vector3 position, CreatureBody listener)
    {
        var scent = 0f;
        foreach (var (id, creaturePosition, other) in _liveCreatures)
        {
            if (id == listener.CreatureId) continue;
            var threatness = CreatureIA.Core.Physiology.FearModel.Threatness(other.SizeScale, listener.SizeScale, other.IsCarnivore);
            if (threatness <= 0f) continue;
            scent += threatness / Mathf.Max(4f, position.DistanceSquaredTo(creaturePosition));
        }
        return scent;
    }

    /// <summary>Live creatures (other than <paramref name="excludeId"/>) whose feet lie within <paramref name="range"/>.</summary>
    public void CollectCreaturesNear(Vector3 point, float range, uint excludeId, List<CreatureBody> output)
    {
        output.Clear();
        RefreshCreatureSnapshot();
        var r2 = range * range;
        foreach (var (id, position, body) in _liveCreatures)
        {
            if (id == excludeId) continue;
            var dx = position.X - point.X;
            var dz = position.Z - point.Z;
            if (dx * dx + dz * dz <= r2) output.Add(body);
        }
    }

    /// <summary>Positions only change during physics steps, so one snapshot per physics frame suffices.</summary>
    private void RefreshCreatureSnapshot()
    {
        var frame = Engine.GetPhysicsFrames();
        if (frame == _snapshotFrame) return;
        _snapshotFrame = frame;
        _liveCreatures.Clear();
        foreach (var node in _creatures.GetChildren())
            if (node is CreatureBody creature && creature.State.IsAlive)
                _liveCreatures.Add((creature.CreatureId, creature.GlobalPosition, creature));
    }

    private float CreatureScent(Vector3 position, uint listenerId)
    {
        var scent = 0f;
        foreach (var (id, creaturePosition, _) in _liveCreatures)
        {
            if (id == listenerId) continue;
            scent += 1f / Mathf.Max(4f, position.DistanceSquaredTo(creaturePosition));
        }
        return scent;
    }

    // v3: log-scale scents shared with the Sim (salt-water scent stays 0 until the sea field exists in Godot).
    private static void WriteGradient(float[] observation, int offset, float body, float nose, float right) =>
        ObservationLayout.WriteScent(observation, offset, body, nose, right);
}
