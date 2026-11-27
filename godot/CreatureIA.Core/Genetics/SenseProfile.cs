namespace CreatureIA.Core.Genetics;

/// <summary>
/// Sense acuity from genes (T2.2d). Multipliers over the etapa-1 senses (vision 25 m / 210°, smell,
/// hearing 25 m); 1 everywhere reproduces etapa 1 exactly. Sharper senses cost metabolism (a bigger
/// eye and brain to process it), so acuity evolves only where it pays.
/// </summary>
public readonly record struct SenseProfile(float VisionRange, float VisionFov, float NightVision, float Smell, float Hearing)
{
    public const float BaseVisionRange = 25f;
    public const float BaseFieldOfViewDegrees = 210f;
    public const float BaseHearingRange = 25f;
    /// <summary>Night vision multiplier in the dark (etapa 1: 0.4 of the day range).</summary>
    public const float BaseNightFactor = 0.4f;
    /// <summary>Extra basal cost per unit of acuity above the etapa-1 senses, as a share of the reference rate.</summary>
    public const double CostPerUnit = 0.08;

    public static readonly SenseProfile Etapa1 = new(1f, 1f, 0f, 1f, 1f);

    public static SenseProfile FromGenome(Genome g) => new(
        g.Phenotype("vision_range"), g.Phenotype("vision_fov"), g.Phenotype("night_vision"), g.Phenotype("smell"), g.Phenotype("hearing"));

    public float VisionRangeMeters => BaseVisionRange * VisionRange;
    public float FieldOfViewDegrees => Math.Min(330f, BaseFieldOfViewDegrees * VisionFov);
    public float HearingRangeMeters => BaseHearingRange * Hearing;
    /// <summary>Vision range multiplier at a given light level (0 dark .. 1 day).</summary>
    public float LightFactor(float light)
    {
        var dark = BaseNightFactor + (1f - BaseNightFactor) * NightVision;
        return dark + (1f - dark) * Math.Clamp(light, 0f, 1f);
    }
    /// <summary>Multiplier on scent intensities (a keener nose picks fainter gradients).</summary>
    public float ScentGain => Smell;

    /// <summary>Extra metabolic rate (per second) for acuity, relative to <see cref="BodyPlan.ReferenceBasalRate"/>.</summary>
    public double ExtraBasalRate =>
        BodyPlan.ReferenceBasalRate * CostPerUnit *
        (Cost(VisionRange) * 2.0 + Cost(VisionFov) + NightVision * 1.5 + Cost(Smell) + Cost(Hearing));

    // Senses can also be cheapened: a duller sense than etapa 1 saves energy.
    private static double Cost(float multiplier) => multiplier - 1.0;
}
