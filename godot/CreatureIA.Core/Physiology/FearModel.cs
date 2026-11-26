namespace CreatureIA.Core.Physiology;

/// <summary>
/// Fear as a feeling (Task 002): rises quickly towards the current threat + pain level and fades
/// slowly once it is gone. It only enters the interoception (and the reward punishes it); nothing
/// here decides to flee — the brain has to learn what reduces it.
/// </summary>
public static class FearModel
{
    /// <summary>Rise rate towards a higher target, 1/s (≈ 0.25 s to reach 63 %).</summary>
    public const float RiseRate = 4f;

    /// <summary>Decay time constant once the cause is gone, seconds.</summary>
    public const float DecayTimeSeconds = 3f;

    /// <summary>Weight of current pain in the fear target.</summary>
    public const float PainWeight = 0.8f;

    public static float Step(float fear, float threat, float pain, float dt)
    {
        var target = Math.Clamp(threat + PainWeight * pain, 0f, 1f);
        var next = target > fear
            ? fear + (target - fear) * (1f - MathF.Exp(-RiseRate * dt))
            : target + (fear - target) * MathF.Exp(-dt / DecayTimeSeconds);
        return Math.Clamp(next, 0f, 1f);
    }

    /// <summary>
    /// How threatening another creature is to the observer: carnivores fully, bigger animals in
    /// proportion to how much bigger (size ratio above 1.15). Etapa 1 has neither, so this is 0.
    /// </summary>
    public static float Threatness(float otherSize, float selfSize, bool otherIsCarnivore)
    {
        if (otherIsCarnivore) return 1f;
        var ratio = otherSize / MathF.Max(1e-3f, selfSize);
        return Math.Clamp((ratio - 1.15f) / 1.0f, 0f, 1f);
    }

    /// <summary>Threat perceived from one creature at a distance: fades linearly to zero at <paramref name="range"/>.</summary>
    public static float Perceived(float threatness, float distance, float range) =>
        threatness * Math.Clamp(1f - distance / range, 0f, 1f);
}
