using CreatureIA.Core.Voxel;
using NUnit.Framework;

namespace CreatureIA.Tests;

[TestFixture]
public sealed class IslandTests
{
    // 192 m island keeps the test fast; the plan's 768 m map uses the same generator.
    private static readonly VoxelWorldSettings Settings = new()
    {
        Island = true, SizeX = 384, SizeZ = 384, SizeY = 96, WaterLevel = 24, LakeCount = 2, RiverCount = 4
    };

    private static VoxelWorld _world = null!;

    [OneTimeSetUp]
    public void Generate() => _world = VoxelWorldGenerator.Generate(7, Settings);

    private static int Top(VoxelGrid grid, int x, int z) => grid.TopBlockY(x, z, solidOnly: true);

    [Test]
    public void Should_SurroundLandWithSaltSea_When_IslandIsGenerated()
    {
        var grid = _world.Grid;
        var s = _world.Settings;
        for (var i = 0; i < s.SizeX; i++)
        {
            Assert.That(grid.Get(i, s.WaterLevel, 0), Is.EqualTo(BlockType.SaltWater));
            Assert.That(grid.Get(i, s.WaterLevel, s.SizeZ - 1), Is.EqualTo(BlockType.SaltWater));
            Assert.That(grid.Get(0, s.WaterLevel, i), Is.EqualTo(BlockType.SaltWater));
            Assert.That(grid.Get(s.SizeX - 1, s.WaterLevel, i), Is.EqualTo(BlockType.SaltWater));
        }
        var land = 0;
        for (var z = 0; z < s.SizeZ; z += 2)
        for (var x = 0; x < s.SizeX; x += 2)
            if (Top(grid, x, z) > s.WaterLevel) land++;
        var share = land / (double)(s.SizeX / 2 * (s.SizeZ / 2));
        Assert.That(share, Is.InRange(0.2, 0.7), "an island, not a continent or an empty sea");
    }

    [Test]
    public void Should_RunRiversFromHighSourceToSea_When_IslandIsGenerated()
    {
        var grid = _world.Grid;
        var s = _world.Settings;
        Assert.That(_world.Rivers, Is.Not.Empty);
        foreach (var river in _world.Rivers)
        {
            var (sx, sz) = river[0];
            Assert.That(Top(grid, sx, sz), Is.GreaterThanOrEqualTo(s.WaterLevel + 8), "source in the highlands");
            for (var i = 1; i < river.Count; i++)
                Assert.That(Math.Abs(river[i].X - river[i - 1].X) + Math.Abs(river[i].Z - river[i - 1].Z), Is.EqualTo(1),
                    "water flows block to block (4-connected)");
            for (var i = 0; i < river.Count - 1; i++)
            {
                var (x, z) = river[i];
                var hasFresh = false;
                for (var y = 1; y < s.SizeY && !hasFresh; y++) hasFresh = grid.Get(x, y, z) == BlockType.Water;
                Assert.That(hasFresh, Is.True, $"river cell {i} holds fresh water");
            }
            var (mx, mz) = river[^1];
            Assert.That(grid.Get(mx, s.WaterLevel, mz), Is.EqualTo(BlockType.SaltWater), "mouth reaches the sea");
        }
    }

    [Test]
    public void Should_NotQuenchThirst_When_OnlySeaIsWithinReach()
    {
        var grid = _world.Grid;
        var s = _world.Settings;
        var checkedBeaches = 0;
        for (var z = 2; z < s.SizeZ - 2 && checkedBeaches < 50; z += 3)
        for (var x = 2; x < s.SizeX - 2 && checkedBeaches < 50; x += 3)
        {
            var top = Top(grid, x, z);
            if (top < s.WaterLevel || grid.Get(x + 1, s.WaterLevel, z) != BlockType.SaltWater) continue;
            var freshNearby = false;
            for (var dz = -4; dz <= 4; dz++)
            for (var dx = -4; dx <= 4; dx++)
            for (var y = top - 3; y <= top + 2; y++)
                freshNearby |= grid.Get(x + dx, y, z + dz) == BlockType.Water;
            if (freshNearby) continue;
            var wx = grid.OriginX + (x + 0.5f) * grid.BlockSize;
            var wz = grid.OriginZ + (z + 0.5f) * grid.BlockSize;
            var feet = grid.OriginY + (top + 1) * grid.BlockSize;
            Assert.That(_world.IsNearWater(wx, feet, wz, 1.5f), Is.False, "salt water is not drinkable");
            checkedBeaches++;
        }
        Assert.That(checkedBeaches, Is.GreaterThan(10));
    }

    [Test]
    public void Should_LetCreatureDrink_When_StandingOnRiverBank()
    {
        var grid = _world.Grid;
        var river = _world.Rivers[0];
        var (x, z) = river[river.Count / 3];
        // Bank: first dry neighbour column across the stream.
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (2, 0), (-2, 0), (0, 2), (0, -2) })
        {
            var top = Top(grid, x + dx, z + dz);
            if (grid.Get(x + dx, top, z + dz) is BlockType.Water or BlockType.SaltWater) continue;
            var wx = grid.OriginX + (x + dx + 0.5f) * grid.BlockSize;
            var wz = grid.OriginZ + (z + dz + 0.5f) * grid.BlockSize;
            Assert.That(_world.IsNearWater(wx, grid.OriginY + (top + 1) * grid.BlockSize, wz, 1.5f), Is.True);
            return;
        }
        Assert.Fail("river has no dry bank next to it");
    }

    [Test]
    public void Should_GenerateIdenticalIsland_When_SeedIsRepeated()
    {
        var again = VoxelWorldGenerator.Generate(7, Settings);
        var other = VoxelWorldGenerator.Generate(8, Settings);
        var same = true;
        var differs = false;
        for (var z = 0; z < Settings.SizeZ; z += 7)
        for (var x = 0; x < Settings.SizeX; x += 7)
        {
            same &= Top(again.Grid, x, z) == Top(_world.Grid, x, z);
            differs |= Top(other.Grid, x, z) != Top(_world.Grid, x, z);
        }
        Assert.That(same, Is.True);
        Assert.That(differs, Is.True);
        Assert.That(again.Rivers.Count, Is.EqualTo(_world.Rivers.Count));
    }
}
