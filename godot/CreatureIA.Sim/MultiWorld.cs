using System.Buffers.Binary;
using CreatureIA.Core.Simulation;
using CreatureIA.Protocol;

namespace CreatureIA.Sim;

/// <summary>
/// N independent worlds stepped in lockstep and presented to the brain as ONE population: creature
/// ids are unique across worlds, so a single STEP carries every agent and the brain runs one batched
/// forward pass per tick. Worlds never interact; each is deterministic by its own seed.
/// </summary>
public sealed class MultiWorld
{
    private readonly SimWorld[] _worlds;
    private readonly Dictionary<uint, int> _worldOf = new();
    private readonly List<int> _deathWorlds = new();
    private readonly SimEvent[][] _eventsPerWorld;

    public IReadOnlyList<SimWorld> Worlds => _worlds;
    public int LiveCount => _worlds.Sum(w => w.Creatures.Count);

    public ExploitCounters Exploits()
    {
        var total = new ExploitCounters();
        foreach (var world in _worlds) total.Add(world.Exploits);
        return total;
    }

    /// <param name="replicas">
    /// false: world w uses seed Seed + w (training). true: every world uses the terrain of Seed with
    /// salt w (evaluation replicas on the reserved seed).
    /// </param>
    public MultiWorld(SimWorldSettings baseSettings, int worldCount, bool replicas = false)
    {
        _worlds = new SimWorld[worldCount];
        _eventsPerWorld = new SimEvent[worldCount][];
        var population = baseSettings.Population;
        Parallel.For(0, worldCount, w =>
        {
            var ids = Enumerable.Range(0, population).Select(i => (uint)(w * population + i + 1));
            var settings = replicas ? baseSettings with { Salt = w } : baseSettings with { Seed = baseSettings.Seed + w };
            _worlds[w] = new SimWorld(settings, ids);
        });
        for (var w = 0; w < worldCount; w++)
            foreach (var creature in _worlds[w].Creatures) _worldOf[creature.Id] = w;
    }

    /// <summary>Resumes worlds saved by <see cref="SaveAll"/> (world-0.bin, world-1.bin, …).</summary>
    public MultiWorld(string directory, int worldCount)
    {
        _worlds = new SimWorld[worldCount];
        _eventsPerWorld = new SimEvent[worldCount][];
        Parallel.For(0, worldCount, w => _worlds[w] = SimWorld.LoadFile(Path.Combine(directory, $"world-{w}.bin")));
        for (var w = 0; w < worldCount; w++)
            foreach (var creature in _worlds[w].Creatures) _worldOf[creature.Id] = w;
    }

    public static bool HasSnapshots(string directory, int worldCount) =>
        Enumerable.Range(0, worldCount).All(w => File.Exists(Path.Combine(directory, $"world-{w}.bin")));

    public void SaveAll(string directory)
    {
        Directory.CreateDirectory(directory);
        Parallel.For(0, _worlds.Length, w => _worlds[w].SaveFile(Path.Combine(directory, $"world-{w}.bin")));
    }

    /// <summary>6 physics ticks in every world, worlds in parallel.</summary>
    public void StepBrainTick() => Parallel.For(0, _worlds.Length, w => _worlds[w].StepBrainTick());

    /// <summary>Observations of every live creature (sorted by id) and the events since the last call.</summary>
    public StepMessage BuildStep(ulong tick)
    {
        var perWorld = new StepAgent[_worlds.Length][];
        Parallel.For(0, _worlds.Length, w =>
        {
            var world = _worlds[w];
            _eventsPerWorld[w] = world.TakeEvents();
            var agents = new StepAgent[world.Creatures.Count];
            for (var i = 0; i < agents.Length; i++)
            {
                var creature = world.Creatures[i];
                var observation = new float[ObservationLayout.Total];
                world.Observe(creature, observation);
                agents[i] = new StepAgent(creature.Id, observation);
            }
            perWorld[w] = agents;
        });
        var events = new List<ProtocolEvent>();
        _deathWorlds.Clear();
        for (var w = 0; w < _worlds.Length; w++)
            foreach (var e in _eventsPerWorld[w])
            {
                events.Add(Encode(e));
                if (e.Kind == SimEventKind.Death) _deathWorlds.Add(w);
            }
        var all = perWorld.SelectMany(a => a).OrderBy(a => a.Id).ToArray();
        return new StepMessage(tick, all, events);
    }

    /// <summary>Applies ACT; the k-th SPAWN replaces the k-th death of this tick in that death's world.</summary>
    public void Apply(ActMessage act, IReadOnlyList<SpawnMessage> spawns)
    {
        var byId = new Dictionary<uint, SimCreature>();
        foreach (var world in _worlds)
            foreach (var creature in world.Creatures) byId[creature.Id] = creature;
        foreach (var agent in act.Agents)
            if (byId.TryGetValue(agent.Id, out var creature)) creature.SetAction(agent.Actions);
        for (var k = 0; k < spawns.Count; k++)
        {
            var spawn = spawns[k];
            var w = k < _deathWorlds.Count ? _deathWorlds[k] : SmallestWorld();
            var position = spawn.Position is { } p ? new System.Numerics.Vector3(p.X, p.Y, p.Z) : (System.Numerics.Vector3?)null;
            if (_worlds[w].Spawn(spawn.Id, position) is not null) _worldOf[spawn.Id] = w;
        }
    }

    private int SmallestWorld()
    {
        var best = 0;
        for (var w = 1; w < _worlds.Length; w++)
            if (_worlds[w].Creatures.Count < _worlds[best].Creatures.Count) best = w;
        return best;
    }

    private static ProtocolEvent Encode(SimEvent e)
    {
        switch (e.Kind)
        {
            case SimEventKind.Death:
            {
                var payload = new byte[5];
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), e.CreatureId);
                payload[4] = e.Code;
                return new ProtocolEvent(EventType.Death, payload);
            }
            case SimEventKind.Ate:
            {
                var payload = new byte[9];
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), e.CreatureId);
                payload[4] = e.Code;
                BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(5, 4), e.Quantity);
                return new ProtocolEvent(EventType.Ate, payload);
            }
            default:
            {
                var payload = new byte[9];
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), e.CreatureId);
                payload[4] = e.Code;
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(5, 4), e.SourceId);
                return new ProtocolEvent(EventType.Damaged, payload);
            }
        }
    }
}
