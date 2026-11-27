using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using CreatureIA.Core.Simulation;
using CreatureIA.Protocol;
using CreatureIA.Sim;

// CreatureIA.Sim — headless training simulator (Task 002, T2.0b). No engine, no rendering:
// N worlds step in parallel and the brain sees them as one population (one STEP per tick).
//
//   CreatureIA.Sim --port 5557 --worlds 6 --seed 1 --ticks 72000 --plants 2800 --lakes 12
//   CreatureIA.Sim --bench --worlds 6 --ticks 300        (random actions, no brain: measures speed)

var options = Options.Parse(args);
var settings = new SimWorldSettings
{
    Seed = options.Seed,
    Population = options.Population,
    Plants = options.Plants,
    Lakes = options.Lakes,
};
var clock = Stopwatch.StartNew();
var worlds = new MultiWorld(settings, options.Worlds);
Console.Error.WriteLine($"[sim] {options.Worlds} world(s) x {options.Population} creatures generated in {clock.ElapsedMilliseconds} ms " +
                        $"(seeds {options.Seed}..{options.Seed + options.Worlds - 1}, plants={options.Plants}, lakes={options.Lakes})");

if (options.Bench)
{
    var random = new SimRandom((ulong)options.Seed, stream: 17);
    var action = new float[ObservationLayout.ActionCount];
    long simTicks = 0, observeTicks = 0;
    var total = Stopwatch.StartNew();
    for (ulong tick = 0; tick < (ulong)options.Ticks; tick++)
    {
        var t0 = Stopwatch.GetTimestamp();
        worlds.StepBrainTick();
        var t1 = Stopwatch.GetTimestamp();
        var step = worlds.BuildStep(tick);
        observeTicks += Stopwatch.GetTimestamp() - t1;
        simTicks += t1 - t0;
        var agents = step.Agents.Select(a =>
        {
            var act = new float[ObservationLayout.ActionCount];
            for (var i = 0; i < act.Length; i++) act[i] = random.Range(-1f, 1f);
            return new ActAgent(a.Id, act);
        }).ToArray();
        worlds.Apply(new ActMessage(tick, agents), Array.Empty<SpawnMessage>());
    }
    var seconds = total.Elapsed.TotalSeconds;
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"[bench] {options.Ticks} brain ticks in {seconds:F2} s = {options.Ticks / seconds:F1} ticks/s, " +
        $"{options.Ticks * options.Worlds * options.Population / seconds:F0} creature-steps/s (sim {simTicks * 1000.0 / Stopwatch.Frequency / options.Ticks:F2} ms/tick, " +
        $"observe {observeTicks * 1000.0 / Stopwatch.Frequency / options.Ticks:F2} ms/tick); live={worlds.LiveCount}"));
    return 0;
}

using var client = new TcpClient { NoDelay = true, ReceiveTimeout = 600_000, SendTimeout = 600_000 };
client.Connect(options.Host, options.Port);
using var stream = client.GetStream();
Write(stream, new HelloMessage(ProtocolConstants.Version, ProtocolConstants.ObservationDimensions,
    ProtocolConstants.ActionDimensions, (ulong)options.Seed));
if (Read(stream) is not ConfigMessage config) throw new InvalidDataException("Expected CONFIG after HELLO.");
if (config.PhysicsHz != SimWorld.PhysicsHz || config.BrainHz != SimWorld.PhysicsHz / SimWorld.PhysicsTicksPerBrainTick)
    throw new InvalidDataException("Server cadence is incompatible with the simulator cadence.");

double simMs = 0, observeMs = 0, waitMs = 0;
var window = Stopwatch.StartNew();
ulong brainTick = 0;
for (; options.Ticks == 0 || brainTick < (ulong)options.Ticks; brainTick++)
{
    var t0 = Stopwatch.GetTimestamp();
    worlds.StepBrainTick();
    var t1 = Stopwatch.GetTimestamp();
    var step = worlds.BuildStep(brainTick);
    var t2 = Stopwatch.GetTimestamp();
    if (options.QuitWhenExtinct && step.Agents.Count == 0 && step.Events.Count == 0 && brainTick > 0) break;
    Write(stream, step);
    var spawns = new List<SpawnMessage>();
    IProtocolMessage response;
    while ((response = Read(stream)) is SpawnMessage spawn) spawns.Add(spawn);
    if (response is not ActMessage act) throw new InvalidDataException("Expected ACT after STEP.");
    ProtocolCodec.ValidateMatchingAct(step, act);
    worlds.Apply(act, spawns);
    var t3 = Stopwatch.GetTimestamp();
    simMs += (t1 - t0) * 1000.0 / Stopwatch.Frequency;
    observeMs += (t2 - t1) * 1000.0 / Stopwatch.Frequency;
    waitMs += (t3 - t2) * 1000.0 / Stopwatch.Frequency;
    if ((brainTick + 1) % (ulong)options.ReportEvery == 0)
    {
        var n = options.ReportEvery;
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[sim] tick {brainTick + 1}: {n / window.Elapsed.TotalSeconds:F1} ticks/s; per tick sim {simMs / n:F1} ms, " +
            $"observe {observeMs / n:F1} ms, brain {waitMs / n:F1} ms; live={worlds.LiveCount}"));
        simMs = observeMs = waitMs = 0;
        window.Restart();
    }
}
Console.Error.WriteLine($"[run] stopping at brain tick {brainTick}: {(options.Ticks > 0 && brainTick >= (ulong)options.Ticks ? "horizon" : "extinct")}");
return 0;

static void Write(NetworkStream stream, IProtocolMessage message)
{
    stream.Write(ProtocolCodec.Encode(message));
    stream.Flush();
}

static IProtocolMessage Read(NetworkStream stream)
{
    Span<byte> lengthBytes = stackalloc byte[4];
    stream.ReadExactly(lengthBytes);
    var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
    if (length is < 1 or > ProtocolConstants.MaxBodyLength) throw new InvalidDataException($"Invalid frame length {length}.");
    var frame = new byte[checked(4 + (int)length)];
    lengthBytes.CopyTo(frame);
    stream.ReadExactly(frame.AsSpan(4));
    return ProtocolCodec.Decode(frame);
}

internal sealed record Options
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 5557;
    public int Worlds { get; init; } = 6;
    public int Seed { get; init; } = 1;
    public int Population { get; init; } = 48;
    public int Plants { get; init; } = PlantField.DefaultCapacity;
    public int Lakes { get; init; } = 6;
    public long Ticks { get; init; }
    public bool QuitWhenExtinct { get; init; }
    public bool Bench { get; init; }
    public int ReportEvery { get; init; } = 600;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            int NextInt() => int.Parse(Next(), CultureInfo.InvariantCulture);
            o = args[i] switch
            {
                "--host" => o with { Host = Next() },
                "--port" => o with { Port = NextInt() },
                "--worlds" => o with { Worlds = NextInt() },
                "--seed" => o with { Seed = NextInt() },
                "--population" => o with { Population = NextInt() },
                "--plants" => o with { Plants = NextInt() },
                "--lakes" => o with { Lakes = NextInt() },
                "--ticks" => o with { Ticks = long.Parse(Next(), CultureInfo.InvariantCulture) },
                "--quit-when-extinct" => o with { QuitWhenExtinct = true },
                "--bench" => o with { Bench = true },
                "--report-every" => o with { ReportEvery = NextInt() },
                _ => throw new ArgumentException($"unknown option {args[i]}")
            };
        }
        if (o.Worlds < 1 || o.Population < 0) throw new ArgumentException("--worlds must be >= 1");
        return o;
    }
}
