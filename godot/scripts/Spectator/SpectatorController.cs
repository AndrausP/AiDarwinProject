using CreatureIA.Godot.Creature;
using CreatureIA.Godot.Net;
using CreatureIA.Godot.World;
using Godot;

namespace CreatureIA.Godot.Spectator;

/// <summary>
/// Free/follow camera, click-to-inspect and overlays. Read-only with respect to the simulation
/// (it only toggles debug-ray capture on the inspected creature). Removed in headless runs.
/// </summary>
public partial class SpectatorController : Node3D
{
    [Export] public NodePath CreaturesPath { get; set; } = "../Creatures";
    [Export] public NodePath CoordinatorPath { get; set; } = "../SimulationCoordinator";
    [Export] public NodePath BridgePath { get; set; } = "../BrainBridge";
    [Export] public NodePath DayNightPath { get; set; } = "../DayNightCycle";
    [Export] public NodePath SeasonPath { get; set; } = "../SeasonCycle";
    [Export] public float CameraSpeed { get; set; } = 20f;

    private const uint CreatureLayer = 1u << 3;
    private const uint AllWorldLayers = 0b1_1111;

    private Camera3D _camera = null!;
    private Label _telemetry = null!;
    private Node _creatures = null!;
    private SimulationCoordinator _coordinator = null!;
    private BrainBridge _bridge = null!;
    private DayNightCycle _dayNight = null!;
    private SeasonCycle _season = null!;
    private CreatureInspector _inspector = null!;
    private ImmediateMesh _rayMesh = null!;
    private StandardMaterial3D _rayMaterial = null!;

    private CreatureBody? _selected;
    private bool _following;
    private float _yaw;
    private float _pitch = Mathf.DegToRad(-35f);
    private float _followDistance = 7f;

    public override void _Ready()
    {
        if (DisplayServer.GetName() == "headless") { QueueFree(); return; }
        _camera = GetNode<Camera3D>("Camera3D");
        _telemetry = GetNode<Label>("Overlay/Telemetry");
        _creatures = GetNode(CreaturesPath);
        _coordinator = GetNode<SimulationCoordinator>(CoordinatorPath);
        _bridge = GetNode<BrainBridge>(BridgePath);
        _dayNight = GetNode<DayNightCycle>(DayNightPath);
        _season = GetNode<SeasonCycle>(SeasonPath);
        // CREATURE_IA_CAMERA="x,y,z,pitchDegrees" places the free camera (overview screenshots).
        var cameraSpec = System.Environment.GetEnvironmentVariable("CREATURE_IA_CAMERA")?.Split(',');
        if (cameraSpec is { Length: 4 } && cameraSpec.All(v => float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)))
        {
            var v = cameraSpec.Select(c => float.Parse(c, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            _camera.Position = new Vector3(v[0], v[1], v[2]);
            _pitch = Mathf.DegToRad(v[3]);
        }
        _camera.Rotation = new Vector3(_pitch, _yaw, 0f);
        _camera.Far = 1600f;

        _telemetry.AddThemeStyleboxOverride("normal", new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.07f, 0.1f, 0.75f),
            ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 6, ContentMarginBottom = 6,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6
        });
        _telemetry.AddThemeFontSizeOverride("font_size", 14);
        _telemetry.OffsetRight = 560f;

        _inspector = new CreatureInspector();
        GetNode("Overlay").AddChild(_inspector);

        _rayMesh = new ImmediateMesh();
        _rayMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true
        };
        AddChild(new MeshInstance3D
        {
            Name = "VisibleSensorRays",
            Mesh = _rayMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        });
    }

    public override void _Process(double delta)
    {
        if (!IsInstanceValid(_telemetry)) return;
        var dt = (float)delta;
        if (_following && _selected is not null && IsInstanceValid(_selected))
        {
            var forward = -_selected.GlobalTransform.Basis.Z;
            var target = _selected.GlobalPosition - forward * _followDistance + Vector3.Up * (_followDistance * 0.5f + 1f);
            _camera.GlobalPosition = _camera.GlobalPosition.Lerp(target, dt * 3f);
            _camera.LookAt(_selected.GlobalPosition + Vector3.Up);
        }
        else MoveFreeCamera(dt);
        AutoInspect();
        DrawSensorRays();
        UpdateTelemetry();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!IsInstanceValid(_camera)) return;
        switch (@event)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click:
                if (PickCreature(click.Position) is { } picked) Select(picked, follow: _following);
                break;
            case InputEventMouseMotion motion when Input.IsMouseButtonPressed(MouseButton.Right):
                if (_following) StopFollowing();
                _yaw -= motion.Relative.X * 0.005f;
                _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * 0.005f, Mathf.DegToRad(-89f), Mathf.DegToRad(30f));
                _camera.Rotation = new Vector3(_pitch, _yaw, 0f);
                break;
            case InputEventMouseButton { Pressed: true } wheel
                when wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown:
                var zoomIn = wheel.ButtonIndex == MouseButton.WheelUp;
                if (_following) _followDistance = Mathf.Clamp(_followDistance * (zoomIn ? 0.85f : 1.18f), 2f, 60f);
                else _camera.Position += -_camera.Basis.Z * (zoomIn ? 4f : -4f) * SpeedMultiplier();
                break;
            case InputEventKey { Pressed: true, Echo: false } key:
                switch (key.Keycode)
                {
                    case Key.Space: SelectNext(); break;
                    case Key.F when _selected is not null: _following = !_following; if (!_following) StopFollowing(); break;
                    case Key.Escape: Deselect(); break;
                }
                break;
        }
    }

    /// <summary>
    /// Ray from the mouse; a direct hit on a creature capsule wins, otherwise the creature
    /// nearest to the ground hit point (within 2.5 m) is chosen, so the wider visual body is clickable.
    /// </summary>
    private CreatureBody? PickCreature(Vector2 screenPosition)
    {
        var from = _camera.ProjectRayOrigin(screenPosition);
        var direction = _camera.ProjectRayNormal(screenPosition);
        var live = _creatures.GetChildren().OfType<CreatureBody>().Where(c => c.State.IsAlive).ToArray();
        CreatureBody? direct = null;
        var best = float.PositiveInfinity;
        foreach (var c in live)
        {
            var b = c.GlobalPosition;
            var t = CreatureIA.Core.Voxel.VisionGeometry.RayVerticalCylinder(from.X, from.Y, from.Z, direction.X, direction.Y, direction.Z,
                b.X, b.Y, b.Z, 0.7f, CreatureBody.Shape.Height, 800f);
            if (t is { } d && d < best) { best = d; direct = c; }
        }
        var grid = GetNodeOrNull<BiomeMap>("../BiomeMap")?.Grid;
        if (grid is null) return direct;
        var ground = CreatureIA.Core.Voxel.VoxelRaycast.Cast(grid, from.X, from.Y, from.Z, direction.X, direction.Y, direction.Z, 800f,
            CreatureIA.Core.Voxel.BlockTypes.IsOpaqueToVision);
        if (direct is not null && (!ground.Hit || best <= ground.Distance)) return direct;
        if (!ground.Hit) return null;
        var point = from + direction * ground.Distance;
        return live
            .Select(c => (Creature: c, Distance: c.GlobalPosition.DistanceTo(point)))
            .Where(pair => pair.Distance < 2.5f)
            .OrderBy(pair => pair.Distance)
            .Select(pair => pair.Creature)
            .FirstOrDefault();
    }
    private void Select(CreatureBody creature, bool follow)
    {
        if (_selected is not null && IsInstanceValid(_selected))
        {
            _selected.SensorSuite.DebugRays = false;
            if (_selected.Visual is not null) _selected.Visual.Selected = false;
        }
        _selected = creature;
        _selected.SensorSuite.DebugRays = true;
        if (_selected.Visual is not null) _selected.Visual.Selected = true;
        _inspector.Creature = creature;
        _following = follow;
    }

    private static readonly string? AutoInspectId = System.Environment.GetEnvironmentVariable("CREATURE_IA_INSPECT");
    private static readonly int AutoInspectTab =
        int.TryParse(System.Environment.GetEnvironmentVariable("CREATURE_IA_INSPECT_TAB"), out var tab) ? tab : 0;
    private bool _autoInspected;

    /// <summary>Screenshots/demos: CREATURE_IA_INSPECT=id selects and follows that creature once it exists.</summary>
    private void AutoInspect()
    {
        if (_autoInspected || !uint.TryParse(AutoInspectId, out var id)) return;
        var creature = _creatures.GetChildren().OfType<CreatureBody>().FirstOrDefault(c => c.CreatureId == id && c.State.IsAlive);
        if (creature is null) return;
        Select(creature, follow: true);
        _inspector.CurrentTab = AutoInspectTab;
        _autoInspected = true;
    }

    private void SelectNext()
    {
        var live = _creatures.GetChildren().OfType<CreatureBody>().Where(c => c.State.IsAlive).ToArray();
        if (live.Length == 0) return;
        var current = _selected is null ? -1 : Array.IndexOf(live, _selected);
        Select(live[(current + 1) % live.Length], follow: true);
    }

    private void Deselect()
    {
        if (_selected is not null && IsInstanceValid(_selected))
        {
            _selected.SensorSuite.DebugRays = false;
            if (_selected.Visual is not null) _selected.Visual.Selected = false;
        }
        _selected = null;
        _inspector.Creature = null;
        StopFollowing();
    }

    private void StopFollowing()
    {
        _following = false;
        var rotation = _camera.GlobalRotation;
        (_pitch, _yaw) = (rotation.X, rotation.Y);
        _camera.Rotation = new Vector3(_pitch, _yaw, 0f);
    }

    private static float SpeedMultiplier() => Input.IsKeyPressed(Key.Shift) ? 4f : 1f;

    private void MoveFreeCamera(float delta)
    {
        var right = (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right) ? 1f : 0f)
                  - (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left) ? 1f : 0f);
        var forward = (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up) ? 1f : 0f)
                    - (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down) ? 1f : 0f);
        var up = (Input.IsKeyPressed(Key.E) ? 1f : 0f) - (Input.IsKeyPressed(Key.Q) ? 1f : 0f);
        var flatForward = new Vector3(-Mathf.Sin(_yaw), 0f, -Mathf.Cos(_yaw));
        var flatRight = new Vector3(Mathf.Cos(_yaw), 0f, -Mathf.Sin(_yaw));
        _camera.Position += (flatRight * right + flatForward * forward + Vector3.Up * up)
                            * CameraSpeed * SpeedMultiplier() * delta;
    }

    private void UpdateTelemetry()
    {
        var all = _creatures.GetChildren().OfType<CreatureBody>().ToArray();
        var alive = all.Count(c => c.State.IsAlive);
        var hours = _dayNight.TimeOfDay01 * 24f;
        var season = _season.WinterStrength switch
        {
            > 0.75f => "inverno",
            < 0.25f => "verão",
            _ => _season.YearProgress01 < 0.5f ? "primavera" : "outono"
        };
        // Day count from simulated brain ticks (10 Hz) and the 07:12 start of the clock.
        var day = 1 + (int)((_coordinator.BrainTick * 0.1 + 0.3 * DayNightCycle.DayDurationSeconds) / DayNightCycle.DayDurationSeconds);
        _telemetry.Text =
            $"dia {day}   {(int)hours:00}:{(int)(hours % 1f * 60f):00}   {season}   vivas {alive}/{all.Length}   " +
            $"{(_bridge.Connected ? "cérebro: Python" : "cérebro: aleatório local")}\n" +
            $"tick {_coordinator.BrainTick}   RTT {_bridge.LastRoundTripMilliseconds:F1} ms   falhas {_coordinator.FailedTicks}\n" +
            "clique: inspecionar  ·  Espaço: próxima  ·  F: seguir  ·  Esc: soltar\n" +
            "WASD/setas mover  ·  Q/E descer/subir  ·  roda zoom  ·  botão direito girar  ·  Shift rápido";
    }

    private void DrawSensorRays()
    {
        _rayMesh.ClearSurfaces();
        // Dead creatures stop observing, so their segment list is empty; an empty surface is an engine error.
        if (_selected is null || !IsInstanceValid(_selected) || _selected.SensorSuite.DebugSegments.Count == 0) return;
        _rayMesh.SurfaceBegin(Mesh.PrimitiveType.Lines, _rayMaterial);
        foreach (var (from, to, hit) in _selected.SensorSuite.DebugSegments)
        {
            _rayMesh.SurfaceSetColor(hit ? Colors.Red : new Color(1f, 1f, 0.3f, 0.6f));
            _rayMesh.SurfaceAddVertex(from);
            _rayMesh.SurfaceAddVertex(to);
        }
        _rayMesh.SurfaceEnd();
    }
}
