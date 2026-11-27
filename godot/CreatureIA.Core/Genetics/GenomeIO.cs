namespace CreatureIA.Core.Genetics;

/// <summary>Binary form of a genome for world snapshots (catalog is the simulator's shared one).</summary>
public static class GenomeIO
{
    public static void Write(BinaryWriter w, Genome genome)
    {
        w.Write((byte)genome.Sex);
        w.Write(genome.Catalog.Count);
        for (var i = 0; i < genome.Catalog.Count; i++)
        {
            w.Write(genome.Maternal[i].Value); w.Write(genome.Maternal[i].Dominance);
            w.Write(genome.Paternal[i].Value); w.Write(genome.Paternal[i].Dominance);
        }
    }

    public static Genome Read(BinaryReader r, GeneCatalog catalog)
    {
        var sex = (Sex)r.ReadByte();
        var count = r.ReadInt32();
        if (count != catalog.Count) throw new InvalidDataException($"genome has {count} loci, catalog {catalog.Count}");
        var maternal = new Allele[count];
        var paternal = new Allele[count];
        for (var i = 0; i < count; i++)
        {
            maternal[i] = new Allele(r.ReadSingle(), r.ReadSingle());
            paternal[i] = new Allele(r.ReadSingle(), r.ReadSingle());
        }
        return new Genome(catalog, maternal, paternal, sex);
    }
}
