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
    Births = !options.NoBirths,
    Selection = !options.Control,
    Island = options.Island,
    MinPopulation = options.MinPopulation ?? Math.Max(4, options.Population / 3),
    MaxPopulation = options.MaxPopulation ?? options.Population * 3,
};
var clock = Stopwatch.StartNew();
var resumed = options.WorldsDir is not null && MultiWorld.HasSnapshots(options.WorldsDir, options.Worlds);
var worlds = resumed ? new MultiWorld(options.WorldsDir!, options.Worlds) : new MultiWorld(settings, options.Worlds, options.Replicas);
var herdBiomassStart = worlds.HerdBiomass();
var seeds = resumed ? $"resumed from {options.WorldsDir}"
    : options.Replicas ? $"terrain seed {options.Seed}, salts 0..{options.Worlds - 1}" : $"seeds {options.Seed}..{options.Seed + options.Worlds - 1}";
Console.Error.WriteLine($"[sim] {options.Worlds} world(s) x {options.Population} creatures generated in {clock.ElapsedMilliseconds} ms " +
                        $"({seeds}, plants={options.Plants}, lakes={options.Lakes}, births={settings.Births}, selection={settings.Selection}, island={settings.Island})");
using var genealogy = options.GenealogyFile is null ? null : new StreamWriter(options.GenealogyFile, append: true);

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
        $"observe {observeTicks * 1000.0 / Stopwatch.Frequency / options.Ticks:F2} ms/tick); live={worlds.LiveCount}; " +
        $"births={worlds.PopulationTotals().Births} conceptions={worlds.PopulationTotals().Conceptions} rescues={worlds.PopulationTotals().Rescues} deaths={worlds.PopulationTotals().Deaths} gen={worlds.PopulationTotals().MaxGeneration}"));
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
var wallClock = Stopwatch.StartNew();
var timedOut = false;
for (; options.Ticks == 0 || brainTick < (ulong)options.Ticks; brainTick++)
{
    // Wall-clock budget (e.g. "train until the morning evaluation"): only decides when to stop,
    // never what happens inside the simulation, so determinism per tick is untouched.
    if (options.MaxMinutes > 0 && wallClock.Elapsed.TotalMinutes >= options.MaxMinutes) { timedOut = true; break; }
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
    if (genealogy is not null) worlds.AppendGenealogy(genealogy);
    var t3 = Stopwatch.GetTimestamp();
    simMs += (t1 - t0) * 1000.0 / Stopwatch.Frequency;
    observeMs += (t2 - t1) * 1000.0 / Stopwatch.Frequency;
    waitMs += (t3 - t2) * 1000.0 / Stopwatch.Frequency;
    if (options.WorldsDir is not null && options.SaveEvery > 0 && (brainTick + 1) % (ulong)options.SaveEvery == 0)
        worlds.SaveAll(options.WorldsDir);
    if ((brainTick + 1) % (ulong)options.ReportEvery == 0)
    {
        var n = options.ReportEvery;
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[sim] tick {brainTick + 1}: {n / window.Elapsed.TotalSeconds:F1} ticks/s; per tick sim {simMs / n:F1} ms, " +
            $"observe {observeMs / n:F1} ms, brain {waitMs / n:F1} ms; live={worlds.LiveCount}; " +
            $"births={worlds.PopulationTotals().Births} rescues={worlds.PopulationTotals().Rescues} gen={worlds.PopulationTotals().MaxGeneration}"));
        simMs = observeMs = waitMs = 0;
        window.Restart();
    }
}
var reason = timedOut ? "time budget" : options.Ticks > 0 && brainTick >= (ulong)options.Ticks ? "horizon" : "extinct";
if (options.WorldsDir is not null) worlds.SaveAll(options.WorldsDir);
if (genealogy is not null) worlds.AppendGenealogy(genealogy);
Console.Error.WriteLine($"[run] stopping at brain tick {brainTick}: {reason}");
var population = worlds.PopulationTotals();
var exploits = worlds.Exploits();
var report = string.Create(CultureInfo.InvariantCulture,
    $"{{\"brain_ticks\": {brainTick}, \"stop\": \"{reason}\", \"worlds\": {options.Worlds}, \"replicas\": {(options.Replicas ? "true" : "false")}, " +
    $"\"seed\": {options.Seed}, \"creature_ticks\": {exploits.CreatureTicks}, \"on_leaves_share\": {exploits.Share(exploits.OnLeaves):F6}, " +
    $"\"buried_share\": {exploits.Share(exploits.Buried):F6}, \"at_border_share\": {exploits.Share(exploits.AtBorder):F6}, " +
    $"\"near_water_share\": {exploits.Share(exploits.NearWater):F6}, " +
    $"\"drink_on_when_near\": {ExploitCounters.Ratio(exploits.DrinkingNearWater, exploits.NearWater):F6}, " +
    $"\"thirsty_share\": {exploits.Share(exploits.Thirsty):F6}, " +
    $"\"near_water_when_thirsty\": {ExploitCounters.Ratio(exploits.ThirstyNearWater, exploits.Thirsty):F6}, " +
    $"\"near_plant_share\": {exploits.Share(exploits.NearPlant):F6}, " +
    $"\"eat_on_when_near_plant\": {ExploitCounters.Ratio(exploits.EatingNearPlant, exploits.NearPlant):F6}, " +
    $"\"hungry_share\": {exploits.Share(exploits.Hungry):F6}, " +
    $"\"near_plant_when_hungry\": {ExploitCounters.Ratio(exploits.HungryNearPlant, exploits.Hungry):F6}, " +
    $"\"herd_biomass_start\": {herdBiomassStart:F2}, \"herd_biomass_end\": {worlds.HerdBiomass():F2}, " +
    $"\"births\": {population.Births}, \"conceptions\": {population.Conceptions}, \"inviable\": {population.Inviable}, " +
    $"\"receptive_ticks\": {population.ReceptiveTicks}, \"fertile_male_ticks\": {population.FertileMaleTicks}, " +
    $"\"male_mate_choices\": {population.MaleMateChoices}, \"mate_opportunities\": {population.MateOpportunities}, " +
    $"\"rescues\": {population.Rescues}, \"deaths\": {population.Deaths}, \"max_generation\": {population.MaxGeneration}, \"live\": {worlds.LiveCount}}}");
Console.Error.WriteLine($"[exploits] {report}");
if (options.ReportFile is not null) File.WriteAllText(options.ReportFile, report + Environment.NewLine);
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
    public bool Replicas { get; init; }
    public int ReportEvery { get; init; } = 600;
    public string? ReportFile { get; init; }
    /// <summary>World snapshots (T2.0c): resume from here when present; saved at exit and every SaveEvery ticks.</summary>
    public string? WorldsDir { get; init; }
    public int SaveEvery { get; init; } = 6000;
    public double MaxMinutes { get; init; }
    /// <summary>Etapa-1 style: no reproduction, the brain respawns (stage-1 evaluation).</summary>
    public bool NoBirths { get; init; }
    /// <summary>Scientific control (T2.8): random parents, no selection on who breeds.</summary>
    public bool Control { get; init; }
    public bool Island { get; init; }
    public int? MinPopulation { get; init; }
    public int? MaxPopulation { get; init; }
    /// <summary>Genealogy JSONL (T2.7a), appended.</summary>
    public string? GenealogyFile { get; init; }

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
                "--replicas" => o with { Replicas = true },
                "--report-file" => o with { ReportFile = Next() },
                "--worlds-dir" => o with { WorldsDir = Next() },
                "--save-every" => o with { SaveEvery = NextInt() },
                "--max-minutes" => o with { MaxMinutes = double.Parse(Next(), CultureInfo.InvariantCulture) },
                "--report-every" => o with { ReportEvery = NextInt() },
                "--no-births" => o with { NoBirths = true },
                "--control" => o with { Control = true },
                "--island" => o with { Island = true },
                "--min-population" => o with { MinPopulation = NextInt() },
                "--max-population" => o with { MaxPopulation = NextInt() },
                "--genealogy" => o with { GenealogyFile = Next() },
                _ => throw new ArgumentException($"unknown option {args[i]}")
            };
        }
        if (o.Worlds < 1 || o.Population < 0) throw new ArgumentException("--worlds must be >= 1");
        return o;
    }
}
