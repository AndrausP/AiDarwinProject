using System.Buffers.Binary;
using CreatureIA.Godot.Creature;
using CreatureIA.Godot.World;
using CreatureIA.Protocol;
using Godot;

namespace CreatureIA.Godot.Net;

public partial class SimulationCoordinator : Node
{
    [Export] public NodePath CreaturesPath { get; set; } = "../Creatures";
    [Export] public NodePath BridgePath { get; set; } = "../BrainBridge";
    [Export] public NodePath DayNightPath { get; set; } = "../DayNightCycle";
    [Export] public NodePath WorldPath { get; set; } = "..";
    [Export] public int WorldSeed { get; set; } = 1337;

    public ulong BrainTick { get; private set; }
    public ulong TicksSent { get; private set; }
    public ulong FailedTicks { get; private set; }

    private BrainBridge _bridge = null!;
    private DayNightCycle _dayNight = null!;
    private WorldRoot _world = null!;
    private Node _creatures = null!;
    private Task<ActionBatch>? _pending;
    private CancellationTokenSource? _exchangeCancellation;
    private RandomNumberGenerator _random = null!;
    private int _physicsTicks;
    private readonly List<ProtocolEvent> _events = new();
    private readonly HashSet<uint> _subscribedCreatures = new();

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        _bridge = GetNode<BrainBridge>(BridgePath);
        _dayNight = GetNode<DayNightCycle>(DayNightPath);
        _world = GetNode<WorldRoot>(WorldPath);
        _creatures = GetNode(CreaturesPath);
        _random = new RandomNumberGenerator { Seed = (ulong)(RunSettings.Seed(WorldSeed) + 17) };
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_pending is not null || ++_physicsTicks < ActionLatch.PhysicsTicksPerAction) return;
        _physicsTicks = 0;
        var live = LiveCreatures();
        if (ShouldStop(live.Length)) return;
        EnsureEventSubscriptions(live);
        if (!_bridge.Connected)
        {
            foreach (var creature in live) creature.ApplyAction(RandomAction());
            _events.Clear();
            BrainTick++;
            return;
        }

        var observeStarted = Time.GetTicksUsec();
        var batch = live
            .Select(creature => (creature.CreatureId, creature.BuildObservation(_dayNight.TimeOfDay01, _dayNight.VisionMultiplier)))
            .OrderBy(agent => agent.CreatureId)
            .ToArray();
        RecordProfile(Time.GetTicksUsec() - observeStarted);
        _exchangeCancellation = new CancellationTokenSource(_bridge.TimeoutMilliseconds);
        var events = _events.ToArray();
        _events.Clear();
        _pending = _bridge.ExchangeStepAsync(BrainTick, batch, events, _exchangeCancellation.Token);
        TicksSent++;
        if (_lastApplyUsec != 0) ProfileCounters.Stop("phase_sim", _lastApplyUsec);
        _sentUsec = ProfileCounters.Start();
        GetTree().Paused = true;
    }

    public override void _Process(double delta)
    {
        if (_pending is null || !_pending.IsCompleted) return;
        try
        {
            var batch = _pending.GetAwaiter().GetResult();
            var byId = LiveCreatures().ToDictionary(creature => creature.CreatureId);
            foreach (var (id, action) in batch.Actions)
                if (byId.TryGetValue(id, out var creature)) creature.ApplyAction(action);
            // Spawned creatures join before physics resumes and first appear in STEP(tick + 1).
            foreach (var spawn in batch.Spawns)
            {
                Vector3? position = spawn.Position is { } p ? new Vector3(p.X, p.Y, p.Z) : null;
                _world.SpawnCreature(spawn.Id, position);
            }
            BrainTick++;
            ProfileCounters.Stop("phase_wait", _sentUsec);
            _lastApplyUsec = ProfileCounters.Start();
        }
        catch (Exception exception)
        {
            FailedTicks++;
            GD.PushError($"Lockstep exchange failed at tick {BrainTick}: {exception.Message}");
        }
        finally
        {
            _exchangeCancellation?.Dispose();
            _exchangeCancellation = null;
            _pending = null;
            GetTree().Paused = false;
        }
    }

    public override void _ExitTree() => _exchangeCancellation?.Cancel();

    private bool _stopping;

    /// <summary>
    /// Benchmark horizon / extinction stop. Checked before building a STEP, so the DEATH events
    /// of the last creatures are still delivered by one final STEP before quitting.
    /// </summary>
    private bool ShouldStop(int liveCount)
    {
        if (_stopping) return true;
        var horizonReached = RunSettings.MaxBrainTicks > 0 && (long)BrainTick >= RunSettings.MaxBrainTicks;
        var extinct = RunSettings.QuitWhenExtinct && liveCount == 0 && _events.Count == 0 && BrainTick > 0;
        if (!horizonReached && !extinct) return false;
        _stopping = true;
        GD.Print($"[run] stopping at brain tick {BrainTick}: {(horizonReached ? "horizon" : "extinct")}; failed_ticks={FailedTicks}");
        GetTree().Quit();
        return true;
    }

    private static readonly bool ProfileEnabled =
        System.Environment.GetEnvironmentVariable("CREATURE_IA_PROFILE") == "1";
    private ulong _profileObserveUsec;
    private ulong _profileWindowStartUsec;
    private int _profileSamples;
    private ulong _sentUsec;
    private ulong _lastApplyUsec;

    /// <summary>Opt-in (CREATURE_IA_PROFILE=1) cost report for the 10 Hz observation build.</summary>
    private void RecordProfile(ulong observeUsec)
    {
        if (!ProfileEnabled) return;
        var now = Time.GetTicksUsec();
        if (_profileSamples == 0) _profileWindowStartUsec = now;
        _profileObserveUsec += observeUsec;
        if (++_profileSamples < 50) return;
        var windowMs = (now - _profileWindowStartUsec) / 1000d;
        GD.Print($"[profile] ticks={_profileSamples} observe_ms_avg={_profileObserveUsec / 1000d / _profileSamples:F2} " +
                 $"tick_interval_ms_avg={windowMs / (_profileSamples - 1):F2} rtt_ms_last={_bridge.LastRoundTripMilliseconds:F2} " +
                 $"fps={Engine.GetFramesPerSecond()} {ProfileCounters.DrainPerTick(_profileSamples)} hits=[{string.Join(',', Senses.HitCounts)}] drink=[{string.Join(',', CreatureBody.DrinkStats)}]");
        _profileObserveUsec = 0;
        _profileSamples = 0;
    }

    private CreatureBody[] LiveCreatures() => _creatures.GetChildren()
        .OfType<CreatureBody>()
        .Where(creature => creature.State.IsAlive)
        .ToArray();

    private float[] RandomAction()
    {
        var action = new float[ActionLatch.ActionCount];
        for (var i = 0; i < action.Length; i++) action[i] = _random.RandfRange(-1f, 1f);
        return action;
    }

    private void EnsureEventSubscriptions(IEnumerable<CreatureBody> creatures)
    {
        foreach (var creature in creatures)
        {
            if (!_subscribedCreatures.Add(creature.CreatureId)) continue;
            creature.DeathOccurred += OnDeath;
            creature.AteResource += OnAte;
            creature.DamageOccurred += OnDamaged;
        }
    }

    private void OnDeath(uint creatureId, CreatureIA.Core.Physiology.DeathCause cause)
    {
        var payload = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), creatureId);
        payload[4] = (byte)cause;
        _events.Add(new ProtocolEvent(EventType.Death, payload));
    }

    private void OnAte(uint creatureId, byte resourceType, float quantity)
    {
        var payload = new byte[9];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), creatureId);
        payload[4] = resourceType;
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(5, 4), quantity);
        _events.Add(new ProtocolEvent(EventType.Ate, payload));
    }

    private void OnDamaged(uint creatureId, BodyZone zone, uint sourceId)
    {
        var payload = new byte[9];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), creatureId);
        payload[4] = (byte)zone;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(5, 4), sourceId);
        _events.Add(new ProtocolEvent(EventType.Damaged, payload));
    }
}
