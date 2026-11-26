namespace CreatureIA.Protocol;

public interface IProtocolMessage
{
    MessageType Type { get; }
}

public sealed record HelloMessage(ushort Version, ushort ObservationDimensions, ushort ActionDimensions, ulong WorldSeed) : IProtocolMessage
{
    public MessageType Type => MessageType.Hello;
}

public sealed record ConfigMessage(float TimeScale, ushort PhysicsHz, ushort BrainHz, ushort Population) : IProtocolMessage
{
    public MessageType Type => MessageType.Config;
}

public sealed record StepAgent(uint Id, float[] Observation);
public sealed record ProtocolEvent(EventType Type, byte[] Payload);
public sealed record StepMessage(ulong Tick, IReadOnlyList<StepAgent> Agents, IReadOnlyList<ProtocolEvent> Events) : IProtocolMessage
{
    public MessageType Type => MessageType.Step;
}

public sealed record ActAgent(uint Id, float[] Actions);
public sealed record ActMessage(ulong Tick, IReadOnlyList<ActAgent> Agents) : IProtocolMessage
{
    public MessageType Type => MessageType.Act;
}

public readonly record struct Position3(float X, float Y, float Z);
public sealed record SpawnMessage(uint Id, float[] Traits, Position3? Position) : IProtocolMessage
{
    public MessageType Type => MessageType.Spawn;
}

public sealed record ProtocolFrame(MessageType Type, byte[] Payload);
