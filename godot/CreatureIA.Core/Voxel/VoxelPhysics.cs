namespace CreatureIA.Core.Voxel;

/// <summary>Kinematic body: feet-centred axis-aligned box.</summary>
public struct VoxelBodyState
{
    public float X, Y, Z;
    public float VelocityX, VelocityY, VelocityZ;
    public bool OnFloor;
}

public readonly record struct VoxelBodyShape(float HalfWidth, float Height, float StepHeight);

public readonly record struct VoxelMoveResult(bool OnFloor, bool HitWall, bool HitCeiling, bool InWater, float LandingSpeed, bool Stepped);

/// <summary>
/// Block collision without a physics engine: the box moves one axis at a time in sub-steps
/// shorter than a block, is clamped against solid blocks, and auto-steps up ledges of at most
/// <see cref="VoxelBodyShape.StepHeight"/> while grounded. The X/Z map edges act as walls.
/// </summary>
public static class VoxelPhysics
{
    public const float Gravity = 9.81f;
    private const float Epsilon = 1e-4f;

    public static VoxelMoveResult Move(VoxelGrid grid, ref VoxelBodyState body, VoxelBodyShape shape, float dt)
    {
        var dx = body.VelocityX * dt;
        var dy = body.VelocityY * dt;
        var dz = body.VelocityZ * dt;
        var maxDelta = MathF.Max(MathF.Abs(dx), MathF.Max(MathF.Abs(dy), MathF.Abs(dz)));
        var steps = Math.Max(1, (int)MathF.Ceiling(maxDelta / (grid.BlockSize * 0.4f)));
        bool onFloor = false, hitWall = false, hitCeiling = false, stepped = false;
        var landingSpeed = 0f;
        var wasOnFloor = body.OnFloor;

        for (var i = 0; i < steps; i++)
        {
            // Vertical first so the floor contact of this sub-step enables stepping.
            if (dy != 0f)
            {
                var vy = body.VelocityY;
                if (!MoveAxis(grid, ref body, shape, 1, dy / steps))
                {
                    if (dy < 0)
                    {
                        onFloor = true;
                        landingSpeed = MathF.Max(landingSpeed, -vy);
                    }
                    else hitCeiling = true;
                    body.VelocityY = 0f;
                    dy = 0f;
                }
            }
            var grounded = onFloor || wasOnFloor;
            if (dx != 0f && !MoveHorizontal(grid, ref body, shape, 0, dx / steps, grounded, ref stepped))
            {
                hitWall = true;
                body.VelocityX = 0f;
                dx = 0f;
            }
            if (dz != 0f && !MoveHorizontal(grid, ref body, shape, 2, dz / steps, grounded, ref stepped))
            {
                hitWall = true;
                body.VelocityZ = 0f;
                dz = 0f;
            }
        }

        // Floor probe: resting contact without downward motion this frame.
        if (!onFloor && body.VelocityY <= 0f && BoxBlocked(grid, body.X, body.Y - 2f * Epsilon - 0.01f, body.Z, shape))
            onFloor = true;
        body.OnFloor = onFloor;
        var inWater = BlockTypes.IsLiquid(grid.GetAtWorld(body.X, body.Y + shape.Height * 0.4f, body.Z));
        return new VoxelMoveResult(onFloor, hitWall, hitCeiling, inWater, landingSpeed, stepped);
    }

    /// <summary>True when the box at this position overlaps any solid block or leaves the map sideways.</summary>
    public static bool BoxBlocked(VoxelGrid grid, float x, float y, float z, VoxelBodyShape shape)
    {
        var s = grid.BlockSize;
        var minX = (int)MathF.Floor((x - shape.HalfWidth - grid.OriginX) / s);
        var maxX = (int)MathF.Floor((x + shape.HalfWidth - grid.OriginX) / s - Epsilon);
        var minY = (int)MathF.Floor((y - grid.OriginY) / s);
        var maxY = (int)MathF.Floor((y + shape.Height - grid.OriginY) / s - Epsilon);
        var minZ = (int)MathF.Floor((z - shape.HalfWidth - grid.OriginZ) / s);
        var maxZ = (int)MathF.Floor((z + shape.HalfWidth - grid.OriginZ) / s - Epsilon);
        if (minX < 0 || minZ < 0 || maxX >= grid.SizeX || maxZ >= grid.SizeZ) return true;
        for (var by = minY; by <= maxY; by++)
        for (var bz = minZ; bz <= maxZ; bz++)
        for (var bx = minX; bx <= maxX; bx++)
            if (BlockTypes.IsSolid(grid.Get(bx, by, bz))) return true;
        return false;
    }

    private static bool MoveHorizontal(VoxelGrid grid, ref VoxelBodyState body, VoxelBodyShape shape, int axis, float delta, bool grounded, ref bool stepped)
    {
        if (MoveAxis(grid, ref body, shape, axis, delta)) return true;
        if (!grounded || shape.StepHeight <= 0f) return false;
        // Step up: lift, move, then settle onto the ledge.
        var nx = body.X + (axis == 0 ? delta : 0f);
        var nz = body.Z + (axis == 2 ? delta : 0f);
        if (BoxBlocked(grid, body.X, body.Y + shape.StepHeight, body.Z, shape)) return false;
        if (BoxBlocked(grid, nx, body.Y + shape.StepHeight, nz, shape)) return false;
        var s = grid.BlockSize;
        var lifted = body.Y + shape.StepHeight;
        // Settle down onto the top of the obstacle (block-aligned).
        var floorY = MathF.Floor((lifted - grid.OriginY) / s) * s + grid.OriginY;
        while (floorY > body.Y && !BoxBlocked(grid, nx, floorY - s + Epsilon, nz, shape)) floorY -= s;
        body.X = nx;
        body.Z = nz;
        body.Y = floorY + Epsilon;
        stepped = true;
        return true;
    }

    /// <summary>Moves along one axis; on contact clamps to the block face and returns false.</summary>
    private static bool MoveAxis(VoxelGrid grid, ref VoxelBodyState body, VoxelBodyShape shape, int axis, float delta)
    {
        float x = body.X, y = body.Y, z = body.Z;
        if (axis == 0) x += delta;
        else if (axis == 1) y += delta;
        else z += delta;
        if (!BoxBlocked(grid, x, y, z, shape))
        {
            body.X = x;
            body.Y = y;
            body.Z = z;
            return true;
        }
        var s = grid.BlockSize;
        switch (axis)
        {
            case 0:
            {
                var edge = delta > 0 ? body.X + shape.HalfWidth : body.X - shape.HalfWidth;
                var target = delta > 0
                    ? MathF.Floor((edge + delta - grid.OriginX) / s) * s + grid.OriginX - shape.HalfWidth - Epsilon
                    : MathF.Ceiling((edge + delta - grid.OriginX) / s) * s + grid.OriginX + shape.HalfWidth + Epsilon;
                if (!BoxBlocked(grid, target, body.Y, body.Z, shape) && (delta > 0 ? target >= body.X : target <= body.X)) body.X = target;
                break;
            }
            case 1:
            {
                var target = delta > 0
                    ? MathF.Floor((body.Y + shape.Height + delta - grid.OriginY) / s) * s + grid.OriginY - shape.Height - Epsilon
                    : MathF.Ceiling((body.Y + delta - grid.OriginY) / s) * s + grid.OriginY + Epsilon;
                if (!BoxBlocked(grid, body.X, target, body.Z, shape) && (delta > 0 ? target >= body.Y : target <= body.Y)) body.Y = target;
                break;
            }
            default:
            {
                var edge = delta > 0 ? body.Z + shape.HalfWidth : body.Z - shape.HalfWidth;
                var target = delta > 0
                    ? MathF.Floor((edge + delta - grid.OriginZ) / s) * s + grid.OriginZ - shape.HalfWidth - Epsilon
                    : MathF.Ceiling((edge + delta - grid.OriginZ) / s) * s + grid.OriginZ + shape.HalfWidth + Epsilon;
                if (!BoxBlocked(grid, body.X, body.Y, target, shape) && (delta > 0 ? target >= body.Z : target <= body.Z)) body.Z = target;
                break;
            }
        }
        return false;
    }
}
