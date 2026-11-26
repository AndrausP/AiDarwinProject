using Godot;

namespace CreatureIA.Godot.Creature;

/// <summary>
/// Presentation-only body: never created in headless runs and never read by senses or physics.
/// Faces -Z like the body. Legs swing with ground speed, the head follows the head-pitch action,
/// eyes close while sleeping and the body tips over grey when dead.
/// </summary>
public partial class CreatureVisual : Node3D
{
    private CreatureBody _body = null!;
    private Node3D _root = null!;
    private Node3D _headPivot = null!;
    private readonly Node3D[] _legs = new Node3D[4];
    private Node3D[] _pupils = System.Array.Empty<Node3D>();
    private Node3D[] _eyes = System.Array.Empty<Node3D>();
    private MeshInstance3D _ring = null!;
    private StandardMaterial3D _skin = null!;
    private StandardMaterial3D _belly = null!;
    private float _gaitPhase;
    private bool _deathApplied;

    public bool Selected
    {
        get => _ring.Visible;
        set => _ring.Visible = value;
    }

    public void Build(CreatureBody body)
    {
        _body = body;
        Name = "Visual";
        _skin = new StandardMaterial3D { AlbedoColor = body.BodyColor, Roughness = 0.75f };
        _belly = new StandardMaterial3D { AlbedoColor = body.BodyColor.Lightened(0.45f), Roughness = 0.85f };
        var dark = new StandardMaterial3D { AlbedoColor = new Color(0.08f, 0.08f, 0.1f), Roughness = 0.4f };
        var white = new StandardMaterial3D { AlbedoColor = new Color(0.97f, 0.97f, 0.95f), Roughness = 0.3f };

        _root = new Node3D { Name = "Root" };
        AddChild(_root);

        // Torso: stretched along -Z (forward), sitting on the legs.
        Add(_root, Sphere(0.5f, 0.85f), _skin, new Vector3(0f, 0.9f, 0f), new Vector3(1f, 0.85f, 1.45f));
        Add(_root, Sphere(0.42f, 0.6f), _belly, new Vector3(0f, 0.78f, -0.05f), new Vector3(0.9f, 0.7f, 1.3f));

        _headPivot = new Node3D { Name = "HeadPivot", Position = new Vector3(0f, 1.2f, -0.62f) };
        _root.AddChild(_headPivot);
        Add(_headPivot, Sphere(0.32f, 0.6f), _skin, new Vector3(0f, 0.08f, -0.18f), Vector3.One);
        Add(_headPivot, Sphere(0.16f, 0.22f), _belly, new Vector3(0f, -0.02f, -0.45f), new Vector3(1f, 0.8f, 1f)); // snout
        Add(_headPivot, Sphere(0.04f, 0.08f), dark, new Vector3(0f, 0.02f, -0.6f), Vector3.One); // nose
        // Ears.
        Add(_headPivot, Cone(0.1f, 0.22f), _skin, new Vector3(-0.17f, 0.36f, -0.12f), Vector3.One).RotationDegrees = new Vector3(0, 0, 18);
        Add(_headPivot, Cone(0.1f, 0.22f), _skin, new Vector3(0.17f, 0.36f, -0.12f), Vector3.One).RotationDegrees = new Vector3(0, 0, -18);

        _eyes = new Node3D[2];
        _pupils = new Node3D[2];
        for (var side = 0; side < 2; side++)
        {
            var x = side == 0 ? -0.13f : 0.13f;
            _eyes[side] = Add(_headPivot, Sphere(0.09f, 0.18f), white, new Vector3(x, 0.17f, -0.42f), Vector3.One);
            _pupils[side] = Add(_eyes[side], Sphere(0.05f, 0.1f), dark, new Vector3(0f, 0f, -0.06f), Vector3.One);
        }

        // Legs pivot at the hip so they can swing.
        var legOffsets = new[] { new Vector3(-0.28f, 0.62f, -0.42f), new Vector3(0.28f, 0.62f, -0.42f),
                                 new Vector3(-0.28f, 0.62f, 0.42f), new Vector3(0.28f, 0.62f, 0.42f) };
        for (var i = 0; i < 4; i++)
        {
            _legs[i] = new Node3D { Name = $"Leg{i}", Position = legOffsets[i] };
            _root.AddChild(_legs[i]);
            Add(_legs[i], Cylinder(0.09f, 0.07f, 0.62f), _skin, new Vector3(0f, -0.31f, 0f), Vector3.One);
            Add(_legs[i], Sphere(0.09f, 0.1f), dark, new Vector3(0f, -0.62f, -0.03f), new Vector3(1f, 0.6f, 1.4f)); // paw
        }

        var tail = Add(_root, Cylinder(0.07f, 0.02f, 0.7f), _skin, new Vector3(0f, 1.05f, 0.95f), Vector3.One);
        tail.RotationDegrees = new Vector3(-55f, 0f, 0f);

        _ring = new MeshInstance3D
        {
            Name = "SelectionRing",
            Mesh = new TorusMesh { InnerRadius = 0.95f, OuterRadius = 1.1f, Rings = 32, RingSegments = 6 },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(1f, 0.85f, 0.1f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
            },
            Position = new Vector3(0f, 0.05f, 0f),
            Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        AddChild(_ring);
    }

    public override void _Process(double delta)
    {
        if (_body is null || !IsInstanceValid(_body)) return;
        var state = _body.State;
        if (!state.IsAlive)
        {
            if (!_deathApplied) ApplyDeath();
            return;
        }

        var dt = (float)delta;
        var speed = new Vector2(_body.Velocity.X, _body.Velocity.Z).Length();
        _gaitPhase += dt * (2f + speed * 3.2f);
        var swing = Mathf.Clamp(speed / 6f, 0f, 1f) * 0.75f;
        for (var i = 0; i < 4; i++)
        {
            // Diagonal pairs move together (trot): 0 & 3, 1 & 2.
            var phase = i is 0 or 3 ? 0f : Mathf.Pi;
            _legs[i].Rotation = new Vector3(Mathf.Sin(_gaitPhase + phase) * swing, 0f, 0f);
        }
        _root.Position = new Vector3(0f, Mathf.Abs(Mathf.Sin(_gaitPhase)) * 0.06f * swing - (state.Sleeping ? 0.3f : 0f), 0f);
        _headPivot.Rotation = new Vector3(_body.HeadPitchRadians, 0f, 0f);

        var eyeOpen = state.Sleeping ? 0.15f : 1f;
        foreach (var eye in _eyes) eye.Scale = new Vector3(1f, eyeOpen, 1f);
        foreach (var pupil in _pupils) pupil.Visible = !state.Sleeping;
    }

    private void ApplyDeath()
    {
        _deathApplied = true;
        foreach (var leg in _legs) leg.Rotation = Vector3.Zero;
        _root.Rotation = new Vector3(0f, 0f, Mathf.Pi / 2f);
        _root.Position = new Vector3(0.75f, -0.35f, 0f);
        foreach (var eye in _eyes) eye.Scale = new Vector3(1f, 0.15f, 1f);
        foreach (var pupil in _pupils) pupil.Visible = false;
        var grey = new Color(0.45f, 0.45f, 0.45f);
        _skin.AlbedoColor = _skin.AlbedoColor.Lerp(grey, 0.7f);
        _belly.AlbedoColor = _belly.AlbedoColor.Lerp(grey, 0.7f);
    }

    private static MeshInstance3D Add(Node3D parent, Mesh mesh, Material material, Vector3 position, Vector3 scale)
    {
        var instance = new MeshInstance3D { Mesh = mesh, MaterialOverride = material, Position = position, Scale = scale };
        parent.AddChild(instance);
        return instance;
    }

    // Voxel look (Task 002): every body part is a box. The old round primitives keep their
    // footprint so the animation pivots still line up. Etapa 2 replaces this with the modular,
    // gene-driven voxel body.
    private static BoxMesh Sphere(float radius, float height) =>
        new() { Size = new Vector3(radius * 1.7f, height * 0.85f, radius * 1.7f) };

    private static BoxMesh Cylinder(float top, float bottom, float height) =>
        new() { Size = new Vector3((top + bottom) * 0.9f, height, (top + bottom) * 0.9f) };

    private static BoxMesh Cone(float radius, float height) =>
        new() { Size = new Vector3(radius * 1.2f, height, radius * 0.8f) };
}
