namespace CreatureIA.Core.Voxel;

/// <summary>
/// Digging: seconds of continuous effort needed to remove one block with base (gene-free) claws.
/// Etapa 3 scales this by the claw/strength genes.
/// </summary>
public static class Digging
{
    public const float ReachMeters = 1.2f;

    public static float SecondsToRemove(BlockType block) => block switch
    {
        BlockType.Grass or BlockType.Dirt or BlockType.Sand or BlockType.Snow => 0.8f,
        BlockType.Leaves or BlockType.PineLeaves or BlockType.BirchLeaves or BlockType.AcaciaLeaves => 0.5f,
        BlockType.Cactus => 0.8f,
        BlockType.Wood or BlockType.BirchWood => 2f,
        BlockType.RedWood => 3f,
        BlockType.Stone or BlockType.Marble => 4f,
        _ => float.PositiveInfinity
    };
}

/// <summary>Per-creature progress on the block currently being dug; switching blocks restarts it.</summary>
public sealed class DigProgress
{
    private (int X, int Y, int Z)? _target;
    private float _seconds;

    public float Fraction { get; private set; }

    /// <summary>Accumulates effort on a block; returns true when it should be removed now.</summary>
    public bool Work(int x, int y, int z, BlockType block, float dt)
    {
        var required = Digging.SecondsToRemove(block);
        if (float.IsInfinity(required))
        {
            Reset();
            return false;
        }
        if (_target != (x, y, z))
        {
            _target = (x, y, z);
            _seconds = 0f;
        }
        _seconds += dt;
        Fraction = Math.Clamp(_seconds / required, 0f, 1f);
        if (_seconds < required) return false;
        Reset();
        return true;
    }

    public void Reset()
    {
        _target = null;
        _seconds = 0f;
        Fraction = 0f;
    }

    /// <summary>State for world snapshots: (target block, seconds of effort, fraction).</summary>
    public ((int X, int Y, int Z)? Target, float Seconds, float Fraction) Save() => (_target, _seconds, Fraction);

    public void Restore((int X, int Y, int Z)? target, float seconds, float fraction)
    {
        _target = target;
        _seconds = seconds;
        Fraction = fraction;
    }
}
