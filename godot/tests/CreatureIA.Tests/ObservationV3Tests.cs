using CreatureIA.Core.Simulation;
using CreatureIA.Core.Voxel;
using CreatureIA.Protocol;
using NUnit.Framework;

namespace CreatureIA.Tests;

[TestFixture]
public sealed class ObservationV3Tests
{
    [Test]
    public void Should_HaveContiguousBlocks_When_LayoutIsV3()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ObservationLayout.VisionLength, Is.EqualTo(1008));
            Assert.That(ObservationLayout.CreatureSectorOffset, Is.EqualTo(1008));
            Assert.That(ObservationLayout.OlfactionOffset, Is.EqualTo(1052));
            Assert.That(ObservationLayout.InteroceptionOffset, Is.EqualTo(1077));
            Assert.That(ObservationLayout.GenesOffset, Is.EqualTo(1097));
            Assert.That(ObservationLayout.MotivationsOffset, Is.EqualTo(1120));
            Assert.That(ObservationLayout.MotivationsOffset + ObservationLayout.MotivationsLength, Is.EqualTo(1129));
            Assert.That(ObservationLayout.Total, Is.EqualTo(ProtocolConstants.ObservationDimensions));
            Assert.That(ObservationLayout.ActionCount, Is.EqualTo(ProtocolConstants.ActionDimensions));
        });
    }

    [Test]
    public void Should_KeepScentGradient_When_WaterIsThirtyMetresAway()
    {
        // Same formula as SimTerrain.WaterScent (lake edge at distance d): 1 / (1 + d²).
        static float Scent(float d) => 1f / (1f + d * d);
        var observation = new float[ObservationLayout.Total];

        ObservationLayout.WriteScent(observation, 0, Scent(30f), Scent(29.4f), Scent(30f));

        // v2 wrote nose − body ≈ 4e-5 here (lost in noise); log scale keeps ~2·0.6/30 = 0.04.
        Assert.That(observation[0], Is.GreaterThan(0.1f));
        Assert.That(observation[1], Is.InRange(0.02f, 0.06f));
        Assert.That(observation[2], Is.EqualTo(0f));
    }

    [Test]
    public void Should_TellSeaFromFreshWater_When_Seeing()
    {
        Assert.That(VisionClass.OfBlock(BlockType.SaltWater), Is.EqualTo(VisionClass.SaltWater));
        Assert.That(VisionClass.OfBlock(BlockType.Water), Is.EqualTo(VisionClass.Water));
    }

    [Test]
    public void Should_FillGenesMotivationsAndTime_When_SimObserves()
    {
        var world = new SimWorld(new SimWorldSettings { Seed = 3, Population = 4 });
        var observation = new float[ObservationLayout.Total];

        world.Observe(world.Creatures[0], observation);

        var genes = observation.AsSpan(ObservationLayout.GenesOffset, ObservationLayout.GenesLength).ToArray();
        var motivations = observation.AsSpan(ObservationLayout.MotivationsOffset, ObservationLayout.MotivationsLength).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(genes, Has.All.InRange(0f, 1f));
            Assert.That(genes, Has.Some.GreaterThan(0f));
            Assert.That(motivations[0], Is.InRange(0.5f, 1.5f)); // m_energy around its default 1 (founder variation)
            Assert.That(observation[ObservationLayout.TimeOffset + 2], Is.InRange(0f, 1f));
            Assert.That(observation, Has.All.Matches<float>(float.IsFinite));
        });
    }
}
