namespace CreatureIA.Core.Voxel;

/// <summary>Seeded 2D gradient (Perlin) noise with fBm, independent of the engine so Core stays testable.</summary>
public sealed class PerlinNoise
{
    private readonly int[] _perm = new int[512];
    private readonly float _frequency;
    private readonly int _octaves;

    public PerlinNoise(int seed, float frequency, int octaves = 1)
    {
        _frequency = frequency;
        _octaves = Math.Max(1, octaves);
        var p = new int[256];
        for (var i = 0; i < 256; i++) p[i] = i;
        var state = (ulong)(uint)seed ^ 0x9E3779B97F4A7C15UL;
        for (var i = 255; i > 0; i--)
        {
            var j = (int)(SplitMix(ref state) % (ulong)(i + 1));
            (p[i], p[j]) = (p[j], p[i]);
        }
        for (var i = 0; i < 512; i++) _perm[i] = p[i & 255];
    }

    /// <summary>fBm in roughly [-1, 1] (gain 0.5, lacunarity 2).</summary>
    public float Sample(float x, float z)
    {
        float sum = 0f, amplitude = 1f, norm = 0f, f = _frequency;
        for (var o = 0; o < _octaves; o++)
        {
            sum += amplitude * Single(x * f, z * f);
            norm += amplitude;
            amplitude *= 0.5f;
            f *= 2f;
        }
        return Math.Clamp(sum / norm * 1.4f, -1f, 1f);
    }

    private float Single(float x, float z)
    {
        var xi = (int)MathF.Floor(x);
        var zi = (int)MathF.Floor(z);
        var xf = x - xi;
        var zf = z - zi;
        xi &= 255;
        zi &= 255;
        var u = Fade(xf);
        var v = Fade(zf);
        var aa = _perm[_perm[xi] + zi];
        var ab = _perm[_perm[xi] + zi + 1];
        var ba = _perm[_perm[xi + 1] + zi];
        var bb = _perm[_perm[xi + 1] + zi + 1];
        var x1 = Lerp(Grad(aa, xf, zf), Grad(ba, xf - 1f, zf), u);
        var x2 = Lerp(Grad(ab, xf, zf - 1f), Grad(bb, xf - 1f, zf - 1f), u);
        return Lerp(x1, x2, v);
    }

    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Grad(int hash, float x, float z) => (hash & 7) switch
    {
        0 => x + z,
        1 => -x + z,
        2 => x - z,
        3 => -x - z,
        4 => x,
        5 => -x,
        6 => z,
        _ => -z
    };

    internal static ulong SplitMix(ref ulong state)
    {
        var z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Deterministic hash of integer coordinates to [0, 1).</summary>
    public static float Hash01(int seed, int x, int z)
    {
        var state = ((ulong)(uint)seed << 32) ^ ((ulong)(uint)x * 0x632BE59BD9B4E019UL) ^ ((ulong)(uint)z * 0x8CB92BA72F3D8DD7UL);
        return (SplitMix(ref state) >> 40) / (float)(1 << 24);
    }
}
