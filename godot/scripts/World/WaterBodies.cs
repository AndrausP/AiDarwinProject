using CreatureIA.Core.Voxel;
using Godot;

namespace CreatureIA.Godot.World;

/// <summary>
/// Water queries over the voxel grid. Lakes are water blocks now (rendered by the chunk mesh);
/// drinking needs a water block within reach, the scent field comes from the lake basins.
/// </summary>
public partial class WaterBodies : Node3D
{
    /// <summary>Horizontal reach of the muzzle for drinking, metres (3 blocks).</summary>
    public const float DrinkReachMeters = 1.5f;

    private VoxelWorld _world = null!;
    private (Vector2 Centre, float Radius)[] _lakes = Array.Empty<(Vector2, float)>();

    public override void _Ready()
    {
        _world = GetNode<BiomeMap>("../BiomeMap").World;
        _lakes = _world.Lakes.Select(l => (new Vector2(l.CenterX, l.CenterZ), l.RadiusMeters)).ToArray();
    }

    public bool IsNearDrinkableWater(Vector3 position, float maximumDistance = DrinkReachMeters) =>
        _world.IsNearWater(position.X, position.Y, position.Z, maximumDistance);

    public float SampleScent(Vector3 position)
    {
        var scent = 0f;
        var p = new Vector2(position.X, position.Z);
        foreach (var (centre, radius) in _lakes)
        {
            var edgeDistance = Mathf.Max(0f, p.DistanceTo(centre) - radius);
            scent += 1f / (1f + edgeDistance * edgeDistance);
        }
        return scent;
    }
}
