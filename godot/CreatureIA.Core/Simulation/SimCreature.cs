using System.Numerics;
using CreatureIA.Core.Genetics;
using CreatureIA.Core.Physiology;
using CreatureIA.Core.Voxel;

namespace CreatureIA.Core.Simulation;

/// <summary>Body zone codes of the DAMAGED event.</summary>
public enum BodyZone : byte { Head, Torso, Legs }

/// <summary>
/// A creature in the simulator: voxel kinematics, actuators, discrete actions (eat, drink, dig, call,
/// mate, feed), physiology and fear. Etapa 2: the body, senses, life cycle and motivations come from
/// its diploid <see cref="Genome"/>; nothing learned is ever written back to it.
/// </summary>
public sealed class SimCreature
{
    /// <summary>Reference body box (size 1). Each creature scales it by its size and growth.</summary>
    public static readonly VoxelBodyShape Shape = new(HalfWidth: 0.4f, Height: 1.4f, StepHeight: 0.55f);
    public const float VisionRadius = 0.45f;
    public const float EyeHeight = 1.1f;
    public const double NewbornEnergy = 0.6;
    public const double NewbornWater = 0.7;
    public const float MaximumSpeed = 6f;
    public const float Acceleration = 14f;
    public const float TurnSpeedRadians = 2.8f;
    public const float JumpSpeed = 5f;
    public const float Buoyancy = 12f;
    public const float ClimbSpeed = 1.5f;
    /// <summary>Seconds of fed adult life for libido to go from 0 to 1.</summary>
    public const float LibidoRiseSeconds = 600f;
    /// <summary>A plant within this distance of the body can be eaten.</summary>
    public const float EatReachMeters = 2f;
    /// <summary>Extra fatigue per second while swimming in deep water (fins make it cheaper).</summary>
    public const double SwimFatiguePerSecond = 0.004;
    public const float FeedReachMeters = 2f;
    public const double FeedRatePerSecond = 0.08;

    public uint Id { get; }
    public Genome Genome { get; }
    public FounderSpecies Lineage { get; }
    public uint MotherId { get; }
    public uint FatherId { get; }
    public int Generation { get; }
    public BodyPlan Body { get; }
    public SenseProfile Senses { get; }
    public LifeCycle Life { get; }
    public float Carnivory { get; }
    public float[] Motivations { get; }
    public Vector3 Position;
    public Vector3 Velocity;
    /// <summary>Rotation about +Y, radians (Godot RotateY convention: forward = (−sin, 0, −cos)).</summary>
    public float Yaw;
    public bool OnFloor { get; private set; }
    /// <summary>Current linear size (adult size × growth), the scale of the body box.</summary>
    public float SizeScale { get; private set; } = 1f;
    public bool IsCarnivore => Carnivory > 0.5f;
    public bool IsFemale => Genome.Sex == Sex.Female;
    public (float R, float G, float B) Color { get; }
    public float Fear { get; private set; }
    public float HeadPitchRadians { get; private set; }
    public float Gait { get; private set; }
    public bool Climbing { get; private set; }
    public VoxelMoveResult LastMove { get; private set; }
    public PhysiologyState Physiology { get; private set; } = new() { Energy = NewbornEnergy, Water = NewbornWater };
    public bool IsAlive => Physiology.IsAlive;
    public bool Sleeping => Physiology.IsSleeping;
    public double AgeSeconds => Physiology.AgeSeconds;
    public bool IsMature => Life.IsMature(Physiology.AgeSeconds);
    public float AgeFraction => (float)Math.Clamp(Physiology.AgeSeconds / (Life.LongevityYears * LifeCycle.SecondsPerYear), 0.0, 1.0);
    /// <summary>Strongest threat perceived at the last observation (feeds fear).</summary>
    public float LastThreat { get; internal set; }
    public ReadOnlySpan<float> Action => _action;
    public float LastTurnAction => _action[1];
    public bool WantsToMate => !Sleeping && _action[8] > 0.5f;
    public bool IsPregnant => _litter.Count > 0;
    public float GestationProgress => _gestationTotal <= 0 ? 0f : (float)Math.Clamp(1.0 - _gestationRemaining / _gestationTotal, 0.0, 1.0);
    public VoxelBodyShape BodyShape { get; private set; } = Shape;
    public float EyeHeightMeters => EyeHeight * SizeScale;
    /// <summary>Set by the world at each observation: own young within reach that is hungry.</summary>
    public bool OffspringInNeed { get; internal set; }

    private readonly float[] _action = new float[ObservationLayout.ActionCount];
    private readonly PhysiologyEngine _engine = new();
    private BodyTraits _traits = new();
    private readonly List<DamageEvent> _pendingDamage = new();
    private readonly DigProgress _dig = new();
    private readonly List<Genome> _litter = new();
    private uint _litterFather;
    private double _gestationRemaining;
    private double _gestationTotal;
    private bool _digging;
    private double _energyIntake;
    private double _waterIntake;
    private int _stepsSinceTraits = int.MaxValue;
    // Age at the last body refresh: saved so a restored world refreshes on the same steps (snapshot determinism).
    private double _traitsAge;

    public SimCreature(uint id, Vector3 position, Genome genome, FounderSpecies lineage, uint motherId = 0, uint fatherId = 0, int generation = 0)
    {
        Id = id;
        Position = position;
        Genome = genome;
        Lineage = lineage;
        MotherId = motherId;
        FatherId = fatherId;
        Generation = generation;
        Body = BodyPlan.FromGenome(genome);
        Senses = SenseProfile.FromGenome(genome);
        Life = LifeCycle.FromGenome(genome);
        Carnivory = genome.Phenotype("carnivory");
        Motivations = genome.Catalog.MotivationIndices().Select(genome.Phenotype).ToArray();
        Color = Hsv(genome.Phenotype("color_hue"), 0.55f, 0.9f);
        RefreshBody();
    }

    public Vector3 Forward => new(-MathF.Sin(Yaw), 0f, -MathF.Cos(Yaw));
    public Vector3 Right => new(MathF.Cos(Yaw), 0f, -MathF.Sin(Yaw));

    /// <summary>Body basis × local vector (local −Z is forward).</summary>
    public Vector3 ToWorld(Vector3 local)
    {
        var (s, c) = MathF.SinCos(Yaw);
        return new Vector3(c * local.X + s * local.Z, local.Y, -s * local.X + c * local.Z);
    }

    /// <summary>Inverse basis × world vector.</summary>
    public Vector3 ToLocal(Vector3 world)
    {
        var (s, c) = MathF.SinCos(Yaw);
        return new Vector3(c * world.X - s * world.Z, world.Y, s * world.X + c * world.Z);
    }

    public void SetAction(ReadOnlySpan<float> action)
    {
        if (action.Length != ObservationLayout.ActionCount)
            throw new ArgumentException($"Expected {ObservationLayout.ActionCount} actions.", nameof(action));
        for (var i = 0; i < _action.Length; i++) _action[i] = Math.Clamp(action[i], -1f, 1f);
    }

    public bool IsKinOf(SimCreature other) =>
        other.MotherId == Id || other.FatherId == Id || MotherId == other.Id || FatherId == other.Id
        || (MotherId != 0 && MotherId == other.MotherId);

    public bool IsParentOf(SimCreature other) => other.MotherId == Id || other.FatherId == Id;

    /// <summary>Growth changes size, body box and metabolism; recomputed once per simulated second.</summary>
    private void RefreshBody() => RefreshBody(Physiology.AgeSeconds);

    private void RefreshBody(double age)
    {
        _stepsSinceTraits = 0;
        _traitsAge = age;
        var growth = Life.SizeFraction(age);
        SizeScale = Math.Clamp(Body.Size * growth, 0.25f, 4f);
        var s = Math.Clamp(SizeScale, 0.5f, 2.5f);
        BodyShape = new VoxelBodyShape(Shape.HalfWidth * s, Shape.Height * s, Math.Clamp(Body.StepHeight * growth, 0.3f, 1.4f));
        var plan = Body with { Size = SizeScale };
        var traits = new BodyTraits
        {
            Size = SizeScale,
            Armor = Math.Clamp(plan.Armor, 0f, 1f),
            Insulation = Math.Clamp(0.3 + 0.7 * plan.Fur, 0, 1),
            // Age is the Gompertz roll of LifeCycle; the etapa-1 senescence ramp is switched off.
            MaximumAgeSeconds = 1e12,
        };
        // Metabolism applies Mass^0.75 itself: hand it the per-unit-mass rate of the body plan + senses.
        var massTerm = Math.Pow(traits.Mass, BodyPlan.KleiberExponent);
        var pregnancy = IsPregnant ? 1.3 : 1.0;
        // Energy is a fraction of the reserves, and reserves scale with body mass: every energy flow (costs here,
        // food in StepPhysiology) is divided by the mass. Absolute costs follow Kleiber (M^0.75), so the time to
        // starve grows as M^0.25. Without this a big founder (B, mass ~4) starved in 74 s, faster than a small one.
        var reserve = Math.Max(MinimumReserve, traits.Mass);
        _traits = traits with
        {
            BasalMetabolicRate = (plan.BasalMetabolicRate + Senses.ExtraBasalRate) * pregnancy / massTerm / reserve,
            MovementCost = traits.MovementCost / reserve,
            TurnCost = traits.TurnCost / reserve,
            BrainCost = traits.BrainCost / reserve,
            ShiverEnergyCost = traits.ShiverEnergyCost / reserve,
        };
    }

    /// <summary>One 1/60 s physics step.</summary>
    internal void Step(SimWorld world, float dt)
    {
        if (!IsAlive) return;
        if (++_stepsSinceTraits >= SimWorld.PhysicsHz) RefreshBody();
        Move(world.Terrain.Grid, dt);
        var landing = LastMove.LandingSpeed;
        if (landing > 12f)
            ApplyDamage(world, BodyZone.Legs, Math.Clamp((landing - 12f) / 30f, 0f, 1f), DeathCause.Fall);
        HandleDiscreteActions(world, dt);
        StepPhysiology(world, dt);
        if (!IsAlive) return;
        Fear = FearModel.Step(Fear, LastThreat, (float)Physiology.Pain, dt);
        if (IsPregnant)
        {
            _gestationRemaining -= dt;
            if (_gestationRemaining <= 0) world.GiveBirth(this, _litter.ToArray(), _litterFather);
        }
    }

    private bool SwimmingDeep => LastMove.InWater && !OnFloor;

    private void Move(VoxelGrid grid, float dt)
    {
        var action = _action;
        // Asleep: no locomotion, turning or jumping until woken (sleep action < 0 or pain).
        var thrust = Sleeping ? 0f : action[0];
        var turn = Sleeping ? 0f : action[1];
        Gait = (action[2] + 1f) * 0.5f;
        HeadPitchRadians = SimClock.Lerp(-0.65f, 0.65f, (action[3] + 1f) * 0.5f);
        var legFactor = SimClock.Lerp(0.25f, 1f, (float)Physiology.LegsIntegrity);
        var fatigueFactor = Physiology.Fatigue > 0.8 ? 0.5f : 1f;
        var swimFactor = LastMove.InWater ? Body.SwimFactor : 1f;
        // Young animals are slower: speed follows the body (BodyPlan) scaled by growth^0.25.
        var maxSpeed = Body.MaxSpeed * MathF.Pow(Math.Clamp(SizeScale / MathF.Max(0.1f, Body.Size), 0.1f, 1f), 0.25f);
        var targetSpeed = maxSpeed * Gait * legFactor * fatigueFactor * swimFactor * thrust;
        Yaw += -turn * TurnSpeedRadians * dt;
        var forward = Forward;
        var velocity = Velocity;
        velocity.X = MoveToward(velocity.X, forward.X * targetSpeed, Acceleration * dt);
        velocity.Z = MoveToward(velocity.Z, forward.Z * targetSpeed, Acceleration * dt);
        if (!OnFloor) velocity.Y -= VoxelPhysics.Gravity * dt;
        if (LastMove.InWater)
        {
            // Float: buoyancy plus strong vertical drag keeps the head near the surface.
            velocity.Y += Buoyancy * dt;
            velocity.Y *= 1f - MathF.Min(1f, 3f * dt);
        }
        if (!Sleeping && action[4] > 0.5f && (OnFloor || LastMove.InWater))
            velocity.Y = LastMove.InWater ? MathF.Max(velocity.Y, JumpSpeed * 0.5f) : JumpSpeed;

        // Climbing needs functional claws (BodyPlan.ClimbFactor); a solid face right ahead lifts the body.
        Climbing = false;
        var climb = Body.ClimbFactor;
        if (!Sleeping && action[11] > 0.5f && climb > 0f)
        {
            var probe = Position + forward * 0.15f;
            if (VoxelPhysics.BoxBlocked(grid, probe.X, Position.Y + 0.05f, probe.Z, BodyShape))
            {
                Climbing = true;
                velocity.Y = ClimbSpeed * (0.4f + 0.6f * climb) * legFactor * fatigueFactor;
            }
        }

        var body = new VoxelBodyState
        {
            X = Position.X, Y = Position.Y, Z = Position.Z,
            VelocityX = velocity.X, VelocityY = velocity.Y, VelocityZ = velocity.Z,
            OnFloor = OnFloor
        };
        LastMove = VoxelPhysics.Move(grid, ref body, BodyShape, dt);
        Position = new Vector3(body.X, body.Y, body.Z);
        Velocity = new Vector3(body.VelocityX, body.VelocityY, body.VelocityZ);
        OnFloor = body.OnFloor;
    }

    private void HandleDiscreteActions(SimWorld world, float dt)
    {
        var action = _action;
        // A sleeping creature does not bite, drink, mate or feed (sleep is a real trade-off).
        if (Sleeping) return;
        if (action[9] > 0f) world.EmitSound(Id, Position, action[9]);
        if (action[5] > 0.5f) Eat(world, dt);
        if (action[6] > 0.5f && world.Terrain.IsNearDrinkableWater(Position)) _waterIntake += dt * 0.35f;
        _digging = action[10] > 0.5f && TryDig(world.Terrain.Grid, dt);
        if (action[10] <= 0.5f) _dig.Reset();
        if (action[12] > 0.5f) Feed(world, dt);
    }

    /// <summary>
    /// Eat: plants (herbivore digestion falls with carnivory), carcasses (meat digestion rises with it),
    /// or a bite on a live creature in front for a carnivorous body (T2.5).
    /// </summary>
    private void Eat(SimWorld world, float dt)
    {
        var plantEfficiency = 1f - 0.85f * Carnivory;
        var meatEfficiency = 0.15f + 0.85f * Carnivory;
        var mouth = Position + Forward * (0.5f * SizeScale);
        var meat = world.ConsumeCarcass(mouth, 1.5f, dt * 0.3f);
        if (meat > 0f)
        {
            _energyIntake += meat * meatEfficiency;
            world.RecordAte(Id, 1, meat);
            return;
        }
        if (Carnivory > 0.3f && world.TryBite(this, mouth, dt)) return;
        var amount = world.Plants.ConsumeNearest(Position, EatReachMeters, dt * 0.3f, out var thorny);
        _energyIntake += amount * 0.8f * plantEfficiency;
        if (amount > 0f) world.RecordAte(Id, 0, amount);
        if (thorny && amount > 0f) ApplyDamage(world, BodyZone.Head, amount * 0.08f, DeathCause.HeadInjury);
    }

    /// <summary>Feed: hand energy to own hungry young within reach (parental care, T2.4a).</summary>
    private void Feed(SimWorld world, float dt)
    {
        if (!IsMature || Physiology.Energy < 0.3) return;
        var child = world.NearestOwnYoung(this, FeedReachMeters, hungryBelow: 0.6);
        if (child is null) return;
        var amount = Math.Min(FeedRatePerSecond * dt, Physiology.Energy - 0.3);
        if (amount <= 0) return;
        Physiology = Physiology with { Energy = Physiology.Energy - amount };
        child.ReceiveFood(amount * 0.8);
        world.RecordFed(Id, child.Id, (float)amount);
    }

    internal void ReceiveFood(double energy) => _energyIntake += energy;

    private const double MinimumReserve = 0.02;

    /// <summary>Energy reserve relative to the reference animal: the current (grown) body mass.</summary>
    public double ReserveMass => Math.Max(MinimumReserve, _traits.Mass);

    internal void SetEnergyForTest(double energy) => Physiology = Physiology with { Energy = Math.Clamp(energy, 0.0, 1.0), Water = 1.0 };

    /// <summary>Founders enter the world as young adults (their growth and fertility follow from this age).</summary>
    internal void SetAgeForFounder(double ageSeconds)
    {
        Physiology = Physiology with { AgeSeconds = Math.Max(0.0, ageSeconds) };
        RefreshBody();
    }

    /// <summary>Works on the first solid block along the gaze within reach (looking down digs the floor).</summary>
    private bool TryDig(VoxelGrid grid, float dt)
    {
        var pitch = HeadPitchRadians - 0.35f; // muzzle aims slightly below the eye line
        var direction = Vector3.Normalize(Forward * MathF.Cos(pitch) + Vector3.UnitY * MathF.Sin(pitch));
        var origin = Position + Vector3.UnitY * (BodyShape.Height * 0.55f);
        var hit = VoxelRaycast.Cast(grid, origin.X, origin.Y, origin.Z, direction.X, direction.Y, direction.Z,
            Digging.ReachMeters * MathF.Max(0.7f, SizeScale), BlockTypes.IsSolid);
        if (!hit.Hit)
        {
            _dig.Reset();
            return false;
        }
        // Claws dig faster (BodyPlan.DigFactor; 1 ≈ etapa-1 speed).
        if (_dig.Work(hit.X, hit.Y, hit.Z, hit.Block, dt * Body.DigFactor)) grid.Set(hit.X, hit.Y, hit.Z, BlockType.Air);
        return true;
    }

    private void StepPhysiology(SimWorld world, float dt)
    {
        var ambient = world.AmbientTemperature(Position);
        var action = _action;
        // Digging, climbing and swimming in deep water cost like running at full effort.
        var working = _digging || Climbing || (SwimmingDeep && MathF.Abs(action[0]) > 0.1f);
        var result = _engine.Step(Physiology, _traits, new PhysiologyInput
        {
            AmbientTemperature = ambient,
            Thrust = working ? 1f : action[0],
            Gait = working ? 1f : Gait,
            Turn = action[1],
            EnergyIntake = _energyIntake / ReserveMass,
            WaterIntake = _waterIntake,
            Sleeping = action[7] > 0.5f && Velocity.LengthSquared() < 0.1f && !SwimmingDeep,
            WakeRequested = action[7] < 0f,
            DamageEvents = _pendingDamage.Count == 0 ? Array.Empty<DamageEvent>() : _pendingDamage.ToArray()
        }, dt);
        _energyIntake = 0;
        _waterIntake = 0;
        _pendingDamage.Clear();
        var state = result.State;
        // Torpor (T2.2c): asleep in the cold, part of the basal metabolism is saved; any trace of the gene costs upkeep.
        var torpor = Genome.Phenotype("torpor");
        if (torpor > 0f && state.IsAlive)
        {
            var basal = _traits.BasalMetabolicRate * Math.Pow(_traits.Mass, BodyPlan.KleiberExponent) * dt;
            var saved = state.IsSleeping && ambient < 12.0 ? 0.6 * torpor * basal : 0.0;
            var upkeep = 0.1 * torpor * basal;
            state = state with { Energy = Math.Clamp(state.Energy + saved - upkeep, 0.0, 1.0) };
        }
        var died = result.DiedThisStep;
        if (state.IsAlive)
        {
            // Swimming tires (T2.1b): open water kills by exhaustion; fins make it cheaper.
            if (SwimmingDeep)
            {
                var fatigue = Math.Min(1.0, state.Fatigue + dt * SwimFatiguePerSecond / Math.Max(0.45, Body.SwimFactor) * 0.45);
                state = state with { Fatigue = fatigue };
                if (fatigue >= 0.99)
                {
                    var head = Math.Max(0.0, state.HeadIntegrity - dt * 0.2);
                    state = head <= 0 ? state with { HeadIntegrity = 0, IsAlive = false, DeathCause = DeathCause.Drowning } : state with { HeadIntegrity = head };
                    died = !state.IsAlive;
                }
            }
            // Senescence (T2.2c): Gompertz roll from the longevity gene.
            if (state.IsAlive && Life.DiesOfAge(state.AgeSeconds, dt, world.Random))
            {
                state = state with { IsAlive = false, DeathCause = DeathCause.Age };
                died = true;
            }
            // Libido builds in fed adults and is satisfied by mating (reward setpoint 0).
            if (state.IsAlive && Life.IsMature(state.AgeSeconds) && !IsPregnant && state.Energy > 0.4)
                state = state with { Libido = Math.Min(1.0, state.Libido + dt / LibidoRiseSeconds) };
        }
        Physiology = state;
        if (died) world.RecordDeath(Id, Physiology.DeathCause);
    }

    /// <summary>Conception: the mother carries the litter for its gestation time; both parents pay the yolk and are satisfied.</summary>
    internal void Conceive(IReadOnlyList<Genome> litter, uint fatherId)
    {
        _litter.Clear();
        _litter.AddRange(litter);
        _litterFather = fatherId;
        _gestationTotal = Math.Max(1.0, Genome.Phenotype("gestation_days") * SimClock.DayDurationSeconds);
        _gestationRemaining = _gestationTotal;
        RefreshBody();
    }

    internal void PayMating()
    {
        Physiology = Physiology with
        {
            Energy = Math.Max(0.0, Physiology.Energy - (IsFemale ? Mating.YolkCost : Mating.SpermCost)),
            Libido = 0.0,
        };
    }

    internal void EndPregnancy()
    {
        _litter.Clear();
        _gestationRemaining = _gestationTotal = 0;
        RefreshBody();
    }

    /// <summary>Takes a bite: damage to the torso, armour absorbs part of it.</summary>
    internal float TakeBite(SimWorld world, float damage, uint attackerId)
    {
        var absorbed = damage * (1f - Body.Armor);
        ApplyDamage(world, BodyZone.Torso, absorbed, DeathCause.Bite, attackerId);
        return absorbed;
    }

    public void ApplyDamage(SimWorld world, BodyZone zone, float amount, DeathCause lethalCause, uint sourceId = 0)
    {
        amount = MathF.Max(0f, amount);
        if (amount <= 0f) return;
        var coreZone = zone switch
        {
            BodyZone.Head => DamageZone.Head,
            BodyZone.Torso => DamageZone.Torso,
            _ => DamageZone.Legs
        };
        _pendingDamage.Add(new DamageEvent(coreZone, amount, lethalCause));
        world.RecordDamage(Id, zone, sourceId);
    }

    /// <summary>Full state between physics steps (pending intake and damage are always empty there).</summary>
    internal void Save(BinaryWriter w)
    {
        w.Write(Id);
        GenomeIO.Write(w, Genome);
        w.Write((byte)Lineage); w.Write(MotherId); w.Write(FatherId); w.Write(Generation);
        w.Write(Position.X); w.Write(Position.Y); w.Write(Position.Z);
        w.Write(Velocity.X); w.Write(Velocity.Y); w.Write(Velocity.Z);
        w.Write(Yaw); w.Write(OnFloor); w.Write(Fear); w.Write(HeadPitchRadians); w.Write(Gait); w.Write(Climbing);
        w.Write(LastMove.OnFloor); w.Write(LastMove.HitWall); w.Write(LastMove.HitCeiling); w.Write(LastMove.InWater);
        w.Write(LastMove.LandingSpeed); w.Write(LastMove.Stepped);
        var s = Physiology;
        w.Write(s.Energy); w.Write(s.Water); w.Write(s.BodyTemperature); w.Write(s.HeadIntegrity); w.Write(s.TorsoIntegrity);
        w.Write(s.LegsIntegrity); w.Write(s.Fatigue); w.Write(s.Pain); w.Write(s.Libido); w.Write(s.AgeSeconds);
        w.Write(s.IsSleeping); w.Write(s.IsAlive); w.Write((byte)s.DeathCause);
        w.Write(LastThreat);
        foreach (var a in _action) w.Write(a);
        var (target, seconds, fraction) = _dig.Save();
        w.Write(target.HasValue);
        if (target is { } t) { w.Write(t.X); w.Write(t.Y); w.Write(t.Z); }
        w.Write(seconds); w.Write(fraction); w.Write(_digging);
        w.Write(_litter.Count);
        foreach (var g in _litter) GenomeIO.Write(w, g);
        w.Write(_litterFather); w.Write(_gestationRemaining); w.Write(_gestationTotal);
        w.Write(_traitsAge); w.Write(_stepsSinceTraits);
    }

    internal static SimCreature Load(BinaryReader r, GeneCatalog catalog)
    {
        var id = r.ReadUInt32();
        var genome = GenomeIO.Read(r, catalog);
        var lineage = (FounderSpecies)r.ReadByte();
        var mother = r.ReadUInt32();
        var father = r.ReadUInt32();
        var generation = r.ReadInt32();
        var c = new SimCreature(id, new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), genome, lineage, mother, father, generation)
        {
            Velocity = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
            Yaw = r.ReadSingle()
        };
        c.OnFloor = r.ReadBoolean(); c.Fear = r.ReadSingle(); c.HeadPitchRadians = r.ReadSingle(); c.Gait = r.ReadSingle();
        c.Climbing = r.ReadBoolean();
        c.LastMove = new VoxelMoveResult(r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean(), r.ReadSingle(), r.ReadBoolean());
        c.Physiology = new PhysiologyState
        {
            Energy = r.ReadDouble(), Water = r.ReadDouble(), BodyTemperature = r.ReadDouble(), HeadIntegrity = r.ReadDouble(),
            TorsoIntegrity = r.ReadDouble(), LegsIntegrity = r.ReadDouble(), Fatigue = r.ReadDouble(), Pain = r.ReadDouble(),
            Libido = r.ReadDouble(), AgeSeconds = r.ReadDouble(), IsSleeping = r.ReadBoolean(), IsAlive = r.ReadBoolean(),
            DeathCause = (DeathCause)r.ReadByte()
        };
        c.LastThreat = r.ReadSingle();
        for (var i = 0; i < c._action.Length; i++) c._action[i] = r.ReadSingle();
        (int, int, int)? target = r.ReadBoolean() ? (r.ReadInt32(), r.ReadInt32(), r.ReadInt32()) : null;
        c._dig.Restore(target, r.ReadSingle(), r.ReadSingle());
        c._digging = r.ReadBoolean();
        var litter = r.ReadInt32();
        for (var i = 0; i < litter; i++) c._litter.Add(GenomeIO.Read(r, catalog));
        c._litterFather = r.ReadUInt32(); c._gestationRemaining = r.ReadDouble(); c._gestationTotal = r.ReadDouble();
        c.RefreshBody(r.ReadDouble());
        c._stepsSinceTraits = r.ReadInt32();
        return c;
    }

    private static float MoveToward(float from, float to, float delta) =>
        MathF.Abs(to - from) <= delta ? to : from + MathF.Sign(to - from) * delta;

    internal static (float R, float G, float B) Hsv(float h, float s, float v)
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
