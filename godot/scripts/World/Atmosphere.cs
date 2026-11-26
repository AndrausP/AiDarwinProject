using Godot;

namespace CreatureIA.Godot.World;

/// <summary>
/// Presentation-only sky, fog and sun driven by <see cref="DayNightCycle"/>. Not created headless.
/// A minimum ambient level keeps night scenes readable for the spectator; it does not affect senses.
/// </summary>
public partial class Atmosphere : Node
{
    private DayNightCycle _dayNight = null!;
    private DirectionalLight3D _sun = null!;
    private global::Godot.Environment _environment = null!;
    private ProceduralSkyMaterial _sky = null!;

    private static readonly Color DaySkyTop = new(0.32f, 0.55f, 0.85f);
    private static readonly Color DayHorizon = new(0.72f, 0.82f, 0.92f);
    private static readonly Color NightSkyTop = new(0.02f, 0.03f, 0.08f);
    private static readonly Color NightHorizon = new(0.1f, 0.12f, 0.2f);
    private static readonly Color DuskHorizon = new(0.95f, 0.55f, 0.3f);

    public override void _Ready()
    {
        Name = "Atmosphere";
        _dayNight = GetNode<DayNightCycle>("../DayNightCycle");
        _sun = GetNode<DirectionalLight3D>("../Sun");
        _environment = GetNode<WorldEnvironment>("../WorldEnvironment").Environment;
        _sky = new ProceduralSkyMaterial
        {
            GroundBottomColor = new Color(0.2f, 0.18f, 0.15f),
            GroundHorizonColor = DayHorizon,
            SunAngleMax = 20f
        };
        _environment.BackgroundMode = global::Godot.Environment.BGMode.Sky;
        _environment.Sky = new Sky { SkyMaterial = _sky };
        _environment.AmbientLightSource = global::Godot.Environment.AmbientSource.Sky;
        _environment.TonemapMode = global::Godot.Environment.ToneMapper.Filmic;
        _environment.FogEnabled = true;
        _environment.FogDensity = 0.0035f;
        _sun.ShadowEnabled = true;
        _sun.DirectionalShadowMaxDistance = 120f;
    }

    public override void _Process(double delta)
    {
        var t = _dayNight.TimeOfDay01;
        var light = _dayNight.LightFactor;
        // t = 0.25 sunrise (horizon), 0.5 noon (overhead), 0.75 sunset.
        _sun.RotationDegrees = new Vector3(-(t * 360f - 90f), -30f, 0f);
        _sun.LightEnergy = Mathf.Lerp(0.05f, 1.25f, light);
        var dusk = 1f - Mathf.Abs(light - 0.5f) * 2f;
        var horizon = NightHorizon.Lerp(DayHorizon, light).Lerp(DuskHorizon, dusk * 0.45f);
        _sky.SkyTopColor = NightSkyTop.Lerp(DaySkyTop, light);
        _sky.SkyHorizonColor = horizon;
        _sky.GroundHorizonColor = horizon;
        _environment.AmbientLightEnergy = Mathf.Lerp(0.45f, 1f, light);
        _environment.FogLightColor = horizon;
    }
}
