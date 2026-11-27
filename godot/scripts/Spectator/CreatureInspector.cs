using CreatureIA.Core.Physiology;
using CreatureIA.Godot.Creature;
using Godot;

namespace CreatureIA.Godot.Spectator;

/// <summary>
/// Read-only data panel for the selected creature. It only reads state; it never writes to the
/// simulation, so inspecting a creature cannot change its behaviour.
/// </summary>
public partial class CreatureInspector : PanelContainer
{
    // Protocol v2 action order (contracts/protocol-v2.json).
    private static readonly string[] ActionNames =
        { "empuxo", "giro", "marcha", "cabeça", "pular", "morder", "beber", "dormir", "acasalar", "som", "cavar", "escalar" };

    private Label _title = null!;
    private ColorRect _swatch = null!;
    private Label _status = null!;
    private Label _details = null!;
    private Label _mind = null!;
    private TabContainer _tabs = null!;
    private readonly Dictionary<string, (ProgressBar Bar, Label Value, StyleBoxFlat Fill)> _bars = new();
    private readonly (ProgressBar Bar, Label Value, StyleBoxFlat Fill)[] _actions = new (ProgressBar, Label, StyleBoxFlat)[12];

    public CreatureBody? Creature { get; set; }

    /// <summary>0 = Estado, 1 = Mente.</summary>
    public int CurrentTab
    {
        get => _tabs.CurrentTab;
        set => _tabs.CurrentTab = Mathf.Clamp(value, 0, _tabs.GetTabCount() - 1);
    }

    public override void _Ready()
    {
        Name = "CreatureInspector";
        AnchorLeft = 1f;
        AnchorRight = 1f;
        OffsetLeft = -340f;
        OffsetRight = -12f;
        OffsetTop = 12f;
        AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.07f, 0.1f, 0.85f),
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 10, ContentMarginBottom = 10
        });

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 3);
        AddChild(column);

        var header = new HBoxContainer();
        _swatch = new ColorRect { CustomMinimumSize = new Vector2(18, 18) };
        _title = new Label();
        _title.AddThemeFontSizeOverride("font_size", 20);
        header.AddChild(_swatch);
        header.AddChild(_title);
        column.AddChild(header);

        _status = new Label();
        column.AddChild(_status);

        _tabs = new TabContainer();
        column.AddChild(_tabs);

        var state = new VBoxContainer { Name = "Estado" };
        state.AddThemeConstantOverride("separation", 3);
        _tabs.AddChild(state);
        state.AddChild(Section("Fisiologia"));
        foreach (var name in new[] { "Energia", "Água", "Temp. corpo", "Cabeça", "Torso", "Pernas", "Fadiga", "Dor", "Libido" })
            _bars[name] = AddBar(state, name, 0f, 1f);
        state.AddChild(Section("Corpo e ambiente"));
        _details = new Label();
        _details.AddThemeFontSizeOverride("font_size", 13);
        state.AddChild(_details);

        var mind = new VBoxContainer { Name = "Mente" };
        mind.AddThemeConstantOverride("separation", 3);
        _tabs.AddChild(mind);
        mind.AddChild(Section("Sentimentos"));
        _bars["Medo"] = AddBar(mind, "Medo", 0f, 1f);
        _bars["Ameaça"] = AddBar(mind, "Ameaça", 0f, 1f);
        _mind = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(290, 0) };
        _mind.AddThemeFontSizeOverride("font_size", 13);
        mind.AddChild(_mind);
        mind.AddChild(Section("Ação atual (cérebro → corpo)"));
        for (var i = 0; i < ActionNames.Length; i++) _actions[i] = AddBar(mind, ActionNames[i], -1f, 1f);

        var hint = new Label { Text = "F seguir · Esc fechar · abas Estado/Mente" };
        hint.AddThemeColorOverride("font_color", new Color(0.65f, 0.7f, 0.75f));
        hint.AddThemeFontSizeOverride("font_size", 12);
        column.AddChild(hint);
        Visible = false;
    }

    public override void _Process(double delta)
    {
        if (Creature is null || !IsInstanceValid(Creature))
        {
            Visible = false;
            return;
        }
        Visible = true;
        var creature = Creature;
        var s = creature.State;
        _title.Text = $"  Criatura #{creature.CreatureId}";
        _swatch.Color = creature.BodyColor;
        _status.Text = s.IsAlive
            ? (s.Sleeping ? "● viva — dormindo" : "● viva")
            : $"✖ morta — {CauseName(s.CauseOfDeath)}";
        _status.AddThemeColorOverride("font_color", s.IsAlive ? new Color(0.5f, 0.95f, 0.55f) : new Color(1f, 0.45f, 0.4f));

        SetBar("Energia", s.Energy, $"{s.Energy:P0}", Need(s.Energy));
        SetBar("Água", s.Water, $"{s.Water:P0}", Need(s.Water));
        var temperatureOk = s.BodyTemperatureCelsius is >= 30f and <= 43f;
        SetBar("Temp. corpo", Mathf.InverseLerp(25f, 45f, s.BodyTemperatureCelsius), $"{s.BodyTemperatureCelsius:F1} °C",
            temperatureOk ? new Color(0.95f, 0.65f, 0.2f) : new Color(0.3f, 0.6f, 1f));
        SetBar("Cabeça", s.HeadIntegrity, $"{s.HeadIntegrity:P0}", Need(s.HeadIntegrity));
        SetBar("Torso", s.TorsoIntegrity, $"{s.TorsoIntegrity:P0}", Need(s.TorsoIntegrity));
        SetBar("Pernas", s.LegsIntegrity, $"{s.LegsIntegrity:P0}", Need(s.LegsIntegrity));
        SetBar("Fadiga", s.Fatigue, $"{s.Fatigue:P0}", new Color(0.7f, 0.55f, 0.95f));
        SetBar("Dor", s.Pain, $"{s.Pain:P0}", new Color(1f, 0.4f, 0.4f));
        SetBar("Libido", s.Libido, $"{s.Libido:P0}", new Color(1f, 0.55f, 0.8f));

        var velocity = creature.Velocity;
        var position = creature.GlobalPosition;
        _details.Text =
            $"idade        {FormatTime(s.AgeSeconds)}\n" +
            $"velocidade   {new Vector2(velocity.X, velocity.Z).Length():F2} m/s\n" +
            $"posição      ({position.X:F0}, {position.Y:F1}, {position.Z:F0})\n" +
            $"temp. ambiente {creature.AmbientTemperatureCelsius:F1} °C  (vital 30–43 °C)";

        SetBar("Medo", creature.Fear, $"{creature.Fear:P0}", new Color(0.85f, 0.4f, 1f));
        var threat = creature.SensorSuite.LastThreat;
        SetBar("Ameaça", threat, $"{threat:P0}", new Color(1f, 0.5f, 0.3f));

        var action = creature.CurrentAction;
        var doing = new List<string>();
        for (var i = 0; i < _actions.Length && i < action.Length; i++)
        {
            var (bar, value, fill) = _actions[i];
            bar.Value = action[i];
            value.Text = $"{action[i]:+0.00;-0.00}";
            // Discrete actions (jump .. climb, except the analogue call) fire above 0.5; highlight them.
            var active = i >= 4 && i != 9 && action[i] > 0.5f;
            if (active) doing.Add(ActionNames[i]);
            fill.BgColor = active ? new Color(1f, 0.8f, 0.2f) : new Color(0.4f, 0.65f, 0.9f);
        }
        if (action.Length > 0 && Mathf.Abs(action[0]) > 0.3f) doing.Insert(0, action[0] > 0f ? "andar" : "recuar");
        _mind.Text =
            $"fazendo  {(doing.Count == 0 ? "parada" : string.Join(", ", doing))}\n" +
            "crítico  — (protocolo v2 não envia)\n" +
            "memória  zera ao nascer";
    }

    private void SetBar(string name, float value, string text, Color color)
    {
        var (bar, label, fill) = _bars[name];
        bar.Value = Mathf.Clamp(value, 0f, 1f);
        label.Text = text;
        fill.BgColor = color;
    }

    private static Color Need(float value) =>
        value < 0.25f ? new Color(1f, 0.35f, 0.3f) : value < 0.5f ? new Color(1f, 0.75f, 0.25f) : new Color(0.35f, 0.85f, 0.45f);

    private static Label Section(string text)
    {
        var label = new Label { Text = text.ToUpperInvariant() };
        label.AddThemeColorOverride("font_color", new Color(0.55f, 0.75f, 0.95f));
        label.AddThemeFontSizeOverride("font_size", 12);
        return label;
    }

    private static (ProgressBar, Label, StyleBoxFlat) AddBar(Container parent, string name, float min, float max)
    {
        var row = new HBoxContainer();
        var label = new Label { Text = name, CustomMinimumSize = new Vector2(92, 0) };
        label.AddThemeFontSizeOverride("font_size", 13);
        var fill = new StyleBoxFlat { BgColor = new Color(0.35f, 0.85f, 0.45f) };
        var bar = new ProgressBar
        {
            MinValue = min,
            MaxValue = max,
            Step = 0.001,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(130, 14),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        bar.AddThemeStyleboxOverride("fill", fill);
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0.15f, 0.17f, 0.2f) });
        var value = new Label { CustomMinimumSize = new Vector2(70, 0), HorizontalAlignment = HorizontalAlignment.Right };
        value.AddThemeFontSizeOverride("font_size", 13);
        row.AddChild(label);
        row.AddChild(bar);
        row.AddChild(value);
        parent.AddChild(row);
        return (bar, value, fill);
    }

    private static string FormatTime(float seconds) => $"{(int)(seconds / 60):00}:{(int)(seconds % 60):00}";

    private static string CauseName(DeathCause cause) => cause switch
    {
        DeathCause.Starvation => "fome",
        DeathCause.Dehydration => "sede",
        DeathCause.Cold => "frio",
        DeathCause.Heat => "calor",
        DeathCause.HeadInjury => "ferimento na cabeça",
        DeathCause.TorsoInjury => "ferimento no torso",
        DeathCause.Age => "velhice",
        DeathCause.Fall => "queda",
        DeathCause.Bite => "mordida",
        _ => "causa desconhecida"
    };
}
