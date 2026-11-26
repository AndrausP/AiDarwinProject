using CreatureIA.Godot.Creature;
using Godot;

namespace CreatureIA.Godot.World;

public partial class WorldRoot : Node3D
{
    [Export] public int Population { get; set; } = 48;
    [Export] public int WorldSeed { get; set; } = 1337;
    [Export] public PackedScene CreatureScene { get; set; } = null!;
    [Export] public NodePath CreaturesPath { get; set; } = "Creatures";
    /// <summary>Half-size of the square where creatures are born (voxel map is 384 m; Task 001 used ±80 m of 256 m).</summary>
    [Export] public float SpawnExtentMeters { get; set; } = 120f;

    public override void _Ready()
    {
        var requestedScale = System.Environment.GetEnvironmentVariable("CREATURE_IA_TIME_SCALE");
        if (float.TryParse(requestedScale, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var timeScale) && timeScale > 0f)
            ApplyTimeScale(timeScale);
        if (DisplayServer.GetName() != "headless")
        {
            // Presentation-only layer; absent from training runs. (Trees are voxel blocks now, so the
            // decorative Scenery of Task 001 is gone.)
            AddChild(new Atmosphere());
        }
        _container = GetNode<Node3D>(CreaturesPath);
        _biomes = GetNode<BiomeMap>("BiomeMap");
        _rng = new RandomNumberGenerator { Seed = (ulong)RunSettings.Seed(WorldSeed) };
        if (RunSettings.Showcase)
        {
            // Few animals, kept on the open floor in front of the exhibits.
            Population = 10;
            SpawnExtentMeters = _biomes.MapSize / 2f - 6f;
        }
        for (var i = 0; i < Population; i++) SpawnCreature((uint)(i + 1), null);
    }

    private static readonly string? ScreenshotPath = System.Environment.GetEnvironmentVariable("CREATURE_IA_SCREENSHOT");
    private int _frames;

    /// <summary>Visual check: with CREATURE_IA_SCREENSHOT=path.png, save one frame after warm-up and quit.</summary>
    public override void _Process(double delta)
    {
        if (string.IsNullOrEmpty(ScreenshotPath) || DisplayServer.GetName() == "headless") return;
        if (++_frames != 90) return;
        GetViewport().GetTexture().GetImage().SavePng(ScreenshotPath);
        GD.Print($"[screenshot] {ScreenshotPath}");
        GetTree().Quit();
    }

    private Node3D _container = null!;
    private BiomeMap _biomes = null!;
    private RandomNumberGenerator _rng = null!;
    private readonly HashSet<uint> _usedIds = new();

    /// <summary>
    /// Creates a creature with a never-used id. Without an explicit position it is placed by the
    /// seeded world RNG, so a run with the same seed and the same SPAWN sequence is reproducible.
    /// Returns null (and logs) when the id was already used.
    /// </summary>
    public CreatureBody? SpawnCreature(uint id, Vector3? position)
    {
        if (!_usedIds.Add(id))
        {
            GD.PushError($"SPAWN rejected: creature id {id} was already used.");
            return null;
        }
        var creature = CreatureScene.Instantiate<CreatureBody>();
        creature.Name = $"Creature{id:000}";
        creature.CreatureId = id;
        creature.BodyColor = Color.FromHsv(_rng.Randf(), 0.55f, 0.9f);
        // Spawn on dry land within the inner part of the map (lakes and tree tops excluded).
        creature.Position = position ?? _biomes.RandomLandPoint(_rng, SpawnExtentMeters) + Vector3.Up * 0.05f;
        _container.AddChild(creature);
        return creature;
    }

    /// <summary>
    /// Godot 4's Engine.TimeScale only multiplies each step's delta; it does not run more steps.
    /// Scaling the tick rate by the same factor keeps the physics delta at exactly 1/60 s of
    /// simulated time (the physiology contract) while actually running <paramref name="scale"/>× more steps.
    /// </summary>
    private static void ApplyTimeScale(float scale)
    {
        const int simulatedPhysicsHz = 60;
        var ticks = Mathf.Max(1, Mathf.RoundToInt(simulatedPhysicsHz * scale));
        Engine.PhysicsTicksPerSecond = ticks;
        Engine.TimeScale = ticks / (double)simulatedPhysicsHz;
        Engine.MaxPhysicsStepsPerFrame = Mathf.Max(8, ticks / 15);
    }
}
