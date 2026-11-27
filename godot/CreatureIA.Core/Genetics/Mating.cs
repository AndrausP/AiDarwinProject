using CreatureIA.Core.Simulation;

namespace CreatureIA.Core.Genetics;

/// <summary>What the world knows about a would-be parent at the moment of the mate action.</summary>
public readonly record struct MateCandidate(Genome Genome, double AgeSeconds, double Energy, float X, float Z, bool WantsToMate);

public enum MateOutcome { Conceived, NotBothWilling, TooFar, Immature, TooHungry, SameSex, Inviable }

/// <summary>
/// Sexual reproduction rules of the plan (T2.4a): two adults within 2 m both choose the mate action,
/// both above the fertile energy level, opposite sexes; the child is crossover + mutation of both, and
/// it develops with a chance that falls with the parents' genetic distance (speciation). Each parent
/// pays the yolk. Nothing here decides *to* mate: that is the brain's action.
/// </summary>
public static class Mating
{
    public const float MaxDistanceMeters = 2f;
    /// <summary>Energy each parent needs and pays per conception (yolk/gestation investment).</summary>
    public const double FertileEnergy = 0.55;
    public const double YolkCost = 0.2;

    public static MateOutcome Check(MateCandidate a, MateCandidate b)
    {
        if (!a.WantsToMate || !b.WantsToMate) return MateOutcome.NotBothWilling;
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        if (dx * dx + dz * dz > MaxDistanceMeters * MaxDistanceMeters) return MateOutcome.TooFar;
        if (a.Genome.Sex == b.Genome.Sex) return MateOutcome.SameSex;
        if (!LifeCycle.FromGenome(a.Genome).IsMature(a.AgeSeconds) || !LifeCycle.FromGenome(b.Genome).IsMature(b.AgeSeconds))
            return MateOutcome.Immature;
        if (a.Energy < FertileEnergy || b.Energy < FertileEnergy) return MateOutcome.TooHungry;
        return MateOutcome.Conceived;
    }

    /// <summary>Child genome, or null when the embryo is not viable (distance between the parents' genomes).</summary>
    public static Genome? Conceive(MateCandidate a, MateCandidate b, SimRandom rng)
    {
        var (mother, father) = a.Genome.Sex == Sex.Female ? (a.Genome, b.Genome) : (b.Genome, a.Genome);
        var viability = Reproduction.Viability(Reproduction.Distance(mother, father));
        var child = Reproduction.Child(mother, father, rng);
        return rng.NextFloat() < viability ? child : null;
    }
}
