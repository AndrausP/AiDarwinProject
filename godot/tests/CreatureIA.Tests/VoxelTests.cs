using CreatureIA.Core.Voxel;
using NUnit.Framework;

namespace CreatureIA.Tests;

[TestFixture]
public sealed class VoxelTests
{
    private static VoxelGrid FlatGrid(int groundTop = 3)
    {
        var grid = new VoxelGrid(32, 16, 32, 0.5f, 0f, 0f, 0f);
        for (var z = 0; z < 32; z++)
        for (var x = 0; x < 32; x++)
        for (var y = 0; y <= groundTop; y++)
            grid.SetRaw(x, y, z, y == groundTop ? BlockType.Grass : BlockType.Stone);
        return grid;
    }

    [Test]
    public void Should_ReadBackBlock_When_SetAcrossChunkBoundary()
    {
        var grid = new VoxelGrid(32, 16, 32, 0.5f, -8f, 0f, -8f);
        grid.SetRaw(15, 3, 16, BlockType.Wood);
        Assert.Multiple(() =>
        {
            Assert.That(grid.Get(15, 3, 16), Is.EqualTo(BlockType.Wood));
            Assert.That(grid.Get(16, 3, 16), Is.EqualTo(BlockType.Air));
            Assert.That(grid.Get(0, -1, 0), Is.EqualTo(BlockType.Bedrock));
            Assert.That(grid.AllocatedChunkCount, Is.EqualTo(1));
            Assert.That(grid.WorldToBlock(-8f + 7.75f, 1.6f, -8f + 8.1f), Is.EqualTo((15, 3, 16)));
        });
    }

    [Test]
    public void Should_MarkNeighbourChunkDirty_When_EditOnChunkFace()
    {
        var grid = FlatGrid();
        grid.TakeDirtyChunks();
        Assert.That(grid.Set(16, 3, 5, BlockType.Air), Is.True);
        var dirty = grid.TakeDirtyChunks();
        Assert.That(dirty, Is.EquivalentTo(new[] { grid.ChunkIndex(1, 0, 0), grid.ChunkIndex(0, 0, 0) }));
    }

    [Test]
    public void Should_EmitSixQuads_When_SingleCube()
    {
        var grid = new VoxelGrid(16, 16, 16, 0.5f, 0f, 0f, 0f);
        grid.SetRaw(4, 4, 4, BlockType.Stone);
        var mesh = GreedyMesher.Build(grid, 0, 0, 0);
        Assert.That(mesh.Solid.QuadCount, Is.EqualTo(6));
        Assert.That(mesh.Water.IsEmpty, Is.True);
    }

    [Test]
    public void Should_MergeFaces_When_BlocksFormSlab()
    {
        var grid = new VoxelGrid(16, 16, 16, 0.5f, 0f, 0f, 0f);
        for (var z = 0; z < 16; z++)
        for (var x = 0; x < 16; x++)
            grid.SetRaw(x, 0, z, BlockType.Grass);
        var mesh = GreedyMesher.Build(grid, 0, 0, 0);
        // Top, bottom and four sides: one quad each. The bottom face points at bedrock (outside) and is culled.
        Assert.That(mesh.Solid.QuadCount, Is.EqualTo(5));
    }

    [Test]
    public void Should_WindClockwiseFromOutside_When_MeshingTopFace()
    {
        var grid = new VoxelGrid(16, 16, 16, 1f, 0f, 0f, 0f);
        grid.SetRaw(2, 2, 2, BlockType.Stone);
        var mesh = GreedyMesher.Build(grid, 0, 0, 0).Solid;
        var p = mesh.Positions;
        for (var t = 0; t < mesh.Indices.Count; t += 3)
        {
            int a = mesh.Indices[t] * 3, b = mesh.Indices[t + 1] * 3, c = mesh.Indices[t + 2] * 3;
            var e1 = (X: p[b] - p[a], Y: p[b + 1] - p[a + 1], Z: p[b + 2] - p[a + 2]);
            var e2 = (X: p[c] - p[a], Y: p[c + 1] - p[a + 1], Z: p[c + 2] - p[a + 2]);
            var cross = (X: e1.Y * e2.Z - e1.Z * e2.Y, Y: e1.Z * e2.X - e1.X * e2.Z, Z: e1.X * e2.Y - e1.Y * e2.X);
            var n = mesh.Indices[t] * 3;
            var dot = cross.X * mesh.Normals[n] + cross.Y * mesh.Normals[n + 1] + cross.Z * mesh.Normals[n + 2];
            Assert.That(dot, Is.LessThan(0f), "Godot front faces are clockwise seen from outside.");
        }
    }

    [Test]
    public void Should_HideFacesBetweenSolids_And_DrawWaterSurfaceOnly()
    {
        var grid = new VoxelGrid(16, 16, 16, 0.5f, 0f, 0f, 0f);
        grid.SetRaw(3, 3, 3, BlockType.Water);
        grid.SetRaw(3, 2, 3, BlockType.Sand);
        var mesh = GreedyMesher.Build(grid, 0, 0, 0);
        // Water: all 6 faces touch air except the bottom (sand) -> 5. Sand: top faces water (drawn), 5 air faces.
        Assert.Multiple(() =>
        {
            Assert.That(mesh.Water.QuadCount, Is.EqualTo(5));
            Assert.That(mesh.Solid.QuadCount, Is.EqualTo(6));
        });
    }

    [Test]
    public void Should_HitBlockAtExactDistance_When_RayAlongAxis()
    {
        var grid = new VoxelGrid(16, 16, 16, 0.5f, 0f, 0f, 0f);
        grid.SetRaw(10, 2, 2, BlockType.Wood);
        var hit = VoxelRaycast.Cast(grid, 1.25f, 1.25f, 1.25f, 1f, 0f, 0f, 20f);
        Assert.Multiple(() =>
        {
            Assert.That(hit.Hit, Is.True);
            Assert.That(hit.Block, Is.EqualTo(BlockType.Wood));
            Assert.That(hit.Distance, Is.EqualTo(5f - 1.25f).Within(1e-4f));
            Assert.That((hit.NormalX, hit.NormalY, hit.NormalZ), Is.EqualTo((-1, 0, 0)));
        });
    }

    [Test]
    public void Should_MissAndRespectRange_When_NothingWithinMaxDistance()
    {
        var grid = new VoxelGrid(16, 16, 16, 0.5f, 0f, 0f, 0f);
        grid.SetRaw(15, 2, 2, BlockType.Stone);
        var hit = VoxelRaycast.Cast(grid, 1.25f, 1.25f, 1.25f, 1f, 0f, 0f, 3f);
        Assert.That(hit.Hit, Is.False);
    }

    [Test]
    public void Should_HitGround_When_RayPointsDiagonallyDown()
    {
        var grid = FlatGrid();
        var hit = VoxelRaycast.Cast(grid, 8f, 4f, 8f, 1f, -1f, 0f, 20f);
        // Ground top at y = 2.0; eye 2 m above -> 2 m forward, distance 2·√2.
        Assert.Multiple(() =>
        {
            Assert.That(hit.Block, Is.EqualTo(BlockType.Grass));
            Assert.That(hit.Distance, Is.EqualTo(2f * MathF.Sqrt(2f)).Within(1e-3f));
            Assert.That(hit.NormalY, Is.EqualTo(1));
        });
    }

    [Test]
    public void Should_HitCylinderSideAndCap_When_RayCrosses()
    {
        // Side: eye at 1.1 m, horizontal ray, bush 0..1.4 m radius 0.45 at 5 m.
        var side = VisionGeometry.RayVerticalCylinder(0f, 1.1f, 0f, 1f, 0f, 0f, 5f, 0f, 0f, 0.45f, 1.4f, 25f);
        // Cap: ray looking straight down onto a 1 m tall cylinder from 3 m.
        var cap = VisionGeometry.RayVerticalCylinder(0f, 3f, 0f, 0f, -1f, 0f, 0.1f, 0f, 0f, 0.45f, 1f, 25f);
        // Over the top: horizontal ray at 2 m passes above a 1.4 m bush.
        var over = VisionGeometry.RayVerticalCylinder(0f, 2f, 0f, 1f, 0f, 0f, 5f, 0f, 0f, 0.45f, 1.4f, 25f);
        var behind = VisionGeometry.RayVerticalCylinder(0f, 1f, 0f, -1f, 0f, 0f, 5f, 0f, 0f, 0.45f, 1.4f, 25f);
        Assert.Multiple(() =>
        {
            Assert.That(side, Is.EqualTo(4.55f).Within(1e-4f));
            Assert.That(cap, Is.EqualTo(2f).Within(1e-4f));
            Assert.That(over, Is.Null);
            Assert.That(behind, Is.Null);
        });
    }

    [Test]
    public void Should_BuildGiantTrunk_When_SequoiaPlanted()
    {
        var grid = new VoxelGrid(32, 96, 32, 0.5f, 0f, 0f, 0f);
        for (var z = 0; z < 32; z++)
        for (var x = 0; x < 32; x++)
            grid.SetRaw(x, 10, z, BlockType.Grass);
        TreeBuilder.Build(grid, TreeSpecies.Sequoia, 14, 10, 14, 0.2f);
        var height = TreeBuilder.SequoiaHeight(grid, 10, 0.2f);
        Assert.Multiple(() =>
        {
            Assert.That(height, Is.GreaterThanOrEqualTo(40), "≥ 20 m");
            foreach (var (dx, dz) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
                Assert.That(grid.Get(14 + dx, 10 + height / 2, 14 + dz), Is.EqualTo(BlockType.RedWood), "2×2 trunk");
            Assert.That(grid.Get(14, 12, 14 - 3), Is.EqualTo(BlockType.Air), "no foliage near the ground");
            Assert.That(grid.TopBlockY(18, 14), Is.GreaterThan(10 + height / 2), "crown spreads around the top");
        });
    }

    [Test]
    public void Should_PassThroughAndDrawCrossed_When_TallGrass()
    {
        var grid = FlatGrid();
        grid.SetRaw(10, 4, 10, BlockType.TallGrass);
        var mesh = GreedyMesher.Build(grid, 0, 0, 0);
        var hit = VoxelRaycast.Cast(grid, 0.3f, 2.2f, 5.25f, 1f, 0f, 0f, 20f);
        Assert.Multiple(() =>
        {
            Assert.That(BlockTypes.IsSolid(BlockType.TallGrass), Is.False);
            Assert.That(hit.Hit, Is.False, "vision ignores grass");
            // Ground top under the grass stays visible: flat grid top = 1 merged quad... plus 4 crossed quads.
            Assert.That(mesh.Solid.Positions.Count / 3 % 4, Is.Zero);
            Assert.That(VoxelPhysics.BoxBlocked(grid, 5.25f, 2.001f, 5.25f, new VoxelBodyShape(0.3f, 0.9f, 0.55f)), Is.False);
        });
    }

    [Test]
    public void Should_GrowSeveralSpecies_When_FullWorldGenerated()
    {
        var world = VoxelWorldGenerator.Generate(0);
        var counts = new Dictionary<BlockType, int>();
        var grid = world.Grid;
        for (var z = 0; z < grid.SizeZ; z += 2)
        for (var x = 0; x < grid.SizeX; x += 2)
        for (var y = 0; y < grid.SizeY; y++)
        {
            var b = grid.Get(x, y, z);
            counts[b] = counts.GetValueOrDefault(b) + 1;
        }
        TestContext.Out.WriteLine(string.Join(", ", counts.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
        Assert.Multiple(() =>
        {
            foreach (var block in new[] { BlockType.Wood, BlockType.BirchWood, BlockType.PineLeaves, BlockType.TallGrass })
                Assert.That(counts.GetValueOrDefault(block), Is.GreaterThan(0), block.ToString());
        });
    }

    [Test]
    public void Should_LayOutEveryExample_When_ShowcaseGenerated()
    {
        var world = ShowcaseGenerator.Generate(0);
        var grid = world.Grid;
        var found = new HashSet<BlockType>();
        for (var z = 0; z < grid.SizeZ; z++)
        for (var x = 0; x < grid.SizeX; x++)
        for (var y = 0; y < grid.SizeY; y++)
            found.Add(grid.Get(x, y, z));
        Assert.Multiple(() =>
        {
            foreach (var block in Enum.GetValues<BlockType>()) Assert.That(found, Does.Contain(block), block.ToString());
            Assert.That(world.Labels.Count, Is.GreaterThanOrEqualTo(20));
            Assert.That(world.IsNearWater(18f, 1f, -6f + 3.5f, 1.5f), Is.True, "pond is drinkable from its rim");
            Assert.That(world.Climate(30f, 30f).TemperatureCelsius, Is.EqualTo(22f));
        });
    }

    [Test]
    public void Should_RemoveBlockOnlyAfterEnoughEffort_When_Digging()
    {
        var dig = new DigProgress();
        var done = false;
        var ticks = 0;
        while (!done && ticks < 1000) { done = dig.Work(1, 2, 3, BlockType.Dirt, 1f / 60f); ticks++; }
        Assert.Multiple(() =>
        {
            Assert.That(ticks, Is.EqualTo(48).Within(1), "0.8 s at 60 Hz");
            Assert.That(dig.Work(1, 2, 3, BlockType.Bedrock, 10f), Is.False);
        });
    }

    [Test]
    public void Should_RestartProgress_When_TargetBlockChanges()
    {
        var dig = new DigProgress();
        for (var i = 0; i < 40; i++) dig.Work(1, 2, 3, BlockType.Dirt, 1f / 60f);
        Assert.That(dig.Work(1, 2, 4, BlockType.Dirt, 1f / 60f), Is.False);
        Assert.That(dig.Fraction, Is.LessThan(0.05f));
    }

    private static readonly VoxelBodyShape Animal = new(0.3f, 0.9f, 0.55f);

    private static VoxelBodyState Settle(VoxelGrid grid, VoxelBodyState body, int ticks = 120)
    {
        for (var i = 0; i < ticks; i++)
        {
            body.VelocityY -= VoxelPhysics.Gravity / 60f;
            VoxelPhysics.Move(grid, ref body, Animal, 1f / 60f);
        }
        return body;
    }

    [Test]
    public void Should_LandOnGroundTop_When_Falling()
    {
        var grid = FlatGrid();
        var body = Settle(grid, new VoxelBodyState { X = 8f, Y = 6f, Z = 8f });
        Assert.Multiple(() =>
        {
            Assert.That(body.Y, Is.EqualTo(2f).Within(0.01f));
            Assert.That(body.OnFloor, Is.True);
        });
    }

    [Test]
    public void Should_StopAtWall_When_WallTallerThanStep()
    {
        var grid = FlatGrid();
        for (var y = 4; y <= 7; y++)
        for (var z = 0; z < 32; z++)
            grid.SetRaw(20, y, z, BlockType.Stone);
        var body = Settle(grid, new VoxelBodyState { X = 8f, Y = 2.1f, Z = 8f });
        VoxelMoveResult result = default;
        for (var i = 0; i < 180; i++)
        {
            body.VelocityX = 3f;
            body.VelocityY -= VoxelPhysics.Gravity / 60f;
            result = VoxelPhysics.Move(grid, ref body, Animal, 1f / 60f);
        }
        Assert.Multiple(() =>
        {
            Assert.That(body.X + Animal.HalfWidth, Is.EqualTo(10f).Within(0.01f));
            Assert.That(result.HitWall, Is.True);
            Assert.That(body.Y, Is.EqualTo(2f).Within(0.01f));
        });
    }

    [Test]
    public void Should_StepUpOneBlock_When_WalkingIntoLedge()
    {
        var grid = FlatGrid();
        for (var z = 0; z < 32; z++)
        for (var x = 20; x < 32; x++)
            grid.SetRaw(x, 4, z, BlockType.Grass);
        var body = Settle(grid, new VoxelBodyState { X = 8f, Y = 2.1f, Z = 8f });
        for (var i = 0; i < 180; i++)
        {
            body.VelocityX = 3f;
            body.VelocityY -= VoxelPhysics.Gravity / 60f;
            VoxelPhysics.Move(grid, ref body, Animal, 1f / 60f);
        }
        Assert.Multiple(() =>
        {
            Assert.That(body.X, Is.GreaterThan(10.5f));
            Assert.That(body.Y, Is.EqualTo(2.5f).Within(0.01f));
        });
    }

    [Test]
    public void Should_KeepBodyInsideMap_When_WalkingPastEdge()
    {
        var grid = FlatGrid();
        var body = Settle(grid, new VoxelBodyState { X = 14f, Y = 2.1f, Z = 8f });
        for (var i = 0; i < 120; i++)
        {
            body.VelocityX = 5f;
            body.VelocityY -= VoxelPhysics.Gravity / 60f;
            VoxelPhysics.Move(grid, ref body, Animal, 1f / 60f);
        }
        Assert.That(body.X + Animal.HalfWidth, Is.LessThanOrEqualTo(16f));
    }

    [Test, Explicit("Performance probe on the full 384 × 384 × 48 m world; run on demand.")]
    public void Should_GenerateAndMeshFullWorld_When_Profiling()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var world = VoxelWorldGenerator.Generate(0);
        var generateMs = watch.ElapsedMilliseconds;
        watch.Restart();
        var grid = world.Grid;
        long solidQuads = 0, waterQuads = 0;
        for (var i = 0; i < grid.ChunkCount; i++)
        {
            if (grid.IsChunkEmpty(i)) continue;
            var (cx, cy, cz) = grid.ChunkCoordinates(i);
            var mesh = GreedyMesher.Build(grid, cx, cy, cz);
            solidQuads += mesh.Solid.QuadCount;
            waterQuads += mesh.Water.QuadCount;
        }
        var meshMs = watch.ElapsedMilliseconds;
        watch.Restart();
        var hits = 0;
        var rng = new Random(1);
        for (var i = 0; i < 100_000; i++)
        {
            var x = (float)(rng.NextDouble() * 300 - 150);
            var z = (float)(rng.NextDouble() * 300 - 150);
            var y = grid.SurfaceHeightAt(x, z) + 1f;
            var a = (float)(rng.NextDouble() * Math.Tau);
            if (VoxelRaycast.Cast(grid, x, y, z, MathF.Cos(a), -0.2f, MathF.Sin(a), 25f).Hit) hits++;
        }
        var rayMs = watch.ElapsedMilliseconds;
        TestContext.Out.WriteLine($"generate {generateMs} ms, chunks {grid.AllocatedChunkCount}/{grid.ChunkCount}, mesh {meshMs} ms ({solidQuads} solid + {waterQuads} water quads), 100k rays {rayMs} ms ({hits} hits), lakes {world.Lakes.Count}");
        Assert.Pass();
    }

    [Test]
    public void Should_GenerateSameWorld_When_SameSeed()
    {
        var settings = new VoxelWorldSettings { SizeX = 128, SizeZ = 128, SizeY = 64, WaterLevel = 16, LakeCount = 2 };
        var a = VoxelWorldGenerator.Generate(7, settings);
        var b = VoxelWorldGenerator.Generate(7, settings);
        var c = VoxelWorldGenerator.Generate(8, settings);
        var same = true;
        var differs = false;
        for (var z = 0; z < 128; z += 3)
        for (var x = 0; x < 128; x += 3)
        {
            same &= a.Grid.TopBlockY(x, z) == b.Grid.TopBlockY(x, z) && a.Grid.Get(x, a.Grid.TopBlockY(x, z), z) == b.Grid.Get(x, b.Grid.TopBlockY(x, z), z);
            differs |= a.Grid.TopBlockY(x, z) != c.Grid.TopBlockY(x, z);
        }
        Assert.Multiple(() =>
        {
            Assert.That(same, Is.True);
            Assert.That(differs, Is.True);
        });
    }

    [Test]
    public void Should_HaveBedrockFloorAndDrinkableLakes_When_Generated()
    {
        var world = VoxelWorldGenerator.Generate(3, new VoxelWorldSettings { SizeX = 256, SizeZ = 256, SizeY = 64, WaterLevel = 16, LakeCount = 3 });
        var grid = world.Grid;
        Assert.That(grid.Get(10, 0, 10), Is.EqualTo(BlockType.Bedrock));
        Assert.That(world.Lakes, Is.Not.Empty);
        foreach (var lake in world.Lakes)
        {
            var (bx, _, bz) = grid.WorldToBlock(lake.CenterX, 0f, lake.CenterZ);
            Assert.That(grid.Get(bx, world.Settings.WaterLevel, bz), Is.EqualTo(BlockType.Water), "lake centre holds water");
            // Standing on the shore, a little outside the radius, the lake is within drinking reach somewhere along the rim.
            var reachable = false;
            for (var a = 0; a < 32 && !reachable; a++)
            {
                var ang = a * MathF.Tau / 32f;
                for (var r = lake.RadiusMeters * 0.5f; r < lake.RadiusMeters * 1.6f && !reachable; r += 0.25f)
                {
                    var wx = lake.CenterX + MathF.Cos(ang) * r;
                    var wz = lake.CenterZ + MathF.Sin(ang) * r;
                    var (cx, _, cz) = grid.WorldToBlock(wx, 0f, wz);
                    var top = grid.TopBlockY(cx, cz);
                    if (grid.Get(cx, top, cz) == BlockType.Water) continue;
                    reachable = world.IsNearWater(wx, grid.SurfaceHeightAt(wx, wz), wz, 1.5f);
                }
            }
            Assert.That(reachable, Is.True, "a shore point can drink");
        }
    }
}
