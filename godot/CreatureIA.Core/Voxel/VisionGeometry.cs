namespace CreatureIA.Core.Voxel;

public static class VisionGeometry
{
    /// <summary>
    /// Distance along a unit-direction ray to a vertical cylinder (base centre bx, by, bz), including
    /// its caps; null when missed or beyond <paramref name="maxDistance"/>. A ray starting inside returns 0.
    /// </summary>
    public static float? RayVerticalCylinder(float ox, float oy, float oz, float dx, float dy, float dz,
        float bx, float by, float bz, float radius, float height, float maxDistance)
    {
        var px = ox - bx;
        var pz = oz - bz;
        var a = dx * dx + dz * dz;
        var c = px * px + pz * pz - radius * radius;
        float tEnter, tExit;
        if (a < 1e-10f)
        {
            // Vertical ray: inside the circle for all t or never.
            if (c > 0f) return null;
            tEnter = float.NegativeInfinity;
            tExit = float.PositiveInfinity;
        }
        else
        {
            var b = px * dx + pz * dz;
            var disc = b * b - a * c;
            if (disc < 0f) return null;
            var root = MathF.Sqrt(disc);
            tEnter = (-b - root) / a;
            tExit = (-b + root) / a;
        }
        // Clip by the slab by <= y <= by + height.
        float yEnter, yExit;
        if (MathF.Abs(dy) < 1e-10f)
        {
            if (oy < by || oy > by + height) return null;
            yEnter = float.NegativeInfinity;
            yExit = float.PositiveInfinity;
        }
        else
        {
            var t1 = (by - oy) / dy;
            var t2 = (by + height - oy) / dy;
            yEnter = MathF.Min(t1, t2);
            yExit = MathF.Max(t1, t2);
        }
        var enter = MathF.Max(tEnter, yEnter);
        var exit = MathF.Min(tExit, yExit);
        if (enter > exit || exit < 0f) return null;
        var t = MathF.Max(0f, enter);
        return t <= maxDistance ? t : null;
    }
}
