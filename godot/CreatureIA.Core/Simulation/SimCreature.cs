using System.Numerics;
using CreatureIA.Core.Physiology;
using CreatureIA.Core.Voxel;

namespace CreatureIA.Core.Simulation;

/// <summary>Body zone codes of the DAMAGED event (protocol v2).</summary>
public enum BodyZone : byte { Head, Torso, Legs }

/// <summary>
/// A creature in the simulator: voxel kinematics, actuators, discrete actions (eat, drink, dig,
/// call), physiology and fear. Port of the Godot CreatureBody + Actuators + CreatureState (etapa 1),
/// same constants and order of operations.
/// </summary>
public sealed class SimCreature
{
    /// <summary>Default body box (size becomes a gene in etapa 2).</summary>
    public static readonly VoxelBodyShape Shape = new(HalfWidth: 0.4f, Height: 1.4f, StepHeight: 0.55f);
    public const float VisionRadius = 0.45f;
    public const float EyeHeight = 1.1f;
    public const double NewbornEnergy = 0.6;
    public const double NewbornWater = 0.7;
    public const float MaximumSpeed = 6f;
    public const float Acceleration = 14f;
    public const float TurnSpeedRadians = 2.8f;
    public const float JumpSpeed = 5f;
    public const float SwimSpeedFactor = 0.45f;
    public const float Buoyancy = 12f;
    public const float ClimbSpeed = 1.5f;

    public uint Id { get; }
    public Vector3 Position;
    public Vector3 Velocity;
    /// <summary>Rotation about +Y, radians (Godot RotateY convention: forward = (−sin, 0, −cos)).</summary>
    public float Yaw;
    public bool OnFloor { get; private set; }
    public float SizeScale { get; } = 1f;
    public bool IsCarnivore { get; }
    public (float R, float G, float B) Color { get; }
    public float Fear { get; private set; }
    public float HeadPitchRadians { get; private set; }
    public float Gait { get; private set; }
    public bool Climbing { get; private set; }
    public VoxelMoveResult LastMove { get; private set; }
    public PhysiologyState Physiology { get; private set; } = new() { Energy = NewbornEnergy, Water = NewbornWater };
    public bool IsAlive => Physiology.IsAlive;
    public bool Sleeping => Physiology.IsSleeping;
    public float MaximumAgeSeconds { get; private set; } = 3600f;
    /// <summary>Strongest threat perceived at the last observation (feeds fear).</summary>
    public float LastThreat { get; internal set; }
    public ReadOnlySpan<float> Action => _action;
    public float LastTurnAction => _action[1];

    private readonly float[] _action = new float[ObservationLayout.ActionCount];
    private readonly PhysiologyEngine _engine = new();
    private static readonly BodyTraits Traits = new();
    private readonly List<DamageEvent> _pendingDamage = new();
    private readonly DigProgress _dig = new();
    private bool _digging;
    private double _energyIntake;
    private double _waterIntake;

    public SimCreature(uint id, Vector3 position, (float R, float G, float B) color)
    {
        Id = id;
        Position = position;
        Color = color;
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

    /// <summary>One 1/60 s physics step.</summary>
    internal void Step(SimWorld world, float dt)
    {
        if (!IsAlive) return;
        Move(world.Terrain.Grid, dt);
        var landing = LastMove.LandingSpeed;
        if (landing > 12f)
            ApplyDamage(world, BodyZone.Legs, Math.Clamp((landing - 12f) / 30f, 0f, 1f), DeathCause.Fall);
        HandleDiscreteActions(world, dt);
        StepPhysiology(world, dt);
        Fear = FearModel.Step(Fear, LastThreat, (float)Physiology.Pain, dt);
    }

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
        var swimFactor = LastMove.InWater ? SwimSpeedFactor : 1f;
        var targetSpeed = MaximumSpeed * Gait * legFactor * fatigueFactor * swimFactor * thrust;
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

        // Climbing: with a solid face right ahead, the climb action holds the body on the wall and lifts it.
        Climbing = false;
        if (!Sleeping && action[11] > 0.5f)
        {
            var probe = Position + forward * 0.15f;
            if (VoxelPhysics.BoxBlocked(grid, probe.X, Position.Y + 0.05f, probe.Z, Shape))
            {
                Climbing = true;
                velocity.Y = ClimbSpeed * legFactor * fatigueFactor;
            }
        }

        var body = new VoxelBodyState
        {
            X = Position.X, Y = Position.Y, Z = Position.Z,
            VelocityX = velocity.X, VelocityY = velocity.Y, VelocityZ = velocity.Z,
            OnFloor = OnFloor
        };
        LastMove = VoxelPhysics.Move(grid, ref body, Shape, dt);
        Position = new Vector3(body.X, body.Y, body.Z);
        Velocity = new Vector3(body.VelocityX, body.VelocityY, body.VelocityZ);
        OnFloor = body.OnFloor;
    }

    private void HandleDiscreteActions(SimWorld world, float dt)
    {
        var action = _action;
        // A sleeping creature does not bite or drink (sleep is a real trade-off).
        if (Sleeping) return;
        if (action[9] > 0f) world.EmitSound(Id, Position, action[9]);
        if (action[5] > 0.5f)
        {
            var amount = world.Plants.ConsumeNearest(Position, 2f, dt * 0.3f, out var thorny);
            _energyIntake += amount * 0.8f;
            if (amount > 0f) world.RecordAte(Id, 0, amount);
            if (thorny && amount > 0f) ApplyDamage(world, BodyZone.Head, amount * 0.08f, DeathCause.HeadInjury);
        }
        if (action[6] > 0.5f && world.Terrain.IsNearDrinkableWater(Position)) _waterIntake += dt * 0.35f;
        _digging = action[10] > 0.5f && TryDig(world.Terrain.Grid, dt);
        if (action[10] <= 0.5f) _dig.Reset();
    }

    /// <summary>Works on the first solid block along the gaze within reach (looking down digs the floor).</summary>
    private bool TryDig(VoxelGrid grid, float dt)
    {
        var pitch = HeadPitchRadians - 0.35f; // muzzle aims slightly below the eye line
        var direction = Vector3.Normalize(Forward * MathF.Cos(pitch) + Vector3.UnitY * MathF.Sin(pitch));
        var origin = Position + Vector3.UnitY * (Shape.Height * 0.55f);
        var hit = VoxelRaycast.Cast(grid, origin.X, origin.Y, origin.Z, direction.X, direction.Y, direction.Z,
            Digging.ReachMeters, BlockTypes.IsSolid);
        if (!hit.Hit)
        {
            _dig.Reset();
            return false;
        }
        if (_dig.Work(hit.X, hit.Y, hit.Z, hit.Block, dt)) grid.Set(hit.X, hit.Y, hit.Z, BlockType.Air);
        return true;
    }

    private void StepPhysiology(SimWorld world, float dt)
    {
        var ambient = world.Terrain.Sample(Position.X, Position.Z).TemperatureCelsius + world.Clock.TemperatureOffsetCelsius;
        var action = _action;
        // Digging and climbing cost like running at full effort.
        var working = _digging || Climbing;
        var result = _engine.Step(Physiology, Traits, new PhysiologyInput
        {
            AmbientTemperature = ambient,
            Thrust = working ? 1f : action[0],
            Gait = working ? 1f : Gait,
            Turn = action[1],
            EnergyIntake = _energyIntake,
            WaterIntake = _waterIntake,
            Sleeping = action[7] > 0.5f && Velocity.LengthSquared() < 0.1f,
            WakeRequested = action[7] < 0f,
            DamageEvents = _pendingDamage.Count == 0 ? Array.Empty<DamageEvent>() : _pendingDamage.ToArray()
        }, dt);
        _energyIntake = 0;
        _waterIntake = 0;
        _pendingDamage.Clear();
        Physiology = result.State;
        MaximumAgeSeconds = MathF.Max(MaximumAgeSeconds, (float)Physiology.AgeSeconds);
        if (result.DiedThisStep) world.RecordDeath(Id, Physiology.DeathCause);
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

    private static float MoveToward(float from, float to, float delta) =>
        MathF.Abs(to - from) <= delta ? to : from + MathF.Sign(to - from) * delta;
}
