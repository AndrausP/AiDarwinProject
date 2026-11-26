using CreatureIA.Core.Voxel;
using Godot;

namespace CreatureIA.Godot.Creature;

public sealed class ActionLatch
{
    public const int ActionCount = 12;
    public const int PhysicsTicksPerAction = 6;
    private readonly float[] _action = new float[ActionCount];
    public int RemainingTicks { get; private set; }
    public ReadOnlySpan<float> Current => _action;

    public void Set(ReadOnlySpan<float> action)
    {
        if (action.Length != ActionCount) throw new ArgumentException($"Expected {ActionCount} actions.", nameof(action));
        for (var i = 0; i < ActionCount; i++) _action[i] = Mathf.Clamp(action[i], -1f, 1f);
        RemainingTicks = PhysicsTicksPerAction;
    }

    public void Tick()
    {
        if (RemainingTicks > 0) RemainingTicks--;
    }
}

public partial class Actuators : Node
{
    [Export] public float MaximumSpeed { get; set; } = 6f;
    [Export] public float Acceleration { get; set; } = 14f;
    [Export] public float TurnSpeedRadians { get; set; } = 2.8f;
    [Export] public float JumpSpeed { get; set; } = 5f;
    /// <summary>Swimming: horizontal speed multiplier and upward buoyancy (m/s²) while the torso is in water.</summary>
    [Export] public float SwimSpeedFactor { get; set; } = 0.45f;
    [Export] public float Buoyancy { get; set; } = 12f;

    public float HeadPitchRadians { get; private set; }
    public float Gait { get; private set; }
    public float EffortSquared { get; private set; }
    /// <summary>Vertical speed while climbing a wall, m/s (base body; claws scale it from etapa 3).</summary>
    [Export] public float ClimbSpeed { get; set; } = 1.5f;

    public VoxelMoveResult LastMove { get; private set; }
    public bool Climbing { get; private set; }

    public void Apply(CreatureBody body, VoxelGrid grid, CreatureState state, ReadOnlySpan<float> action, double delta)
    {
        var dt = (float)delta;
        // Asleep: no locomotion, turning or jumping until woken (sleep action < 0 or pain).
        var thrust = state.Sleeping ? 0f : action[0];
        var turn = state.Sleeping ? 0f : action[1];
        Gait = (action[2] + 1f) * 0.5f;
        HeadPitchRadians = Mathf.Lerp(-0.65f, 0.65f, (action[3] + 1f) * 0.5f);
        var legFactor = Mathf.Lerp(0.25f, 1f, state.LegsIntegrity);
        var fatigueFactor = state.Fatigue > 0.8f ? 0.5f : 1f;
        var swimFactor = LastMove.InWater ? SwimSpeedFactor : 1f;
        var targetSpeed = MaximumSpeed * Gait * legFactor * fatigueFactor * swimFactor * thrust;
        body.RotateY(-turn * TurnSpeedRadians * dt);
        var forward = -body.GlobalTransform.Basis.Z;
        var target = forward * targetSpeed;
        var velocity = body.Velocity;
        velocity.X = Mathf.MoveToward(velocity.X, target.X, Acceleration * dt);
        velocity.Z = Mathf.MoveToward(velocity.Z, target.Z, Acceleration * dt);
        if (!body.IsOnFloor()) velocity.Y -= VoxelPhysics.Gravity * dt;
        if (LastMove.InWater)
        {
            // Float: buoyancy plus strong vertical drag keeps the head near the surface.
            velocity.Y += Buoyancy * dt;
            velocity.Y *= 1f - Mathf.Min(1f, 3f * dt);
        }
        if (!state.Sleeping && action[4] > 0.5f && (body.IsOnFloor() || LastMove.InWater))
            velocity.Y = LastMove.InWater ? Mathf.Max(velocity.Y, JumpSpeed * 0.5f) : JumpSpeed;

        // Climbing: with a solid face right ahead (cliff, trunk), the climb action holds the body
        // on the wall and lifts it; pushing forward carries it over the top.
        Climbing = false;
        if (!state.Sleeping && action[11] > 0.5f)
        {
            var probe = body.GlobalPosition + forward * 0.15f;
            if (VoxelPhysics.BoxBlocked(grid, probe.X, body.GlobalPosition.Y + 0.05f, probe.Z, CreatureBody.Shape))
            {
                Climbing = true;
                velocity.Y = ClimbSpeed * legFactor * fatigueFactor;
            }
        }

        var voxel = new VoxelBodyState
        {
            X = body.GlobalPosition.X,
            Y = body.GlobalPosition.Y,
            Z = body.GlobalPosition.Z,
            VelocityX = velocity.X,
            VelocityY = velocity.Y,
            VelocityZ = velocity.Z,
            OnFloor = body.IsOnFloor()
        };
        LastMove = VoxelPhysics.Move(grid, ref voxel, CreatureBody.Shape, dt);
        body.GlobalPosition = new Vector3(voxel.X, voxel.Y, voxel.Z);
        body.SetKinematics(new Vector3(voxel.VelocityX, voxel.VelocityY, voxel.VelocityZ), voxel.OnFloor);
        EffortSquared = thrust * thrust * Gait * Gait + turn * turn;
    }
}
