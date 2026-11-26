using CreatureIA.Core.Physiology;
using CreatureIA.Core.Voxel;
using CreatureIA.Godot.Net;
using CreatureIA.Godot.World;
using Godot;
using CoreDamageZone = CreatureIA.Core.Physiology.DamageZone;
using CoreDeathCause = CreatureIA.Core.Physiology.DeathCause;

namespace CreatureIA.Godot.Creature;

/// <summary>
/// A creature in the voxel world. Not a physics body: it moves with <see cref="VoxelPhysics"/>
/// against the block grid and is seen by others through <see cref="Senses"/> geometry checks.
/// </summary>
public partial class CreatureBody : Node3D
{
    /// <summary>Default body box (Task 002 etapa 1; size becomes a gene in etapa 2).</summary>
    public static readonly VoxelBodyShape Shape = new(HalfWidth: 0.4f, Height: 1.4f, StepHeight: 0.55f);
    public const float VisionRadius = 0.45f;

    public Vector3 Velocity { get; private set; }
    private bool _onFloor;
    public bool IsOnFloor() => _onFloor;
    public bool InWater => _actuators.LastMove.InWater;
    public bool IsBlocked => _actuators.LastMove.HitWall;
    public bool IsClimbing => _actuators.Climbing;

    /// <summary>Linear body scale relative to the default animal (gene from etapa 2; 1 for everyone now).</summary>
    public float SizeScale { get; set; } = 1f;
    /// <summary>Diet (etapa 2 introduces carnivores); herbivore for everyone in etapa 1.</summary>
    public bool IsCarnivore { get; set; }
    /// <summary>Fear feeling 0..1 (interoception index 11), driven by perceived threat and pain.</summary>
    public float Fear { get; private set; }
    /// <summary>Digging progress on the current block, 0..1 (inspector/visuals).</summary>
    public float DigFraction => _dig.Fraction;
    private readonly DigProgress _dig = new();
    private bool _digging;

    internal void SetKinematics(Vector3 velocity, bool onFloor)
    {
        Velocity = velocity;
        _onFloor = onFloor;
    }

    [Signal] public delegate void DiedEventHandler(uint creatureId, int cause);
    public event Action<uint, CoreDeathCause>? DeathOccurred;
    public event Action<uint, byte, float>? AteResource;
    public event Action<uint, BodyZone, uint>? DamageOccurred;

    [Export] public uint CreatureId { get; set; }
    [Export] public Color BodyColor { get; set; } = new(0.65f, 0.72f, 0.9f);
    [Export] public NodePath PlantManagerPath { get; set; } = "../../PlantManager";
    [Export] public NodePath WaterBodiesPath { get; set; } = "../../WaterBodies";
    [Export] public NodePath BiomeMapPath { get; set; } = "../../BiomeMap";
    [Export] public NodePath DayNightPath { get; set; } = "../../DayNightCycle";
    [Export] public NodePath SeasonPath { get; set; } = "../../SeasonCycle";
    [Export] public NodePath SensoryFieldPath { get; set; } = "../../SensoryField";

    public CreatureState State { get; } = new();

    /// <summary>Profiling only: physics ticks [near water, drink action, drank].</summary>
    public static readonly long[] DrinkStats = new long[3];
    public float HeadPitchRadians => _actuators.HeadPitchRadians;
    public float LastTurnAction => _latch.Current[1];
    public ReadOnlySpan<float> CurrentAction => _latch.Current;
    public Senses SensorSuite => _senses;
    internal BiomeMap? Biomes => _biomes;
    internal PlantManager? Plants => _plants;

    private readonly ActionLatch _latch = new();
    private Actuators _actuators = null!;
    private Senses _senses = null!;
    private PlantManager? _plants;
    private WaterBodies? _water;
    private BiomeMap? _biomes;
    private DayNightCycle? _dayNight;
    private SeasonCycle? _season;
    private SensoryField? _sensoryField;
    private readonly PhysiologyEngine _physiology = new();
    private readonly BodyTraits _traits = new();
    private readonly List<DamageEvent> _pendingDamage = new();
    /// <summary>
    /// Newborns start below the homeostatic setpoints (energy 0.8, water 0.8), like a yolk-fed
    /// hatchling (plan 8.2). Starting full (1.0) made eating *raise* the deviation for the first
    /// minutes, so learners discovered "stand still, don't eat" before hunger ever arrived.
    /// </summary>
    public const double NewbornEnergy = 0.6;
    public const double NewbornWater = 0.7;

    private PhysiologyState _coreState = new() { Energy = NewbornEnergy, Water = NewbornWater };
    private double _energyIntake;
    private double _waterIntake;

    public override void _Ready()
    {
        _actuators = GetNode<Actuators>("Actuators");
        _senses = GetNode<Senses>("Senses");
        _plants = GetNodeOrNull<PlantManager>(PlantManagerPath);
        _water = GetNodeOrNull<WaterBodies>(WaterBodiesPath);
        _biomes = GetNodeOrNull<BiomeMap>(BiomeMapPath);
        _dayNight = GetNodeOrNull<DayNightCycle>(DayNightPath);
        _season = GetNodeOrNull<SeasonCycle>(SeasonPath);
        _sensoryField = GetNodeOrNull<SensoryField>(SensoryFieldPath);
        if (DisplayServer.GetName() == "headless")
        {
            // No rendering in training runs: drop the placeholder meshes entirely.
            GetNodeOrNull("BodyMesh")?.QueueFree();
            GetNodeOrNull("HeadMesh")?.QueueFree();
            return;
        }
        GetNodeOrNull<Node3D>("BodyMesh")?.Hide();
        GetNodeOrNull<Node3D>("HeadMesh")?.Hide();
        Visual = new CreatureVisual();
        AddChild(Visual);
        Visual.Build(this);
    }

    /// <summary>Ambient temperature fed to the physiology on the last step (read-only diagnostics).</summary>
    public float AmbientTemperatureCelsius { get; private set; }

    /// <summary>Presentation-only model; null in headless runs.</summary>
    public CreatureVisual? Visual { get; private set; }

    public override void _PhysicsProcess(double delta)
    {
        if (!State.IsAlive) return;
        var p = ProfileCounters.Start();
        if (_biomes is not null) _actuators.Apply(this, _biomes.Grid, State, _latch.Current, delta);
        ProfileCounters.Stop("move", p);
        var landing = _actuators.LastMove.LandingSpeed;
        if (landing > 12f)
            ApplyDamage(BodyZone.Legs, Mathf.Clamp((landing - 12f) / 30f, 0f, 1f), CoreDeathCause.Fall);
        p = ProfileCounters.Start();
        HandleDiscreteActions((float)delta);
        ProfileCounters.Stop("discrete", p);
        p = ProfileCounters.Start();
        StepPhysiology((float)delta);
        Fear = FearModel.Step(Fear, _senses.LastThreat, State.Pain, (float)delta);
        ProfileCounters.Stop("physiology", p);
        _latch.Tick();
    }

    public void ApplyAction(ReadOnlySpan<float> action) => _latch.Set(action);

    public float[] BuildObservation(float timeOfDay01, float visionMultiplier) =>
        _senses.Observe(this, timeOfDay01, visionMultiplier);

    public void ApplyDamage(BodyZone zone, float amount, CoreDeathCause lethalCause, uint sourceId = 0)
    {
        amount = Mathf.Max(0f, amount);
        if (amount <= 0f) return;
        var coreZone = zone switch
        {
            BodyZone.Head => CoreDamageZone.Head,
            BodyZone.Torso => CoreDamageZone.Torso,
            _ => CoreDamageZone.Legs
        };
        _pendingDamage.Add(new DamageEvent(coreZone, amount, lethalCause));
        DamageOccurred?.Invoke(CreatureId, zone, sourceId);
    }

    public void Die(CoreDeathCause cause)
    {
        if (!State.TryDie(cause)) return;
        OnDied(cause);
    }

    /// <summary>Seconds of simulated time a corpse stays visible before being removed.</summary>
    public const double CorpseLifetimeSeconds = 20.0;

    private void OnDied(CoreDeathCause cause)
    {
        EmitSignal("Died", CreatureId, (int)cause);
        DeathOccurred?.Invoke(CreatureId, cause);
        SetPhysicsProcess(false);
        // Carcasses are etapa 2: until then a corpse is not seen as a creature (senses only list
        // live creatures). Physics-driven timer, so it pauses during the lockstep wait.
        GetTree().CreateTimer(CorpseLifetimeSeconds, processAlways: false, processInPhysics: true)
            .Timeout += QueueFree;
    }

    private void HandleDiscreteActions(float delta)
    {
        var action = _latch.Current;
        // A sleeping creature does not bite or drink (sleep is a real trade-off, not a free discount).
        if (State.Sleeping) return;
        if (action[9] > 0f) _sensoryField?.EmitSound(CreatureId, GlobalPosition, action[9]);
        if (action[5] > 0.5f && _plants is not null)
        {
            var amount = _plants.ConsumeNearest(GlobalPosition, 2f, delta * 0.3f, out var thorny);
            _energyIntake += amount * 0.8f;
            if (amount > 0f) AteResource?.Invoke(CreatureId, 0, amount);
            if (thorny && amount > 0f) ApplyDamage(BodyZone.Head, amount * 0.08f, CoreDeathCause.HeadInjury);
        }
        var nearWater = _water?.IsNearDrinkableWater(GlobalPosition) == true;
        if (ProfileCounters.Enabled)
        {
            if (nearWater) DrinkStats[0]++;
            if (action[6] > 0.5f) DrinkStats[1]++;
        }
        if (action[6] > 0.5f && nearWater)
        {
            _waterIntake += delta * 0.35f;
            if (ProfileCounters.Enabled) DrinkStats[2]++;
        }
        _digging = action[10] > 0.5f && TryDig(delta);
        if (action[10] <= 0.5f) _dig.Reset();
    }

    /// <summary>
    /// Works on the first solid block along the gaze within reach (muzzle height, head pitch applies,
    /// so looking down digs the floor). Returns true while effort is being spent on a block.
    /// </summary>
    private bool TryDig(float delta)
    {
        if (_biomes is null) return false;
        var grid = _biomes.Grid;
        var pitch = HeadPitchRadians - 0.35f; // muzzle aims slightly below the eye line
        var forward = -GlobalTransform.Basis.Z;
        var direction = (forward * Mathf.Cos(pitch) + Vector3.Up * Mathf.Sin(pitch)).Normalized();
        var origin = GlobalPosition + Vector3.Up * (Shape.Height * 0.55f);
        var hit = VoxelRaycast.Cast(grid, origin.X, origin.Y, origin.Z, direction.X, direction.Y, direction.Z,
            Digging.ReachMeters, BlockTypes.IsSolid);
        if (!hit.Hit)
        {
            _dig.Reset();
            return false;
        }
        if (_dig.Work(hit.X, hit.Y, hit.Z, hit.Block, delta)) grid.Set(hit.X, hit.Y, hit.Z, BlockType.Air);
        return true;
    }

    private void StepPhysiology(float delta)
    {
        var biome = _biomes?.Sample(new Vector2(GlobalPosition.X, GlobalPosition.Z));
        var ambient = biome?.TemperatureCelsius ?? 20f;
        ambient += _dayNight?.TemperatureOffsetCelsius ?? 0f;
        ambient += _season?.TemperatureOffsetCelsius ?? 0f;
        AmbientTemperatureCelsius = ambient;
        var action = _latch.Current;
        // Digging and climbing cost like running at full effort.
        var working = _digging || _actuators.Climbing;
        var result = _physiology.Step(_coreState, _traits, new PhysiologyInput
        {
            AmbientTemperature = ambient,
            Thrust = working ? 1f : action[0],
            Gait = working ? 1f : _actuators.Gait,
            Turn = action[1],
            EnergyIntake = _energyIntake,
            WaterIntake = _waterIntake,
            Sleeping = action[7] > 0.5f && Velocity.LengthSquared() < 0.1f,
            WakeRequested = action[7] < 0f,
            DamageEvents = _pendingDamage.ToArray()
        }, delta);
        _energyIntake = 0;
        _waterIntake = 0;
        _pendingDamage.Clear();
        _coreState = result.State;
        State.Synchronize(_coreState);
        if (result.DiedThisStep) OnDied(_coreState.DeathCause);
    }
}
