using System.Globalization;

namespace CreatureIA.Godot.World;

/// <summary>
/// Process-wide run settings read once from the environment, so headless benchmark runs can vary the
/// seed and horizon without editing scenes. Read lazily by each node: child _Ready runs before the
/// parent's, so the seed cannot be pushed down from WorldRoot.
/// </summary>
public static class RunSettings
{
    private static readonly int? SeedOverride = ReadInt("CREATURE_IA_WORLD_SEED");

    /// <summary>Stop after this many brain ticks (0 = run forever).</summary>
    public static readonly long MaxBrainTicks = ReadInt("CREATURE_IA_MAX_BRAIN_TICKS") ?? 0;

    /// <summary>Quit once every creature has died (benchmark runs without respawn).</summary>
    public static readonly bool QuitWhenExtinct =
        System.Environment.GetEnvironmentVariable("CREATURE_IA_QUIT_WHEN_EXTINCT") == "1";

    public static int Seed(int exported) => SeedOverride ?? exported;

    /// <summary>White showcase room with every example (CREATURE_IA_SHOWCASE=1); viewing only, never training.</summary>
    public static readonly bool Showcase =
        System.Environment.GetEnvironmentVariable("CREATURE_IA_SHOWCASE") == "1";

    /// <summary>Etapa-2 island preview (CREATURE_IA_ISLAND=1): 768 m island, salt sea, rivers. Viewing only for now.</summary>
    public static readonly bool Island =
        System.Environment.GetEnvironmentVariable("CREATURE_IA_ISLAND") == "1";

    private static readonly int? PlantsOverride = ReadInt("CREATURE_IA_PLANTS");

    /// <summary>Plant count override (training curriculum, e.g. abundant food early on).</summary>
    public static int Plants(int exported) => PlantsOverride ?? exported;

    private static readonly int? LakesOverride = ReadInt("CREATURE_IA_LAKES");

    /// <summary>Lake count override (training curriculum: more water early on).</summary>
    public static int Lakes(int exported) => LakesOverride ?? exported;

    private static readonly int? PortOverride = ReadInt("CREATURE_IA_BRAIN_PORT");

    /// <summary>Brain server port; lets a headless run coexist with a spectator session.</summary>
    public static int Port(int exported) => PortOverride ?? exported;

    private static int? ReadInt(string name) =>
        int.TryParse(System.Environment.GetEnvironmentVariable(name), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value) ? value : null;
}
