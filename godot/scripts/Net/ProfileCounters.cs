using Godot;

namespace CreatureIA.Godot.Net;

/// <summary>
/// Opt-in (CREATURE_IA_PROFILE=1) accumulators for hot-path costs. Zero work when disabled.
/// </summary>
public static class ProfileCounters
{
    public static readonly bool Enabled =
        System.Environment.GetEnvironmentVariable("CREATURE_IA_PROFILE") == "1";

    private static readonly Dictionary<string, ulong> Totals = new();

    public static ulong Start() => Enabled ? Time.GetTicksUsec() : 0;

    public static void Stop(string name, ulong started)
    {
        if (!Enabled) return;
        var elapsed = Time.GetTicksUsec() - started;
        Totals[name] = Totals.TryGetValue(name, out var total) ? total + elapsed : elapsed;
    }

    /// <summary>Returns "name=avg_ms" per counter, averaged over <paramref name="ticks"/>, and resets.</summary>
    public static string DrainPerTick(int ticks)
    {
        var report = string.Join(' ', Totals.OrderBy(pair => pair.Key)
            .Select(pair => $"{pair.Key}_ms={pair.Value / 1000d / ticks:F2}"));
        Totals.Clear();
        return report;
    }
}
