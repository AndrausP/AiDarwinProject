namespace CreatureIA.Core.Voxel;

/// <summary>
/// "Sala branca": a 96 × 96 m white room with every example side by side and a name tag on each —
/// trees (back row), shrubs/grass/flowers/pond (middle row), block palette (front row). The open
/// floor in front is where creatures and food plants roam. Display/testing aid, never used in training.
/// </summary>
public static class ShowcaseGenerator
{
    private const int Floor = 1;

    public static VoxelWorld Generate(int seed)
    {
        var settings = new VoxelWorldSettings
        {
            SizeX = 192, SizeY = 96, SizeZ = 192, WaterLevel = Floor, LakeCount = 0, Showcase = true
        };
        var grid = new VoxelGrid(settings.SizeX, settings.SizeY, settings.SizeZ, settings.BlockSize,
            -settings.WorldSizeX * 0.5f, 0f, -settings.WorldSizeZ * 0.5f);
        var lakes = new List<LakeBasin>();
        var world = new VoxelWorld(grid, settings, seed, lakes);

        for (var z = 0; z < settings.SizeZ; z++)
        for (var x = 0; x < settings.SizeX; x++)
        {
            grid.SetRaw(x, 0, z, BlockType.Bedrock);
            grid.SetRaw(x, Floor, z, BlockType.Marble);
            var wall = x == 0 || z == 0 || x == settings.SizeX - 1 || z == settings.SizeZ - 1;
            if (wall)
                for (var y = Floor + 1; y <= Floor + 8; y++) grid.SetRaw(x, y, z, BlockType.Marble);
        }

        // Back row: trees, 12 m apart; the sequoia in the middle.
        var trees = new (TreeSpecies Species, string Name, float X, float R)[]
        {
            (TreeSpecies.Cactus, "Cacto", -38f, 0.9f),
            (TreeSpecies.Acacia, "Acácia", -28f, 0.3f),
            (TreeSpecies.Birch, "Bétula", -17f, 0.6f),
            (TreeSpecies.Sequoia, "Sequoia gigante", -2f, 0.9f),
            (TreeSpecies.Oak, "Carvalho", 13f, 0.7f),
            (TreeSpecies.BigOak, "Carvalho grande", 25f, 0.5f),
            (TreeSpecies.Spruce, "Abeto", 37f, 0.6f)
        };
        const float treeRowZ = -26f;
        foreach (var (species, name, x, r) in trees)
        {
            var (bx, bz) = Block(grid, x, treeRowZ);
            TreeBuilder.Build(grid, species, bx, Floor, bz, r);
            world.Labels.Add((name, x, 2.6f, treeRowZ + 5f));
        }

        // Middle row: shrubs, ground cover and a pond.
        const float midZ = -6f;
        var shrubs = new (BlockType Leaves, string Name, float X)[]
        {
            (BlockType.Leaves, "Arbusto", -38f),
            (BlockType.PineLeaves, "Arbusto (frio)", -30f),
            (BlockType.AcaciaLeaves, "Arbusto (seco)", -22f),
            (BlockType.BirchLeaves, "Arbusto (claro)", -14f)
        };
        foreach (var (leaves, name, x) in shrubs)
        {
            var (bx, bz) = Block(grid, x, midZ);
            TreeBuilder.Shrub(grid, bx, Floor, bz, 0.95f, leaves);
            world.Labels.Add((name, x, 2f, midZ + 3f));
        }
        Patch(grid, -6f, midZ, 3, (gx, gz) => BlockType.TallGrass);
        world.Labels.Add(("Capim alto", -5f, 1.6f, midZ + 3.5f));
        Patch(grid, 4f, midZ, 3, (gx, gz) => (gx + gz) % 2 == 0 ? BlockType.FlowerYellow : BlockType.FlowerRed);
        world.Labels.Add(("Flores", 5f, 1.6f, midZ + 3.5f));

        // Pond: floor replaced by water, rim of sand. Drinkable.
        var (px, pz) = Block(grid, 18f, midZ);
        for (var dz = -6; dz <= 6; dz++)
        for (var dx = -6; dx <= 6; dx++)
        {
            var ring = Math.Max(Math.Abs(dx), Math.Abs(dz));
            grid.SetRaw(px + dx, Floor, pz + dz, ring <= 5 ? BlockType.Water : BlockType.Sand);
        }
        lakes.Add(new LakeBasin(18f + grid.BlockSize * 0.5f, midZ + grid.BlockSize * 0.5f, 2.75f));
        world.Labels.Add(("Lago (beber)", 18f, 1.8f, midZ + 4.5f));

        // Front row: block palette, a 1 × 1 × 2 pillar per solid block type.
        var palette = new (BlockType Block, string Name)[]
        {
            (BlockType.Grass, "Grama"), (BlockType.Dirt, "Terra"), (BlockType.Sand, "Areia"), (BlockType.Stone, "Pedra"),
            (BlockType.Snow, "Neve"), (BlockType.Wood, "Tronco"), (BlockType.BirchWood, "Tronco bétula"),
            (BlockType.RedWood, "Tronco sequoia"), (BlockType.Leaves, "Folhas"), (BlockType.PineLeaves, "Folhas pinho"),
            (BlockType.BirchLeaves, "Folhas bétula"), (BlockType.AcaciaLeaves, "Folhas acácia"), (BlockType.Cactus, "Cacto"),
            (BlockType.Bedrock, "Rocha-mãe")
        };
        const float paletteZ = 10f;
        for (var i = 0; i < palette.Length; i++)
        {
            var x = -39f + i * 6f;
            var (bx, bz) = Block(grid, x, paletteZ);
            for (var y = Floor + 1; y <= Floor + 2; y++)
            for (var dz = 0; dz < 2; dz++)
            for (var dx = 0; dx < 2; dx++)
                grid.SetRaw(bx + dx, y, bz + dz, palette[i].Block);
            world.Labels.Add((palette[i].Name, x + 0.5f, 2.4f, paletteZ + 0.5f));
        }

        world.Labels.Add(("Área livre: criaturas e plantas de comida", 0f, 3f, 24f));
        return world;
    }

    private static (int X, int Z) Block(VoxelGrid grid, float wx, float wz)
    {
        var (x, _, z) = grid.WorldToBlock(wx, 0f, wz);
        return (x, z);
    }

    private static void Patch(VoxelGrid grid, float wx, float wz, int half, Func<int, int, BlockType> block)
    {
        var (cx, cz) = Block(grid, wx, wz);
        for (var dz = -half; dz <= half; dz++)
        for (var dx = -half; dx <= half; dx++)
            grid.SetRaw(cx + dx, Floor + 1, cz + dz, block(dx, dz));
    }
}
