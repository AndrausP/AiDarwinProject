using System.Buffers.Binary;
using System.Net.Sockets;
using CreatureIA.Protocol;
using CreatureIA.Godot.World;
using Godot;

namespace CreatureIA.Godot.Net;

public sealed record ActionBatch(
    ulong Tick,
    IReadOnlyDictionary<uint, float[]> Actions,
    IReadOnlyList<SpawnMessage> Spawns);

public partial class BrainBridge : Node
{
    [Export] public string Host { get; set; } = "127.0.0.1";
    [Export] public int Port { get; set; } = 5555;
    [Export] public int TimeoutMilliseconds { get; set; } = 5000;
    [Export] public bool AutoConnect { get; set; } = true;
    public const int MaxSpawnsPerTick = 256;

    // Running state only after the HELLO -> CONFIG handshake: a bare TCP connection must not
    // let the coordinator send STEP before HELLO (Disconnected -> Hello -> Configured -> Running).
    public bool Connected => Configuration is not null && _stream is not null && _client?.Connected == true;
    public double LastRoundTripMilliseconds { get; private set; }
    public ConfigMessage? Configuration { get; private set; }

    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _exchangeLock = new(1, 1);

    public override async void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        if (!AutoConnect) return;
        try { await ConnectAsync((ulong)RunSettings.Seed(1337), CancellationToken.None); }
        catch (Exception exception) { GD.PushWarning($"Brain server unavailable, using seeded random actions: {exception.Message}"); }
    }

    public async Task ConnectAsync(ulong worldSeed, CancellationToken cancellationToken)
    {
        if (Connected) return;
        _client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeoutMilliseconds);
        await _client.ConnectAsync(Host, RunSettings.Port(Port), timeout.Token);
        _stream = _client.GetStream();
        await WriteMessageAsync(new HelloMessage(
            ProtocolConstants.Version,
            ProtocolConstants.ObservationDimensions,
            ProtocolConstants.ActionDimensions,
            worldSeed), timeout.Token);
        Configuration = await ReadMessageAsync(timeout.Token) as ConfigMessage
            ?? throw new InvalidDataException("Expected CONFIG after HELLO.");
        if (Configuration.PhysicsHz != 60 || Configuration.BrainHz != 10)
            throw new InvalidDataException("Server cadence is incompatible with the world cadence.");
    }

    public async Task<ActionBatch> ExchangeStepAsync(
        ulong tick,
        IReadOnlyList<(uint Id, float[] Observation)> agents,
        IReadOnlyList<ProtocolEvent> events,
        CancellationToken cancellationToken)
    {
        if (_stream is null) throw new InvalidOperationException("Bridge is not connected.");
        await _exchangeLock.WaitAsync(cancellationToken);
        try
        {
            var step = new StepMessage(tick,
                agents.OrderBy(agent => agent.Id)
                    .Select(agent => new StepAgent(agent.Id, agent.Observation))
                    .ToArray(),
                events);
            var started = Time.GetTicksUsec();
            await WriteMessageAsync(step, cancellationToken);
            // Python may send SPAWN frames (births/respawns) before the single ACT of this tick.
            var spawns = new List<SpawnMessage>();
            IProtocolMessage response;
            while ((response = await ReadMessageAsync(cancellationToken)) is SpawnMessage spawn)
            {
                if (spawns.Count >= MaxSpawnsPerTick)
                    throw new InvalidDataException($"More than {MaxSpawnsPerTick} SPAWN frames in one tick.");
                spawns.Add(spawn);
            }
            LastRoundTripMilliseconds = (Time.GetTicksUsec() - started) / 1000d;
            if (response is not ActMessage act) throw new InvalidDataException("Expected ACT after STEP.");
            ProtocolCodec.ValidateMatchingAct(step, act);
            return new ActionBatch(act.Tick, act.Agents.ToDictionary(agent => agent.Id, agent => agent.Actions), spawns);
        }
        finally { _exchangeLock.Release(); }
    }

    public override void _ExitTree()
    {
        _stream?.Dispose();
        _client?.Dispose();
    }

    private async Task WriteMessageAsync(IProtocolMessage message, CancellationToken cancellationToken)
    {
        if (_stream is null) throw new InvalidOperationException("Bridge is not connected.");
        var frame = ProtocolCodec.Encode(message);
        await _stream.WriteAsync(frame, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    private async Task<IProtocolMessage> ReadMessageAsync(CancellationToken cancellationToken)
    {
        if (_stream is null) throw new InvalidOperationException("Bridge is not connected.");
        var lengthBytes = new byte[4];
        await _stream.ReadExactlyAsync(lengthBytes, cancellationToken);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
        if (length is < 1 or > ProtocolConstants.MaxBodyLength) throw new InvalidDataException($"Invalid frame length {length}.");
        var frame = new byte[checked(4 + (int)length)];
        lengthBytes.CopyTo(frame, 0);
        await _stream.ReadExactlyAsync(frame.AsMemory(4), cancellationToken);
        return ProtocolCodec.Decode(frame);
    }
}
