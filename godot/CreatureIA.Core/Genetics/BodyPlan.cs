using CreatureIA.Core.Physiology;

namespace CreatureIA.Core.Genetics;

/// <summary>
/// Modular body from the expressed genes (T2.2b). Every trait buys an ability and pays for it: more
/// mass costs metabolism by Kleiber's law (mass^0.75), each part adds upkeep, and abilities trade
/// against each other (a carapace protects but slows). Parts below the functional threshold give
/// nothing yet still cost a little: a dormant part must grow through generations before it pays off.
/// </summary>
public sealed record BodyPlan
{
    public const float FunctionalThreshold = 0.15f;
    public const float BaseSpeed = 6f;
    public const float BaseStepHeight = 0.55f;
    public const double KleiberExponent = 0.75;
    /// <summary>Basal metabolic rate of the reference body (size 1, nothing extra), per second.</summary>
    public const double ReferenceBasalRate = 0.002;
    /// <summary>Upkeep per unit of each part, as a share of the reference basal rate.</summary>
    public const double PartUpkeep = 0.25;

    public float Size { get; init; }
    public float LegLength { get; init; }
    public float Fur { get; init; }
    public float Claws { get; init; }
    public float Carapace { get; init; }
    public float Horns { get; init; }
    public float Fins { get; init; }
    public float Teeth { get; init; }

    public static BodyPlan FromGenome(Genome genome)
    {
        var size = genome.Phenotype("size");
        if (genome.Sex == Sex.Male) size *= 1f + genome.Phenotype("male_size_bias");
        return new BodyPlan
        {
            Size = Math.Clamp(size, 0.3f, 4f),
            LegLength = genome.Phenotype("leg_length"),
            Fur = genome.Phenotype("fur"),
            Claws = genome.Phenotype("claws"),
            Carapace = genome.Phenotype("carapace"),
            Horns = genome.Phenotype("horns"),
            Fins = genome.Phenotype("fins"),
            Teeth = genome.Phenotype("teeth"),
        };
    }

    private static float Functional(float part) => part < FunctionalThreshold ? 0f : (part - FunctionalThreshold) / (1f - FunctionalThreshold);

    /// <summary>Body mass relative to the reference animal: volume plus the heavy parts.</summary>
    public double Mass => Math.Pow(Size, 3) * (1.0 + 0.6 * Carapace + 0.15 * Horns + 0.1 * LegLength + 0.05 * Claws);

    /// <summary>Basal metabolic rate (per second): Kleiber on mass plus upkeep of every part.</summary>
    public double BasalMetabolicRate =>
        ReferenceBasalRate * (Math.Pow(Mass, KleiberExponent)
                              + PartUpkeep * (Claws + Carapace + Horns + Fins + Teeth + Fur * 0.5 + LegLength * 0.3));

    /// <summary>Top speed: longer legs and a bigger body run faster; a carapace and horns weigh it down.</summary>
    public float MaxSpeed => BaseSpeed * MathF.Pow(Size, 0.25f) * (0.7f + 0.6f * LegLength)
                             * (1f - 0.45f * Functional(Carapace)) * (1f - 0.1f * Functional(Horns));

    public float StepHeight => BaseStepHeight * Size * (0.8f + 0.4f * LegLength);
    /// <summary>Climbing speed multiplier (claws grip); 0 without functional claws: no climbing.</summary>
    public float ClimbFactor => Functional(Claws) * (1f - 0.5f * Functional(Carapace));
    /// <summary>Digging speed multiplier (claws dig; teeth help a little).</summary>
    public float DigFactor => 0.3f + 1.5f * Functional(Claws) + 0.2f * Functional(Teeth);
    /// <summary>Swimming speed factor: fins make water travel cheap and fast.</summary>
    public float SwimFactor => 0.45f + 0.5f * Functional(Fins);
    /// <summary>Share of bite damage absorbed.</summary>
    public float Armor => 0.7f * Functional(Carapace);
    public float BiteDamage => (0.2f + Functional(Teeth) + 0.5f * Functional(Horns)) * Size;

    /// <summary>Physiology traits for this body (size, armour, insulation from fur, metabolism).</summary>
    public BodyTraits ToTraits() => new()
    {
        Size = Size,
        Armor = Math.Clamp(Armor, 0f, 1f),
        Insulation = Math.Clamp(0.3 + 0.7 * Fur, 0, 1),
        BasalMetabolicRate = BasalMetabolicRate,
        MovementCost = 0.003 * Math.Pow(Mass, KleiberExponent),
    };
}
