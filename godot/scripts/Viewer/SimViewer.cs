using System.Globalization;
using System.Text;
using CreatureIA.Core.Genetics;
using CreatureIA.Core.Simulation;
using CreatureIA.Core.Voxel;
using CreatureIA.Godot.World;
using Godot;

namespace CreatureIA.Godot.Viewer;

/// <summary>
/// T2.0d + T2.7b: Godot only draws the simulator (decision D4). Opens a world snapshot written by
/// CreatureIA.Sim and follows the run live by reloading the file whenever the Sim saves it again.
/// Nothing is re-simulated here. Tab opens the Observatory (species, genes, genealogy); N / P pick
/// a creature, 1 / 2 switch the inspector between Genes and Family; WASD + right mouse fly, wheel = speed.
///
///   CREATURE_IA_SIM_SNAPSHOT=benchmarks/runs/s2-a-sel/worlds/world-0.bin
/// </summary>
public partial class SimViewer : Node3D
{
    private string _path = "";
    private DateTime _loadedWrite;
    private SimWorld? _world;
    private VoxelGrid? _meshedGrid;
    private readonly List<MeshInstance3D> _chunks = new();
    private MultiMeshInstance3D _creatures = null!;
    private MultiMeshInstance3D _plants = null!;
    private MultiMeshInstance3D _carcasses = null!;
    private Camera3D _camera = null!;
    private Label _hud = null!;
    private Label _observatory = null!;
    private Label _inspector = null!;
    private MeshInstance3D _marker = null!;
    private int _selected = -1;
    private int _inspectorTab;
    private float _speed = 25f;
    private float _yaw, _pitch = -0.6f;
    private double _reloadTimer;
    private static readonly string? ScreenshotPath = System.Environment.GetEnvironmentVariable("CREATURE_IA_SCREENSHOT");
    private static readonly string? ScreenshotTab = System.Environment.GetEnvironmentVariable("CREATURE_IA_VIEWER_PANEL");
    private int _frames;

    public override void _Ready()
    {
        // Decimal points in every panel (the machine locale writes commas).
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        _path = System.Environment.GetEnvironmentVariable("CREATURE_IA_SIM_SNAPSHOT") ?? "";
        AddChild(new WorldEnvironment
        {
            Environment = new global::Godot.Environment
            {
                BackgroundMode = global::Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.25f, 0.42f, 0.64f),
                AmbientLightSource = global::Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.65f, 0.7f, 0.8f),
                AmbientLightEnergy = 0.75f,
            }
        });
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55, -30, 0), ShadowEnabled = true });
        _camera = new Camera3D { Far = 1600f, Position = new Vector3(0, 60, 80) };
        AddChild(_camera);
        _creatures = NewMultiMesh(new BoxMesh { Size = Vector3.One }, vertexColors: true);
        _plants = NewMultiMesh(new CylinderMesh { TopRadius = 0.3f, BottomRadius = 0.45f, Height = 1.4f }, vertexColors: true);
        _carcasses = NewMultiMesh(new BoxMesh { Size = new Vector3(0.9f, 0.4f, 0.9f) }, vertexColors: true);
        _marker = new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = 0.9f, OuterRadius = 1.2f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.9f, 0.1f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
            Visible = false,
        };
        AddChild(_marker);
        var overlay = new CanvasLayer();
        AddChild(overlay);
        _hud = NewLabel(overlay, new Vector2(16, 16), 15);
        _observatory = NewLabel(overlay, new Vector2(16, 120), 15);
        _observatory.Visible = ScreenshotTab == "observatory";
        _inspector = NewLabel(overlay, new Vector2(760, 16), 15);
        if (ScreenshotTab == "family") _inspectorTab = 1;
        Load();
        if (_world is not null && _world.Creatures.Count > 0) Select(0);
    }

    private MultiMeshInstance3D NewMultiMesh(Mesh mesh, bool vertexColors)
    {
        var instance = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = vertexColors, Mesh = mesh },
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.9f },
        };
        AddChild(instance);
        return instance;
    }

    private static Label NewLabel(CanvasLayer parent, Vector2 at, int size)
    {
        var label = new Label { Position = at };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", Colors.White);
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        label.AddThemeConstantOverride("outline_size", 6);
        parent.AddChild(label);
        return label;
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            _hud.Text = $"Sim viewer: snapshot não encontrado ({_path}). Defina CREATURE_IA_SIM_SNAPSHOT.";
            return;
        }
        try
        {
            var selectedId = _world is not null && _selected >= 0 && _selected < _world.Creatures.Count ? _world.Creatures[_selected].Id : (uint?)null;
            _world = SimWorld.LoadFile(_path);
            _loadedWrite = File.GetLastWriteTimeUtc(_path);
            if (_meshedGrid is null || _world.Terrain.MapSize != _meshedGrid.SizeX * _meshedGrid.BlockSize) BuildTerrain(_world.Terrain.Grid);
            else RefreshEdits(_world.Terrain.Grid);
            _selected = selectedId is { } id ? _world.Creatures.ToList().FindIndex(c => c.Id == id) : _selected;
            DrawLife();
        }
        catch (IOException)
        {
            // The Sim is writing the file right now (atomic rename): try again on the next poll.
        }
    }

    private void BuildTerrain(VoxelGrid grid)
    {
        foreach (var chunk in _chunks) chunk.QueueFree();
        _chunks.Clear();
        var solid = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.95f };
        var water = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true, AlbedoColor = new Color(1f, 1f, 1f, 0.72f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, Roughness = 0.15f, CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        for (var i = 0; i < grid.ChunkCount; i++)
        {
            if (grid.IsChunkEmpty(i)) continue;
            var (cx, cy, cz) = grid.ChunkCoordinates(i);
            var data = GreedyMesher.Build(grid, cx, cy, cz);
            if (data.Solid.IsEmpty && data.Water.IsEmpty) continue;
            var mesh = new ArrayMesh();
            TerrainGenerator.AddSurface(mesh, data.Solid, solid);
            TerrainGenerator.AddSurface(mesh, data.Water, water);
            var instance = new MeshInstance3D { Mesh = mesh };
            AddChild(instance);
            _chunks.Add(instance);
        }
        grid.TakeDirtyChunks();
        _meshedGrid = grid;
    }

    /// <summary>Dug blocks arrive with each snapshot; rebuilding the whole terrain each time is cheap enough for a viewer.</summary>
    private void RefreshEdits(VoxelGrid grid) => BuildTerrain(grid);

    private void DrawLife()
    {
        if (_world is null) return;
        var creatures = _world.Creatures;
        var mm = _creatures.Multimesh;
        mm.InstanceCount = creatures.Count;
        for (var i = 0; i < creatures.Count; i++)
        {
            var c = creatures[i];
            var shape = c.BodyShape;
            var basis = new Basis(Vector3.Up, c.Yaw).Scaled(new Vector3(shape.HalfWidth * 2f, shape.Height, shape.HalfWidth * 2.6f));
            mm.SetInstanceTransform(i, new Transform3D(basis, new Vector3(c.Position.X, c.Position.Y + shape.Height * 0.5f, c.Position.Z)));
            var (r, g, b) = c.Color;
            var tint = c.IsCarnivore ? new Color(r * 0.6f + 0.4f, g * 0.5f, b * 0.5f) : new Color(r, g, b);
            mm.SetInstanceColor(i, c.Sleeping ? tint.Darkened(0.5f) : tint);
        }
        var plants = _world.Plants.Plants.Where(p => p.Biomass > 0.05f).ToList();
        var pm = _plants.Multimesh;
        pm.InstanceCount = plants.Count;
        for (var i = 0; i < plants.Count; i++)
        {
            var p = plants[i];
            var s = Mathf.Clamp(0.4f + p.Biomass, 0.4f, 1.4f);
            pm.SetInstanceTransform(i, new Transform3D(Basis.Identity.Scaled(new Vector3(s, s, s)), new Vector3(p.Position.X, p.Position.Y + 0.7f * s, p.Position.Z)));
            pm.SetInstanceColor(i, p.Thorny ? new Color(0.45f, 0.5f, 0.15f) : new Color(0.2f, 0.75f, 0.25f));
        }
        var carcasses = _world.Carcasses;
        var cm = _carcasses.Multimesh;
        cm.InstanceCount = carcasses.Count;
        for (var i = 0; i < carcasses.Count; i++)
        {
            var c = carcasses[i];
            cm.SetInstanceTransform(i, new Transform3D(Basis.Identity, new Vector3(c.Position.X, c.Position.Y + 0.2f, c.Position.Z)));
            cm.SetInstanceColor(i, new Color(0.55f, 0.08f, 0.08f));
        }
        UpdatePanels();
    }

    private void Select(int index)
    {
        if (_world is null || _world.Creatures.Count == 0) return;
        _selected = ((index % _world.Creatures.Count) + _world.Creatures.Count) % _world.Creatures.Count;
        var c = _world.Creatures[_selected];
        _camera.Position = new Vector3(c.Position.X, c.Position.Y + 14f, c.Position.Z + 18f);
        _camera.LookAt(new Vector3(c.Position.X, c.Position.Y, c.Position.Z));
        _yaw = _camera.Rotation.Y;
        _pitch = _camera.Rotation.X;
        UpdatePanels();
    }

    private void UpdatePanels()
    {
        if (_world is null) return;
        var w = _world;
        var clock = w.Clock;
        var day = (int)(clock.ElapsedSeconds / SimClock.DayDurationSeconds);
        var year = clock.ElapsedSeconds / SimClock.YearDurationSeconds;
        _hud.Text = (
            $"Sim viewer (só desenha o Sim, D4) — {Path.GetFileName(_path)}\n" +
            $"Dia {day + 1} · ano {year:F2} · hora {clock.TimeOfDay01 * 24:F1} h · vivas {w.Creatures.Count} · carcaças {w.Carcasses.Count}" +
            (w.Terrain.Drought ? " · SECA" : "") + "\n" +
            $"Nascimentos {w.Population.Births} · resgates {w.Population.Rescues} · geração máx. {w.Population.MaxGeneration}\n" +
            "Tab observatório · N/P criatura · 1 Genes / 2 Família · WASD + botão direito voa");
        _observatory.Text = ObservatoryText(w);
        _inspector.Text = InspectorText(w);
        if (_selected >= 0 && _selected < w.Creatures.Count)
        {
            var c = w.Creatures[_selected];
            _marker.Visible = true;
            _marker.Position = new Vector3(c.Position.X, c.Position.Y + c.BodyShape.Height + 0.6f, c.Position.Z);
        }
        else _marker.Visible = false;
    }

    private static readonly string[] WatchedGenes = { "size", "carnivory", "longevity_years", "maturity_years", "litter_size", "m_libido", "m_care", "smell", "fur" };

    private static string ObservatoryText(SimWorld w)
    {
        var sb = new StringBuilder("OBSERVATÓRIO\n\nEspécies (linhagem fundadora)\n");
        foreach (var group in w.Creatures.GroupBy(c => c.Lineage).OrderBy(g => g.Key))
        {
            var list = group.ToList();
            sb.Append(CultureInfo.InvariantCulture,
                $"  {group.Key}: {list.Count} vivas · ♀ {list.Count(c => c.IsFemale)} · adultos {list.Count(c => c.IsMature)} · grávidas {list.Count(c => c.IsPregnant)} · geração máx. {list.Max(c => c.Generation)}\n");
        }
        sb.Append("\nGenes (média dos vivos por linhagem)\n");
        foreach (var gene in WatchedGenes)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {gene,-16}");
            foreach (var group in w.Creatures.GroupBy(c => c.Lineage).OrderBy(g => g.Key))
                sb.Append(CultureInfo.InvariantCulture, $" {group.Key} {group.Average(c => c.Genome.Phenotype(gene)):F2}");
            sb.Append('\n');
        }
        sb.Append("\nGenealogia\n");
        var byGeneration = w.Creatures.GroupBy(c => c.Generation).OrderBy(g => g.Key);
        sb.Append("  vivas por geração: " + string.Join(" · ", byGeneration.Select(g => $"G{g.Key} {g.Count()}")) + "\n");
        var p = w.Population;
        sb.Append(CultureInfo.InvariantCulture, $"  concepções {p.Conceptions} · inviáveis {p.Inviable} · mortes {p.Deaths}\n");
        return sb.ToString();
    }

    private string InspectorText(SimWorld w)
    {
        if (_selected < 0 || _selected >= w.Creatures.Count) return "";
        var c = w.Creatures[_selected];
        var s = c.Physiology;
        var sb = new StringBuilder();
        sb.Append(
            $"Criatura {c.Id} · linhagem {c.Lineage} · {(c.IsFemale ? "fêmea" : "macho")} · G{c.Generation}\n" +
            $"idade {c.AgeSeconds / LifeCycle.SecondsPerYear:F2} anos · {(c.IsMature ? "adulta" : "filhote")}" + (c.IsPregnant ? $" · grávida {c.GestationProgress:P0}" : "") + "\n" +
            $"energia {s.Energy:F2} · água {s.Water:F2} · fadiga {s.Fatigue:F2} · libido {s.Libido:F2} · medo {c.Fear:F2}\n\n");
        if (_inspectorTab == 0)
        {
            sb.Append("[1 Genes]  2 Família\n");
            var genome = c.Genome;
            for (var i = 0; i < genome.Catalog.Count; i++)
            {
                var g = genome.Catalog.Genes[i];
                sb.Append(CultureInfo.InvariantCulture, $"  {g.Name,-16} {genome.Phenotype(i),7:F3}   (♀ {genome.Maternal[i].Value:F2} / ♂ {genome.Paternal[i].Value:F2})\n");
            }
        }
        else
        {
            sb.Append(" 1 Genes  [2 Família]\n");
            var mother = w.Creatures.FirstOrDefault(x => x.Id == c.MotherId);
            var father = w.Creatures.FirstOrDefault(x => x.Id == c.FatherId);
            sb.Append($"  mãe   {(c.MotherId == 0 ? "fundadora" : c.MotherId + (mother is null ? " (morta)" : " (viva)"))}\n");
            sb.Append($"  pai   {(c.FatherId == 0 ? "fundador" : c.FatherId + (father is null ? " (morto)" : " (vivo)"))}\n");
            var young = w.Creatures.Where(x => c.IsParentOf(x)).ToList();
            sb.Append($"  filhos vivos {young.Count}: {string.Join(", ", young.Take(12).Select(x => x.Id))}\n");
            var siblings = w.Creatures.Where(x => x.Id != c.Id && c.MotherId != 0 && x.MotherId == c.MotherId).ToList();
            sb.Append($"  irmãos vivos {siblings.Count}\n");
        }
        return sb.ToString();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true } key)
        {
            switch (key.Keycode)
            {
                case Key.Tab: _observatory.Visible = !_observatory.Visible; break;
                case Key.N: Select(_selected + 1); break;
                case Key.P: Select(_selected - 1); break;
                case Key.Key1: _inspectorTab = 0; UpdatePanels(); break;
                case Key.Key2: _inspectorTab = 1; UpdatePanels(); break;
                case Key.R: Load(); break;
            }
        }
        if (@event is InputEventMouseMotion motion && Input.IsMouseButtonPressed(MouseButton.Right))
        {
            _yaw -= motion.Relative.X * 0.004f;
            _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * 0.004f, -1.5f, 1.5f);
            _camera.Rotation = new Vector3(_pitch, _yaw, 0f);
        }
        if (@event is InputEventMouseButton { Pressed: true } wheel)
        {
            if (wheel.ButtonIndex == MouseButton.WheelUp) _speed = Mathf.Min(300f, _speed * 1.25f);
            if (wheel.ButtonIndex == MouseButton.WheelDown) _speed = Mathf.Max(2f, _speed / 1.25f);
        }
    }

    public override void _Process(double delta)
    {
        var move = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W)) move -= _camera.GlobalTransform.Basis.Z;
        if (Input.IsKeyPressed(Key.S)) move += _camera.GlobalTransform.Basis.Z;
        if (Input.IsKeyPressed(Key.A)) move -= _camera.GlobalTransform.Basis.X;
        if (Input.IsKeyPressed(Key.D)) move += _camera.GlobalTransform.Basis.X;
        if (Input.IsKeyPressed(Key.E)) move += Vector3.Up;
        if (Input.IsKeyPressed(Key.Q)) move -= Vector3.Up;
        _camera.Position += move * _speed * (float)delta;

        // Live: the Sim rewrites the snapshot atomically every --save-every ticks; follow it.
        _reloadTimer += delta;
        if (_reloadTimer > 2.0)
        {
            _reloadTimer = 0;
            if (File.Exists(_path) && File.GetLastWriteTimeUtc(_path) != _loadedWrite) Load();
        }

        if (string.IsNullOrEmpty(ScreenshotPath)) return;
        if (++_frames != 90) return;
        GetViewport().GetTexture().GetImage().SavePng(ScreenshotPath);
        GD.Print($"[screenshot] {ScreenshotPath}");
        GetTree().Quit();
    }
}
