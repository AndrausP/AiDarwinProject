namespace CreatureIA.Core.Simulation;

/// <summary>
/// Deterministic PCG32 (O'Neill, XSH-RR) for the simulator: same seed and same call sequence give
/// the same numbers on every machine. Not Godot's generator, so a Sim world with seed S is not the
/// Godot world with seed S block-for-block in its plants/spawns; the terrain (Core generator) is.
/// </summary>
public sealed class SimRandom
{
    private const ulong Multiplier = 6364136223846793005UL;
    private ulong _state;
    private readonly ulong _increment;

    public SimRandom(ulong seed, ulong stream = 54)
    {
        _increment = (stream << 1) | 1UL;
        _state = 0;
        NextUInt();
        _state += seed;
        NextUInt();
    }

    /// <summary>Generator position, for world snapshots (T2.0c).</summary>
    internal (ulong State, ulong Increment) Save() => (_state, _increment);

    internal SimRandom((ulong State, ulong Increment) saved)
    {
        _state = saved.State;
        _increment = saved.Increment;
    }

    /// <summary>Continues from another generator's position (used when restoring a snapshot into an existing owner).</summary>
    internal void Restore(SimRandom other)
    {
        _state = other._state;
        if (_increment != other._increment) throw new InvalidDataException("generator stream mismatch");
    }

    public uint NextUInt()
    {
        var old = _state;
        _state = unchecked(old * Multiplier + _increment);
        var xorShifted = (uint)(((old >> 18) ^ old) >> 27);
        var rotation = (int)(old >> 59);
        return (xorShifted >> rotation) | (xorShifted << (-rotation & 31));
    }

    /// <summary>Uniform in [0, 1).</summary>
    public float NextFloat() => (NextUInt() >> 8) * (1f / 16777216f);

    /// <summary>Uniform in [from, to).</summary>
    public float Range(float from, float to) => from + (to - from) * NextFloat();
}
