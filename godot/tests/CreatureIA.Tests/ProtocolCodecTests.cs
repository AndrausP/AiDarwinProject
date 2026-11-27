using CreatureIA.Protocol;
using NUnit.Framework;

namespace CreatureIA.Tests;

[TestFixture]
public sealed class ProtocolCodecTests
{
    [Test]
    public void Should_MatchGoldenFixture_When_EncodingHello()
    {
        var expected = Convert.FromHexString("0f00000001030069040d000807060504030201");
        var actual = ProtocolCodec.Encode(new HelloMessage(3, 1129, 13, 0x0102030405060708));
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Should_RoundTripStep_When_PayloadIsValid()
    {
        var observations = Enumerable.Range(0, ProtocolConstants.ObservationDimensions).Select(index => index / 1000f).ToArray();
        var original = new StepMessage(42, new[] { new StepAgent(7, observations) }, new[] { new ProtocolEvent(EventType.Ate, new byte[] { 1, 2 }) });
        var decoded = (StepMessage)ProtocolCodec.Decode(ProtocolCodec.Encode(original));
        Assert.Multiple(() =>
        {
            Assert.That(decoded.Tick, Is.EqualTo(42));
            Assert.That(decoded.Agents[0].Id, Is.EqualTo(7));
            Assert.That(decoded.Agents[0].Observation, Is.EqualTo(observations));
            Assert.That(decoded.Events[0].Payload, Is.EqualTo(new byte[] { 1, 2 }));
        });
    }

    [Test]
    public void Should_RejectFrame_When_LengthPrefixDoesNotMatch()
    {
        var frame = ProtocolCodec.Encode(new HelloMessage(3, 1129, 13, 1));
        frame[0]++;
        Assert.Throws<InvalidDataException>(() => ProtocolCodec.Decode(frame));
    }

    [Test]
    public void Should_RejectAction_When_ValueIsNotFinite()
    {
        var actions = new float[10];
        actions[3] = float.NaN;
        var message = new ActMessage(1, new[] { new ActAgent(1, actions) });
        Assert.Throws<InvalidDataException>(() => ProtocolCodec.Encode(message));
    }

    [Test]
    public void Should_RejectAct_When_TickDoesNotMatchStep()
    {
        var step = new StepMessage(1, new[] { new StepAgent(9, new float[ProtocolConstants.ObservationDimensions]) }, Array.Empty<ProtocolEvent>());
        var act = new ActMessage(2, new[] { new ActAgent(9, new float[10]) });
        Assert.Throws<InvalidDataException>(() => ProtocolCodec.ValidateMatchingAct(step, act));
    }

    [Test]
    public void Should_RejectHello_When_DimensionsAreIncompatible()
    {
        var message = new HelloMessage(1, 696, 10, 1);
        Assert.Throws<InvalidDataException>(() => ProtocolCodec.Encode(message));
    }

    [Test]
    public void Should_RoundTripSpawn_When_PositionExists()
    {
        var original = new SpawnMessage(3, new[] { 0.25f, 0.75f }, new Position3(1, 2, 3));
        var decoded = (SpawnMessage)ProtocolCodec.Decode(ProtocolCodec.Encode(original));
        Assert.Multiple(() =>
        {
            Assert.That(decoded.Id, Is.EqualTo(original.Id));
            Assert.That(decoded.Traits, Is.EqualTo(original.Traits));
            Assert.That(decoded.Position, Is.EqualTo(original.Position));
        });
    }
}
