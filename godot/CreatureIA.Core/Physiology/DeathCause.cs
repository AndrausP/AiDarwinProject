namespace CreatureIA.Core.Physiology;

public enum DeathCause : byte
{
    None = 0,
    Starvation = 1,
    Dehydration = 2,
    Cold = 3,
    Heat = 4,
    HeadInjury = 5,
    TorsoInjury = 6,
    Age = 7,
    Fall = 8,
    Bite = 9,
    /// <summary>Exhausted while swimming in deep water (T2.1b).</summary>
    Drowning = 10
}
