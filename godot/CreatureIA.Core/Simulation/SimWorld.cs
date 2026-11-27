using System.Numerics;
using CreatureIA.Core.Genetics;
using CreatureIA.Core.Physiology;
using CreatureIA.Core.Voxel;

namespace CreatureIA.Core.Simulation;

public enum SimEventKind : byte { Death = 1, Ate = 3, Damaged = 4, Birth = 5, Mated = 6, Fed = 7 }

/// <summary>
/// World event for the brain (protocol v3). Death: (id, cause). Ate: (id, resource, amount). Damaged: (id, zone, source).
/// Birth: (child, mother = SourceId, father = OtherId). Mated: (female, male = SourceId). Fed: (feeder, child = SourceId, amount).
/// </summary>
public readonly record struct SimEvent(SimEventKind Kind, uint CreatureId, byte Code, float Quantity, uint SourceId, uint OtherId = 0);

/// <summary>One line of the genealogy (T2.7a): every birth and death, appended by the simulator.</summary>
public readonly record struct GenealogyRecord(
    bool IsBirth, long PhysicsTick, uint Id, uint MotherId, uint FatherId, FounderSpecies Lineage, Sex Sex, int Generation,
    float[]? Phenotypes, DeathCause Cause, double AgeSeconds, bool Rescue);

/// <summary>
/// Exploit watch (T1.7): creature-ticks spent standing on foliage, buried (solid block right above
/// the head) and hugging the map edge. Reported with every evaluation; a large share is a bug to fix.
/// </summary>
public sealed class ExploitCounters
{
    public const float BorderMarginMeters = 2f;
    public long CreatureTicks;
    public long OnLeaves;
    public long Buried;
    public long AtBorder;
    /// <summary>Behaviour probe: ticks within drinking reach of fresh water, and of those, with the drink action on.</summary>
    public long NearWater;
    public long DrinkingNearWater;
    /// <summary>Ticks with water below 0.35 (thirsty), and of those, near drinkable water.</summary>
    public long Thirsty;
    public long ThirstyNearWater;

    public void Add(ExploitCounters other)
    {
        CreatureTicks += other.CreatureTicks;
        OnLeaves += other.OnLeaves;
        Buried += other.Buried;
        AtBorder += other.AtBorder;
        NearWater += other.NearWater;
        DrinkingNearWater += other.DrinkingNearWater;
        Thirsty += other.Thirsty;
        ThirstyNearWater += other.ThirstyNearWater;
    }

    public static double Ratio(long part, long whole) => whole == 0 ? 0 : (double)part / whole;

    public double Share(long count) => CreatureTicks == 0 ? 0 : (double)count / CreatureTicks;
}

/// <summary>Population counters of one world (births, deaths, rescues, generations).</summary>
public sealed class PopulationCounters
{
    public long Births;
    public long Conceptions;
    public long Inviable;
    public long Rescues;
    public long Deaths;
    public int MaxGeneration;
}

public sealed record SimWorldSettings
{
    public int Seed { get; init; } = 1337;
    /// <summary>Founders at the start; also the rescue target base and the control-run population.</summary>
    public int Population { get; init; } = 48;
    public int Plants { get; init; } = PlantField.DefaultCapacity;
    public int Lakes { get; init; } = 6;
    /// <summary>Half-size of the square where creatures are born.</summary>
    public float SpawnExtentMeters { get; init; } = 120f;
    /// <summary>
    /// Varies plants and spawns without changing the terrain: replicas of one reserved evaluation
    /// seed (seed 0) use salts 0, 1, 2… Salt 0 is the plain world of <see cref="Seed"/>.
    /// </summary>
    public int Salt { get; init; }
    /// <summary>Etapa 2: reproduction makes the population (births, rescue, cap). Off = etapa-1 style (brain respawns).</summary>
    public bool Births { get; init; } = true;
    /// <summary>Rescue from the archive when fewer creatures than this are alive (anti-extinction, T2.4b).</summary>
    public int MinPopulation { get; init; } = 16;
    /// <summary>No conception while this many are alive or about to be born (cap).</summary>
    public int MaxPopulation { get; init; } = 160;
    /// <summary>
    /// false = scientific control (T2.8): parents are drawn at random among adults, ignoring the
    /// brain's mate action, energy and distance; same genetics and mutation, no sexual selection.
    /// </summary>
    public bool Selection { get; init; } = true;
    /// <summary>Etapa-2 island (salt ocean, rivers) instead of the etapa-1 map.</summary>
    public bool Island { get; init; }
    /// <summary>First id of creatures born in this world (ids ≥ 2^30 belong to the simulator).</summary>
    public uint IdBase { get; init; } = SimWorld.SimulatorIdBase;
}

/// <summary>A dead body: meat that rots (T2.5).</summary>
public sealed class Carcass
{
    public Vector3 Position;
    public float Meat;
    public long DiedTick;
}

/// <summary>
/// One headless world: voxel terrain, clock, plants, sounds and creatures, stepped at 60 Hz with no
/// engine. Etapa 2: genomes, mating, gestation, births, rescue and carcasses. Not thread-safe;
/// different worlds can step on different threads.
/// </summary>
public sealed class SimWorld
{
    public const int PhysicsHz = 60;
    public const int PhysicsTicksPerBrainTick = 6;
    public const float PhysicsDelta = 1f / PhysicsHz;
    public const float ThreatSmellRange = 15f;
    /// <summary>Sound lifetime in physics ticks (0.5 s).</summary>
    public const int SoundLifetimeTicks = 30;
    /// <summary>Ids from here up are created by the simulator (births, rescue); the brain uses lower ids.</summary>
    public const uint SimulatorIdBase = 1u << 30;
    public const float CarcassRotSeconds = 1200f;
    public const int ArchiveSize = 64;
    private const float CarcassVisionRadius = 0.5f;
    private const float CarcassVisionHeight = 0.5f;

    private readonly record struct SoundPulse(uint SourceId, Vector3 Position, float Intensity, float Tone, long CreatedTick);

    public SimWorldSettings Settings { get; }
    public SimTerrain Terrain { get; }
    public SimClock Clock { get; } = new();
    public PlantField Plants { get; }
    public long PhysicsTick { get; private set; }
    public IReadOnlyList<SimCreature> Creatures => _creatures;
    public IReadOnlyList<Carcass> Carcasses => _carcasses;
    public PopulationCounters Population { get; } = new();
    /// <summary>Per-world random stream for life events (ageing, conception, litters).</summary>
    public SimRandom Random { get; }

    private readonly List<SimCreature> _creatures = new();
    private readonly HashSet<uint> _usedIds = new();
    private readonly SimRandom _spawnRng;
    private readonly SortedDictionary<uint, SoundPulse> _sounds = new();
    private readonly SortedDictionary<(int X, int Y, int Z), BlockType> _edits = new();
    private readonly List<uint> _expired = new();
    private readonly List<SimEvent> _events = new();
    private readonly List<(Vector3 Base, bool Thorny)> _nearPlants = new();
    private readonly List<SimCreature> _nearCreatures = new();
    private readonly List<Carcass> _carcasses = new();
    private readonly List<(SimCreature Mother, Genome[] Litter, uint Father)> _pendingBirths = new();
    private readonly List<Genome> _archive = new();
    private readonly List<GenealogyRecord> _genealogy = new();
    private uint _nextBirthId;

    public SimWorld(SimWorldSettings settings, IEnumerable<uint>? initialIds = null)
    {
        Settings = settings;
        var voxel = new VoxelWorldSettings { LakeCount = settings.Lakes };
        if (settings.Island) voxel = voxel with { Island = true, SizeX = 1536, SizeY = 128, SizeZ = 1536, WaterLevel = 32 };
        Terrain = new SimTerrain(VoxelWorldGenerator.Generate(settings.Seed, voxel));
        Terrain.Grid.BlockChanged += (x, y, z) => _edits[(x, y, z)] = Terrain.Grid.Get(x, y, z);
        Plants = new PlantField(Terrain, settings.Seed + settings.Salt * 1_000_003, settings.Plants);
        _spawnRng = new SimRandom((ulong)(settings.Seed + settings.Salt * 1_000_003), stream: 7);
        Random = new SimRandom((ulong)(settings.Seed + settings.Salt * 1_000_003), stream: 11);
        _nextBirthId = settings.IdBase;
        foreach (var id in initialIds ?? Enumerable.Range(1, settings.Population).Select(i => (uint)i)) Spawn(id, null);
    }

    private const uint SnapshotMagic = 0x57414943; // "CIAW"
    private const int SnapshotVersion = 3; // 3: etapa 2 (genomes, gestation, carcasses, archive)

    /// <summary>
    /// Complete world state (T2.0c): settings, edited blocks, clock, plants, creatures (with genomes),
    /// carcasses, archive, sounds, pending events, ids and every generator position. Terrain is regenerated on load.
    /// </summary>
    public void Save(Stream stream)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(SnapshotMagic); w.Write(SnapshotVersion);
        var s = Settings;
        w.Write(s.Seed); w.Write(s.Population); w.Write(s.Plants); w.Write(s.Lakes); w.Write(s.SpawnExtentMeters); w.Write(s.Salt);
        w.Write(s.Births); w.Write(s.MinPopulation); w.Write(s.MaxPopulation); w.Write(s.Selection); w.Write(s.Island); w.Write(s.IdBase);
        w.Write(PhysicsTick);
        Clock.Save(w);
        w.Write(_edits.Count);
        foreach (var ((x, y, z), block) in _edits) { w.Write(x); w.Write(y); w.Write(z); w.Write((byte)block); }
        Plants.Save(w);
        foreach (var rng in new[] { _spawnRng, Random })
        {
            var (state, increment) = rng.Save();
            w.Write(state); w.Write(increment);
        }
        w.Write(_nextBirthId);
        w.Write(_usedIds.Count);
        foreach (var id in _usedIds.OrderBy(i => i)) w.Write(id);
        w.Write(_creatures.Count);
        foreach (var creature in _creatures) creature.Save(w);
        w.Write(_carcasses.Count);
        foreach (var c in _carcasses) { w.Write(c.Position.X); w.Write(c.Position.Y); w.Write(c.Position.Z); w.Write(c.Meat); w.Write(c.DiedTick); }
        w.Write(_archive.Count);
        foreach (var g in _archive) GenomeIO.Write(w, g);
        w.Write(_sounds.Count);
        foreach (var sound in _sounds.Values)
        {
            w.Write(sound.SourceId); w.Write(sound.Position.X); w.Write(sound.Position.Y); w.Write(sound.Position.Z);
            w.Write(sound.Intensity); w.Write(sound.Tone); w.Write(sound.CreatedTick);
        }
        w.Write(_events.Count);
        foreach (var e in _events) { w.Write((byte)e.Kind); w.Write(e.CreatureId); w.Write(e.Code); w.Write(e.Quantity); w.Write(e.SourceId); w.Write(e.OtherId); }
        w.Write(Exploits.CreatureTicks); w.Write(Exploits.OnLeaves); w.Write(Exploits.Buried); w.Write(Exploits.AtBorder);
        var p = Population;
        w.Write(p.Births); w.Write(p.Conceptions); w.Write(p.Inviable); w.Write(p.Rescues); w.Write(p.Deaths); w.Write(p.MaxGeneration);
    }

    public static SimWorld Load(Stream stream)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != SnapshotMagic) throw new InvalidDataException("not a world snapshot");
        if (r.ReadInt32() != SnapshotVersion) throw new InvalidDataException("unsupported world snapshot version");
        var settings = new SimWorldSettings
        {
            Seed = r.ReadInt32(), Population = r.ReadInt32(), Plants = r.ReadInt32(), Lakes = r.ReadInt32(),
            SpawnExtentMeters = r.ReadSingle(), Salt = r.ReadInt32(), Births = r.ReadBoolean(), MinPopulation = r.ReadInt32(),
            MaxPopulation = r.ReadInt32(), Selection = r.ReadBoolean(), Island = r.ReadBoolean(), IdBase = r.ReadUInt32(),
        };
        var world = new SimWorld(settings, Array.Empty<uint>()) { PhysicsTick = r.ReadInt64() };
        world.Clock.Restore(r);
        var edits = r.ReadInt32();
        for (var i = 0; i < edits; i++) world.Terrain.Grid.Set(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), (BlockType)r.ReadByte());
        world.Terrain.Grid.TakeDirtyChunks();
        world.Plants.Restore(r);
        world._spawnRng.Restore(new SimRandom((r.ReadUInt64(), r.ReadUInt64())));
        world.Random.Restore(new SimRandom((r.ReadUInt64(), r.ReadUInt64())));
        world._nextBirthId = r.ReadUInt32();
        var used = r.ReadInt32();
        for (var i = 0; i < used; i++) world._usedIds.Add(r.ReadUInt32());
        var catalog = Species.Catalog;
        var creatures = r.ReadInt32();
        for (var i = 0; i < creatures; i++) world._creatures.Add(SimCreature.Load(r, catalog));
        var carcasses = r.ReadInt32();
        for (var i = 0; i < carcasses; i++)
            world._carcasses.Add(new Carcass { Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), Meat = r.ReadSingle(), DiedTick = r.ReadInt64() });
        var archive = r.ReadInt32();
        for (var i = 0; i < archive; i++) world._archive.Add(GenomeIO.Read(r, catalog));
        var sounds = r.ReadInt32();
        for (var i = 0; i < sounds; i++)
        {
            var pulse = new SoundPulse(r.ReadUInt32(), new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                r.ReadSingle(), r.ReadSingle(), r.ReadInt64());
            world._sounds[pulse.SourceId] = pulse;
        }
        var events = r.ReadInt32();
        for (var i = 0; i < events; i++)
            world._events.Add(new SimEvent((SimEventKind)r.ReadByte(), r.ReadUInt32(), r.ReadByte(), r.ReadSingle(), r.ReadUInt32(), r.ReadUInt32()));
        world.Exploits.CreatureTicks = r.ReadInt64(); world.Exploits.OnLeaves = r.ReadInt64();
        world.Exploits.Buried = r.ReadInt64(); world.Exploits.AtBorder = r.ReadInt64();
        var p = world.Population;
        p.Births = r.ReadInt64(); p.Conceptions = r.ReadInt64(); p.Inviable = r.ReadInt64(); p.Rescues = r.ReadInt64(); p.Deaths = r.ReadInt64();
        p.MaxGeneration = r.ReadInt32();
        world.Terrain.SetSeason(world.Photoperiod);
        return world;
    }

    /// <summary>SHA-256 of the snapshot bytes: equal hashes = identical world state.</summary>
    public string StateHash()
    {
        using var buffer = new MemoryStream();
        Save(buffer);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(buffer.ToArray()));
    }

    /// <summary>Atomic file write: a killed process never leaves a truncated snapshot.</summary>
    public void SaveFile(string path)
    {
        var temporary = path + ".tmp";
        using (var file = File.Create(temporary)) Save(file);
        File.Move(temporary, path, overwrite: true);
    }

    public static SimWorld LoadFile(string path)
    {
        using var file = File.OpenRead(path);
        return Load(file);
    }

    /// <summary>
    /// Creates a founder (brain SPAWN or the initial population) with a never-used id; null when the id
    /// was already used. Founders are young adults of a founding species with standing variation.
    /// </summary>
    public SimCreature? Spawn(uint id, Vector3? position)
    {
        if (!_usedIds.Add(id)) return null;
        var species = Species.Pick(_spawnRng);
        var sex = _spawnRng.NextFloat() < 0.5f ? Sex.Female : Sex.Male;
        var genome = Species.Founder(species, sex, _spawnRng);
        return AddFounder(id, genome, species, position, rescue: false);
    }

    private SimCreature AddFounder(uint id, Genome genome, FounderSpecies species, Vector3? position, bool rescue)
    {
        var place = position ?? Terrain.RandomLandPoint(_spawnRng, Settings.SpawnExtentMeters) + Vector3.UnitY * 0.05f;
        var creature = new SimCreature(id, place, genome, species) { Yaw = _spawnRng.Range(0f, MathF.Tau) };
        // Founders start as young adults (between maturity and 1.5× maturity) so the first generation can breed.
        creature.SetAgeForFounder(creature.Life.MaturityYears * LifeCycle.SecondsPerYear * _spawnRng.Range(1f, 1.5f));
        _creatures.Add(creature);
        _genealogy.Add(new GenealogyRecord(true, PhysicsTick, id, 0, 0, species, genome.Sex, 0, genome.Phenotypes(), DeathCause.None, 0, rescue));
        return creature;
    }

    /// <summary>Tests: puts a prepared creature in the world at a given age.</summary>
    internal SimCreature AddForTest(SimCreature creature, double ageSeconds, double energy = SimCreature.NewbornEnergy)
    {
        _usedIds.Add(creature.Id);
        creature.SetAgeForFounder(ageSeconds);
        creature.SetEnergyForTest(energy);
        _creatures.Add(creature);
        return creature;
    }

    /// <summary>Runs the physics ticks of one brain tick (6 × 1/60 s), then mating and rescue.</summary>
    public void StepBrainTick()
    {
        for (var i = 0; i < PhysicsTicksPerBrainTick; i++) StepPhysics();
        Terrain.SetSeason(Photoperiod);
        if (!Settings.Births) return;
        if (Settings.Selection) ResolveMating(); else ControlConception();
        Rescue();
    }

    /// <summary>Day length 0..1 of the current season (shared with the time block of the observation).</summary>
    public float Photoperiod => (1f - MathF.Cos(Clock.YearProgress01 * MathF.Tau)) * 0.5f;

    public void StepPhysics()
    {
        var dt = PhysicsDelta;
        Clock.Advance(dt);
        Plants.Step(dt, Clock.LightFactor, Clock.GrowthMultiplier);
        foreach (var creature in _creatures) creature.Step(this, dt);
        foreach (var creature in _creatures)
        {
            if (creature.IsAlive) continue;
            Population.Deaths++;
            _carcasses.Add(new Carcass { Position = creature.Position, Meat = (float)Math.Min(3.0, 0.6 * creature.Body.Mass * Math.Pow(creature.SizeScale / Math.Max(0.1f, creature.Body.Size), 3)), DiedTick = PhysicsTick });
            if (creature.IsMature) Archive(creature.Genome);
            _genealogy.Add(new GenealogyRecord(false, PhysicsTick, creature.Id, creature.MotherId, creature.FatherId, creature.Lineage,
                creature.Genome.Sex, creature.Generation, null, creature.Physiology.DeathCause, creature.AgeSeconds, false));
        }
        _creatures.RemoveAll(creature => !creature.IsAlive);
        FlushBirths();
        RotCarcasses(dt);
        WatchExploits();
        PhysicsTick++;
        _expired.Clear();
        foreach (var (sourceId, sound) in _sounds)
            if (PhysicsTick - sound.CreatedTick >= SoundLifetimeTicks) _expired.Add(sourceId);
        foreach (var sourceId in _expired) _sounds.Remove(sourceId);
    }

    // ---------------------------------------------------------------- reproduction (T2.4)

    private bool InBreedingSeason(SimCreature mother)
    {
        var width = mother.Genome.Phenotype("breeding_width");
        if (width >= 0.999f) return true;
        var peak = mother.Genome.Phenotype("breeding_peak");
        var d = MathF.Abs(Clock.YearProgress01 - peak);
        d = MathF.Min(d, 1f - d);
        return d <= width * 0.5f;
    }

    private int PopulationIncludingUnborn() => _creatures.Count + _creatures.Where(c => c.IsPregnant).Sum(c => 1);

    private static MateCandidate Candidate(SimCreature c, bool wants) =>
        new(c.Genome, c.AgeSeconds, c.Physiology.Energy, c.Position.X, c.Position.Z, wants);

    /// <summary>
    /// Every willing, fertile female in season pairs with the nearest willing male within reach
    /// (rules of <see cref="Mating"/>). Nothing here decides to mate: it only resolves the brain's actions.
    /// </summary>
    private void ResolveMating()
    {
        if (PopulationIncludingUnborn() >= Settings.MaxPopulation) return;
        foreach (var female in _creatures)
        {
            if (!female.IsFemale || !female.WantsToMate || female.IsPregnant || !female.IsMature || !InBreedingSeason(female)) continue;
            SimCreature? best = null;
            var bestD2 = Mating.MaxDistanceMeters * Mating.MaxDistanceMeters;
            foreach (var male in _creatures)
            {
                if (male.IsFemale || !male.WantsToMate || !male.IsMature) continue;
                var d2 = Vector3.DistanceSquared(male.Position, female.Position);
                if (d2 > bestD2) continue;
                if (Mating.Check(Candidate(female, true), Candidate(male, true)) != MateOutcome.Conceived) continue;
                best = male;
                bestD2 = d2;
            }
            if (best is null) continue;
            Conceive(female, best);
            if (PopulationIncludingUnborn() >= Settings.MaxPopulation) return;
        }
    }

    /// <summary>
    /// Control run (T2.8): keeps the population at its founding size with parents drawn at random
    /// among adults, regardless of what they do. Same genetics; no selection on who breeds.
    /// </summary>
    private void ControlConception()
    {
        var expected = _creatures.Count + _creatures.Where(c => c.IsPregnant).Sum(c => 2);
        if (expected >= Settings.Population) return;
        var females = _creatures.Where(c => c.IsFemale && c.IsMature && !c.IsPregnant).ToList();
        var males = _creatures.Where(c => !c.IsFemale && c.IsMature).ToList();
        if (females.Count == 0 || males.Count == 0) return;
        Conceive(females[(int)(Random.NextUInt() % (uint)females.Count)], males[(int)(Random.NextUInt() % (uint)males.Count)]);
    }

    private void Conceive(SimCreature female, SimCreature male)
    {
        Population.Conceptions++;
        var litterGene = female.Genome.Phenotype("litter_size");
        var count = (int)MathF.Floor(litterGene) + (Random.NextFloat() < litterGene - MathF.Floor(litterGene) ? 1 : 0);
        var litter = new List<Genome>();
        var a = Candidate(female, true);
        var b = Candidate(male, true);
        for (var i = 0; i < Math.Max(1, count); i++)
        {
            var child = Mating.Conceive(a, b, Random);
            if (child is null) Population.Inviable++; else litter.Add(child);
        }
        female.PayMating();
        male.PayMating();
        _events.Add(new SimEvent(SimEventKind.Mated, female.Id, 0, 0f, male.Id));
        Archive(female.Genome);
        Archive(male.Genome);
        if (litter.Count > 0) female.Conceive(litter, male.Id);
    }

    /// <summary>Called by a mother whose gestation ended; births join the world after the physics loop.</summary>
    internal void GiveBirth(SimCreature mother, Genome[] litter, uint fatherId)
    {
        _pendingBirths.Add((mother, litter, fatherId));
        mother.EndPregnancy();
    }

    private void FlushBirths()
    {
        foreach (var (mother, litter, fatherId) in _pendingBirths)
        {
            var fatherGeneration = _creatures.FirstOrDefault(c => c.Id == fatherId)?.Generation ?? mother.Generation;
            var generation = Math.Max(mother.Generation, fatherGeneration) + 1;
            foreach (var genome in litter)
            {
                if (_creatures.Count >= Settings.MaxPopulation) break;
                var id = NextSimulatorId();
                var offset = new Vector3(_spawnRng.Range(-0.8f, 0.8f), 0.05f, _spawnRng.Range(-0.8f, 0.8f));
                var child = new SimCreature(id, mother.Position + offset, genome, mother.Lineage, mother.Id, fatherId, generation)
                {
                    Yaw = _spawnRng.Range(0f, MathF.Tau)
                };
                _creatures.Add(child);
                Population.Births++;
                Population.MaxGeneration = Math.Max(Population.MaxGeneration, generation);
                _events.Add(new SimEvent(SimEventKind.Birth, id, 0, 0f, mother.Id, fatherId));
                _genealogy.Add(new GenealogyRecord(true, PhysicsTick, id, mother.Id, fatherId, mother.Lineage, genome.Sex, generation,
                    genome.Phenotypes(), DeathCause.None, 0, false));
            }
        }
        _pendingBirths.Clear();
    }

    private uint NextSimulatorId()
    {
        while (!_usedIds.Add(_nextBirthId)) _nextBirthId++;
        return _nextBirthId++;
    }

    private void Archive(Genome genome)
    {
        _archive.Add(genome);
        if (_archive.Count > ArchiveSize) _archive.RemoveAt(0);
    }

    /// <summary>
    /// Anti-extinction (T2.4b): below the minimum, new founders come from the archive of recent
    /// parents (a child of two archived genomes), or from the founding species when it is empty.
    /// </summary>
    private void Rescue()
    {
        while (_creatures.Count < Settings.MinPopulation)
        {
            var females = _archive.Where(g => g.Sex == Sex.Female).ToList();
            var males = _archive.Where(g => g.Sex == Sex.Male).ToList();
            Genome genome;
            FounderSpecies species;
            if (females.Count > 0 && males.Count > 0)
            {
                var mother = females[(int)(Random.NextUInt() % (uint)females.Count)];
                var father = males[(int)(Random.NextUInt() % (uint)males.Count)];
                genome = Reproduction.Child(mother, father, Random);
                species = SpeciesOf(genome);
            }
            else
            {
                species = Species.Pick(Random);
                genome = Species.Founder(species, Random.NextFloat() < 0.5f ? Sex.Female : Sex.Male, Random);
            }
            Population.Rescues++;
            AddFounder(NextSimulatorId(), genome, species, null, rescue: true);
        }
    }

    /// <summary>Founding species whose genome is closest (a label of descent for reports).</summary>
    public static FounderSpecies SpeciesOf(Genome genome)
    {
        var longevity = genome.Phenotype("longevity_years");
        if (genome.Phenotype("carnivory") > 0.5f) return FounderSpecies.C;
        return longevity > 10f ? FounderSpecies.B : FounderSpecies.A;
    }

    internal SimCreature? NearestOwnYoung(SimCreature parent, float reach, double hungryBelow)
    {
        SimCreature? best = null;
        var bestD2 = reach * reach;
        foreach (var c in _creatures)
        {
            if (!parent.IsParentOf(c) || c.IsMature || c.Physiology.Energy >= hungryBelow) continue;
            var d2 = Vector3.DistanceSquared(c.Position, parent.Position);
            if (d2 > bestD2) continue;
            best = c;
            bestD2 = d2;
        }
        return best;
    }

    // ---------------------------------------------------------------- predation and carcasses (T2.5)

    /// <summary>Bite the nearest live creature in front of the mouth (not one's own young). Returns true if it bit.</summary>
    internal bool TryBite(SimCreature attacker, Vector3 mouth, float dt)
    {
        SimCreature? target = null;
        var bestD2 = 1.2f * 1.2f;
        foreach (var c in _creatures)
        {
            if (c.Id == attacker.Id || !c.IsAlive || attacker.IsParentOf(c)) continue;
            var d2 = Vector3.DistanceSquared(c.Position + Vector3.UnitY * (0.5f * c.SizeScale), mouth + Vector3.UnitY * 0.3f);
            var reach = 0.6f + 0.4f * c.SizeScale;
            if (d2 > bestD2 + reach * reach) continue;
            target = c;
            bestD2 = d2;
        }
        if (target is null) return false;
        var ratio = MathF.Sqrt(attacker.SizeScale / MathF.Max(0.1f, target.SizeScale));
        target.TakeBite(this, attacker.Body.BiteDamage * ratio * dt * 0.6f, attacker.Id);
        EmitSound(target.Id, target.Position, 0.8f, -0.8f);
        return true;
    }

    /// <summary>Eat meat from the nearest carcass within reach; returns the amount taken.</summary>
    internal float ConsumeCarcass(Vector3 mouth, float reach, float amount)
    {
        Carcass? best = null;
        var bestD2 = reach * reach;
        foreach (var c in _carcasses)
        {
            var d2 = Vector3.DistanceSquared(c.Position, mouth);
            if (d2 > bestD2 || c.Meat <= 0f) continue;
            best = c;
            bestD2 = d2;
        }
        if (best is null) return 0f;
        var taken = MathF.Min(best.Meat, amount);
        best.Meat -= taken;
        return taken;
    }

    private void RotCarcasses(float dt)
    {
        foreach (var c in _carcasses) c.Meat -= dt * 3f / CarcassRotSeconds;
        _carcasses.RemoveAll(c => c.Meat <= 0f);
    }

    // ---------------------------------------------------------------- climate (T2.1c)

    /// <summary>
    /// Regional climate: seasons are stronger towards the north (−Z) and milder to the south, so the
    /// same day of the year means a different winter in different places.
    /// </summary>
    public float SeasonStrength(Vector3 position)
    {
        var half = Terrain.MapSize * 0.5f;
        var north = Math.Clamp(0.5f - position.Z / (2f * half), 0f, 1f); // 1 at the north edge
        return 0.5f + north;
    }

    public float AmbientTemperature(Vector3 position)
    {
        var day = SimClock.Lerp(-8f, 0f, Clock.LightFactor);
        var season = -10f * Clock.WinterStrength * SeasonStrength(position);
        return Terrain.Sample(position.X, position.Z).TemperatureCelsius + day + season;
    }

    // ---------------------------------------------------------------- exploits and events

    public ExploitCounters Exploits { get; } = new();

    private void WatchExploits()
    {
        var grid = Terrain.Grid;
        var edge = Terrain.MapSize / 2f - ExploitCounters.BorderMarginMeters;
        foreach (var creature in _creatures)
        {
            var p = creature.Position;
            Exploits.CreatureTicks++;
            if (BlockTypes.IsFoliage(grid.GetAtWorld(p.X, p.Y - 0.1f, p.Z))) Exploits.OnLeaves++;
            if (BlockTypes.IsSolid(grid.GetAtWorld(p.X, p.Y + creature.BodyShape.Height + 0.1f, p.Z))) Exploits.Buried++;
            if (MathF.Abs(p.X) > edge || MathF.Abs(p.Z) > edge) Exploits.AtBorder++;
            var near = Terrain.IsNearDrinkableWater(p);
            if (near)
            {
                Exploits.NearWater++;
                if (creature.Action[6] > 0.5f) Exploits.DrinkingNearWater++;
            }
            if (creature.Physiology.Water < 0.35)
            {
                Exploits.Thirsty++;
                if (near) Exploits.ThirstyNearWater++;
            }
        }
    }

    /// <summary>Events since the last call, in the order they happened.</summary>
    public SimEvent[] TakeEvents()
    {
        var events = _events.ToArray();
        _events.Clear();
        return events;
    }

    /// <summary>Genealogy records since the last call (births and deaths, T2.7a).</summary>
    public GenealogyRecord[] TakeGenealogy()
    {
        var records = _genealogy.ToArray();
        _genealogy.Clear();
        return records;
    }

    internal void RecordDeath(uint id, DeathCause cause) => _events.Add(new SimEvent(SimEventKind.Death, id, (byte)cause, 0f, 0));
    internal void RecordAte(uint id, byte resource, float amount) => _events.Add(new SimEvent(SimEventKind.Ate, id, resource, amount, 0));
    internal void RecordDamage(uint id, BodyZone zone, uint source) => _events.Add(new SimEvent(SimEventKind.Damaged, id, (byte)zone, 0f, source));
    internal void RecordFed(uint feeder, uint child, float amount) => _events.Add(new SimEvent(SimEventKind.Fed, feeder, 0, amount, child));

    internal void EmitSound(uint sourceId, Vector3 position, float intensity, float tone = 0f)
    {
        intensity = Math.Clamp(intensity, 0f, 1f);
        if (intensity <= 0.01f) return;
        _sounds[sourceId] = new SoundPulse(sourceId, position, intensity, Math.Clamp(tone, -1f, 1f), PhysicsTick);
    }

    // ---------------------------------------------------------------- observation (protocol v3)

    /// <summary>Writes the observation v3 of <paramref name="creature"/> and updates its perceived threat.</summary>
    public void Observe(SimCreature creature, Span<float> observation)
    {
        if (observation.Length != ObservationLayout.Total) throw new ArgumentException($"observation must have {ObservationLayout.Total} floats");
        observation.Clear();
        const int sf = ObservationLayout.SectorFeatures;
        Span<float> sectorSums = stackalloc float[4 * sf];
        Span<int> sectorHits = stackalloc int[4];
        var senses = creature.Senses;
        var origin = creature.Position + Vector3.UnitY * creature.EyeHeightMeters;
        var range = senses.VisionRangeMeters * senses.LightFactor(Clock.LightFactor);
        var fov = senses.FieldOfViewDegrees;
        var grid = Terrain.Grid;
        Plants.CollectNear(origin, range + PlantField.VisionRadius, _nearPlants);
        CollectCreaturesNear(origin, range + SimCreature.VisionRadius * 3f, creature.Id, _nearCreatures);
        var threat = 0f;
        const int rays = ObservationLayout.HorizontalRays;

        for (var level = 0; level < ObservationLayout.VerticalLevels; level++)
        {
            var pitch = (level - 1) * 13f * (MathF.PI / 180f) + creature.HeadPitchRadians;
            var (sinPitch, cosPitch) = MathF.SinCos(pitch);
            for (var ray = 0; ray < rays; ray++)
            {
                var yaw = (-fov * 0.5f + fov * ray / (rays - 1f)) * (MathF.PI / 180f);
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
                foreach (var carcass in _carcasses)
                {
                    var t = VisionGeometry.RayVerticalCylinder(origin.X, origin.Y, origin.Z, direction.X, direction.Y, direction.Z,
                        carcass.Position.X, carcass.Position.Y, carcass.Position.Z, CarcassVisionRadius, CarcassVisionHeight, range);
                    if (t is not { } d || d >= bestDistance) continue;
                    bestDistance = d;
                    type = VisionClass.Meat;
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
                var kin = creature.IsKinOf(seen);
                var threatness = kin ? 0f : FearModel.Threatness(seen.SizeScale, creature.SizeScale, seen.IsCarnivore);
                threat = MathF.Max(threat, FearModel.Perceived(threatness, bestDistance, range));
                if (level != 1) continue;
                var sector = ray * 4 / rays;
                var s = sector * sf;
                sectorSums[s] += seen.Color.R;
                sectorSums[s + 1] += seen.Color.G;
                sectorSums[s + 2] += seen.Color.B;
                sectorSums[s + 3] += seen.SizeScale / MathF.Max(1e-3f, creature.SizeScale);
                sectorSums[s + 4] += seen.IsCarnivore ? 1f : 0f;
                sectorSums[s + 5] += kin ? 1f : 0f; // family scent: parents, young and siblings
                sectorSums[s + 6] += seen.IsFemale ? 1f : 0f;
                var mature = seen.IsMature;
                sectorSums[s + 7] += mature ? 1f : 0f;
                sectorSums[s + 8] += mature ? 0f : 1f;
                sectorHits[sector]++;
            }
        }

        for (var sector = 0; sector < 4; sector++)
        {
            if (sectorHits[sector] == 0) continue;
            for (var k = 0; k < sf; k++)
                observation[ObservationLayout.CreatureSectorOffset + sector * sf + k] = sectorSums[sector * sf + k] / sectorHits[sector];
            // Relative size is unbounded above; keep it in a sane range for the network.
            var i = ObservationLayout.CreatureSectorOffset + sector * sf + 3;
            observation[i] = Math.Clamp(observation[i], 0f, 4f);
        }

        WriteAudio(observation, creature);
        threat = MathF.Max(threat, WriteOlfaction(observation, creature));
        creature.LastThreat = threat;
        creature.OffspringInNeed = NearestOwnYoung(creature, 6f, 0.5) is not null;
        WriteProprioception(observation, creature);
        WriteInteroception(observation, creature);
        WriteTime(observation, creature);
        WriteGenome(observation, creature);
    }

    private void WriteTime(Span<float> observation, SimCreature creature)
    {
        // Regional seasons (T2.1c): the photoperiod swing and the cold follow the season strength of the place.
        var strength = SeasonStrength(creature.Position);
        ObservationLayout.WriteTime(observation, Clock.TimeOfDay01, Clock.YearProgress01, Clock.LightFactor,
            SimClock.Lerp(-8f, 0f, Clock.LightFactor) - 10f * Clock.WinterStrength * strength);
        var i = ObservationLayout.TimeOffset + 2;
        observation[i] = Math.Clamp(0.5f + (observation[i] - 0.5f) * strength, 0f, 1f);
    }

    private static readonly int[] BodyGenes = Species.Catalog.BodyGeneIndices();

    private static void WriteGenome(Span<float> observation, SimCreature creature)
    {
        var genome = creature.Genome;
        var genes = genome.Catalog.Genes;
        for (var k = 0; k < BodyGenes.Length; k++)
        {
            var g = genes[BodyGenes[k]];
            observation[ObservationLayout.GenesOffset + k] = (genome.Phenotype(BodyGenes[k]) - g.Min) / g.Range;
        }
        creature.Motivations.AsSpan().CopyTo(observation.Slice(ObservationLayout.MotivationsOffset, ObservationLayout.MotivationsLength));
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
        var hearing = listener.Senses.HearingRangeMeters;
        foreach (var sound in _sounds.Values)
        {
            if (sound.SourceId == listener.Id) continue;
            var delta = sound.Position - listener.Position;
            var distance = delta.Length();
            if (distance > hearing || distance <= 0.001f) continue;
            var local = listener.ToLocal(delta);
            var quadrant = local.Z < 0f ? (local.X < 0f ? 0 : 1) : (local.X < 0f ? 2 : 3);
            var perceived = sound.Intensity * (1f - distance / hearing);
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

    /// <summary>Writes the scent gradients (log scale, keener noses amplify); returns the strongest threat smelled (0..1).</summary>
    private float WriteOlfaction(Span<float> observation, SimCreature listener)
    {
        var body = listener.Position;
        var nose = body + listener.Forward * 0.6f;
        var side = body + listener.Right * 0.6f;
        var gain = listener.Senses.ScentGain;
        var o = ObservationLayout.OlfactionOffset;
        static void Scent(Span<float> obs, int at, float gain, Func<Vector3, float> field, Vector3 body, Vector3 nose, Vector3 side) =>
            ObservationLayout.WriteScent(obs, at, gain * field(body), gain * field(nose), gain * field(side));
        Scent(observation, o + ObservationLayout.SmellPlants, gain, Plants.Scent, body, nose, side);
        Scent(observation, o + ObservationLayout.SmellFreshWater, gain, Terrain.WaterScent, body, nose, side);
        Scent(observation, o + ObservationLayout.SmellSaltWater, gain, Terrain.SaltScent, body, nose, side);
        Scent(observation, o + ObservationLayout.SmellCreatures, gain, p => CreatureScent(p, listener, false), body, nose, side);
        Scent(observation, o + ObservationLayout.SmellThreat, gain, p => CreatureScent(p, listener, true), body, nose, side);
        var strongest = 0f;
        foreach (var other in _creatures)
        {
            if (other.Id == listener.Id || !other.IsAlive || listener.IsKinOf(other)) continue;
            var threatness = FearModel.Threatness(other.SizeScale, listener.SizeScale, other.IsCarnivore);
            if (threatness <= 0f) continue;
            strongest = MathF.Max(strongest, FearModel.Perceived(threatness, Vector3.Distance(body, other.Position), ThreatSmellRange));
        }
        return strongest;
    }

    /// <summary>Scent of other live creatures (and carcasses); with <paramref name="threatOnly"/>, weighted by threat.</summary>
    private float CreatureScent(Vector3 position, SimCreature listener, bool threatOnly)
    {
        var scent = 0f;
        foreach (var other in _creatures)
        {
            if (other.Id == listener.Id || !other.IsAlive) continue;
            var weight = 1f;
            if (threatOnly)
            {
                if (listener.IsKinOf(other)) continue;
                weight = FearModel.Threatness(other.SizeScale, listener.SizeScale, other.IsCarnivore);
                if (weight <= 0f) continue;
            }
            scent += weight / MathF.Max(4f, Vector3.DistanceSquared(position, other.Position));
        }
        if (!threatOnly)
            foreach (var c in _carcasses) scent += 0.5f * c.Meat / MathF.Max(4f, Vector3.DistanceSquared(position, c.Position));
        return scent;
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
        observation[i++] = creature.AgeFraction;
        observation[i++] = creature.IsMature ? 1f : 0f;
        observation[i++] = creature.Fear;
        observation[i++] = creature.GestationProgress;
        observation[i] = creature.OffspringInNeed ? 1f : 0f;
    }
}
