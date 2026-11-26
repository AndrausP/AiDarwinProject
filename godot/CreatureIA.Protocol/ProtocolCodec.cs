namespace CreatureIA.Protocol;

public static class ProtocolCodec
{
    public static byte[] Encode(IProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        switch (message)
        {
            case HelloMessage value: Write(writer, value); break;
            case ConfigMessage value: Write(writer, value); break;
            case StepMessage value: Write(writer, value); break;
            case ActMessage value: Write(writer, value); break;
            case SpawnMessage value: Write(writer, value); break;
            default: throw new NotSupportedException($"Unsupported message {message.GetType().Name}.");
        }

        return FrameCodec.Encode(message.Type, stream.ToArray());
    }

    public static IProtocolMessage Decode(ReadOnlySpan<byte> bytes)
    {
        var frame = FrameCodec.Decode(bytes);
        using var stream = new MemoryStream(frame.Payload, writable: false);
        using var reader = new BinaryReader(stream);
        IProtocolMessage message = frame.Type switch
        {
            MessageType.Hello => ReadHello(reader),
            MessageType.Config => ReadConfig(reader),
            MessageType.Step => ReadStep(reader),
            MessageType.Act => ReadAct(reader),
            MessageType.Spawn => ReadSpawn(reader),
            _ => throw new InvalidDataException("Unknown message type.")
        };
        if (stream.Position != stream.Length) throw new InvalidDataException("Message has trailing payload bytes.");
        return message;
    }

    public static void ValidateMatchingAct(StepMessage step, ActMessage act)
    {
        if (step.Tick != act.Tick) throw new InvalidDataException("ACT tick does not match STEP tick.");
        var stepIds = step.Agents.Select(agent => agent.Id).Order().ToArray();
        var actIds = act.Agents.Select(agent => agent.Id).Order().ToArray();
        if (!stepIds.SequenceEqual(actIds)) throw new InvalidDataException("ACT agent IDs do not match STEP agent IDs.");
    }

    private static void Write(BinaryWriter writer, HelloMessage value)
    {
        ValidateHello(value);
        writer.Write(value.Version);
        writer.Write(value.ObservationDimensions);
        writer.Write(value.ActionDimensions);
        writer.Write(value.WorldSeed);
    }

    private static HelloMessage ReadHello(BinaryReader reader)
    {
        var message = new HelloMessage(reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt64());
        ValidateHello(message);
        return message;
    }

    private static void Write(BinaryWriter writer, ConfigMessage value)
    {
        RequireFinite(value.TimeScale, nameof(value.TimeScale));
        writer.Write(value.TimeScale);
        writer.Write(value.PhysicsHz);
        writer.Write(value.BrainHz);
        writer.Write(value.Population);
    }

    private static ConfigMessage ReadConfig(BinaryReader reader) => new(ReadFinite(reader), reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16());

    private static void Write(BinaryWriter writer, StepMessage value)
    {
        if (value.Agents.Count > ushort.MaxValue || value.Events.Count > ushort.MaxValue) throw new InvalidDataException("Collection exceeds protocol limit.");
        EnsureUniqueIds(value.Agents.Select(agent => agent.Id));
        writer.Write(value.Tick);
        writer.Write((ushort)value.Agents.Count);
        foreach (var agent in value.Agents)
        {
            ValidateVector(agent.Observation, ProtocolConstants.ObservationDimensions, nameof(agent.Observation));
            writer.Write(agent.Id);
            foreach (var observation in agent.Observation) writer.Write(observation);
        }
        writer.Write((ushort)value.Events.Count);
        foreach (var protocolEvent in value.Events)
        {
            if (!Enum.IsDefined(protocolEvent.Type)) throw new InvalidDataException("Unknown event type.");
            if (protocolEvent.Payload.Length > ushort.MaxValue) throw new InvalidDataException("Event payload exceeds protocol limit.");
            writer.Write((byte)protocolEvent.Type);
            writer.Write((ushort)protocolEvent.Payload.Length);
            writer.Write(protocolEvent.Payload);
        }
    }

    private static StepMessage ReadStep(BinaryReader reader)
    {
        var tick = reader.ReadUInt64();
        var count = reader.ReadUInt16();
        var agents = new StepAgent[count];
        for (var index = 0; index < count; index++) agents[index] = new StepAgent(reader.ReadUInt32(), ReadVector(reader, ProtocolConstants.ObservationDimensions));
        EnsureUniqueIds(agents.Select(agent => agent.Id));
        var eventCount = reader.ReadUInt16();
        var events = new ProtocolEvent[eventCount];
        for (var index = 0; index < eventCount; index++)
        {
            var eventType = (EventType)reader.ReadByte();
            if (!Enum.IsDefined(eventType)) throw new InvalidDataException("Unknown event type.");
            var length = reader.ReadUInt16();
            var payload = reader.ReadBytes(length);
            if (payload.Length != length) throw new EndOfStreamException();
            events[index] = new ProtocolEvent(eventType, payload);
        }
        return new StepMessage(tick, agents, events);
    }

    private static void Write(BinaryWriter writer, ActMessage value)
    {
        if (value.Agents.Count > ushort.MaxValue) throw new InvalidDataException("Collection exceeds protocol limit.");
        EnsureUniqueIds(value.Agents.Select(agent => agent.Id));
        writer.Write(value.Tick);
        writer.Write((ushort)value.Agents.Count);
        foreach (var agent in value.Agents)
        {
            ValidateVector(agent.Actions, ProtocolConstants.ActionDimensions, nameof(agent.Actions));
            writer.Write(agent.Id);
            foreach (var action in agent.Actions) writer.Write(action);
        }
    }

    private static ActMessage ReadAct(BinaryReader reader)
    {
        var tick = reader.ReadUInt64();
        var count = reader.ReadUInt16();
        var agents = new ActAgent[count];
        for (var index = 0; index < count; index++) agents[index] = new ActAgent(reader.ReadUInt32(), ReadVector(reader, ProtocolConstants.ActionDimensions));
        EnsureUniqueIds(agents.Select(agent => agent.Id));
        return new ActMessage(tick, agents);
    }

    private static void Write(BinaryWriter writer, SpawnMessage value)
    {
        if (value.Traits.Length > ushort.MaxValue) throw new InvalidDataException("Trait collection exceeds protocol limit.");
        ValidateVector(value.Traits, value.Traits.Length, nameof(value.Traits));
        writer.Write(value.Id);
        writer.Write((ushort)value.Traits.Length);
        foreach (var trait in value.Traits) writer.Write(trait);
        writer.Write((byte)(value.Position.HasValue ? 1 : 0));
        if (value.Position is { } position)
        {
            RequireFinite(position.X, nameof(position.X));
            RequireFinite(position.Y, nameof(position.Y));
            RequireFinite(position.Z, nameof(position.Z));
            writer.Write(position.X);
            writer.Write(position.Y);
            writer.Write(position.Z);
        }
    }

    private static SpawnMessage ReadSpawn(BinaryReader reader)
    {
        var id = reader.ReadUInt32();
        var traits = ReadVector(reader, reader.ReadUInt16());
        var hasPosition = reader.ReadByte();
        if (hasPosition > 1) throw new InvalidDataException("Invalid position flag.");
        Position3? position = hasPosition == 1 ? new Position3(ReadFinite(reader), ReadFinite(reader), ReadFinite(reader)) : null;
        return new SpawnMessage(id, traits, position);
    }

    private static float[] ReadVector(BinaryReader reader, int length)
    {
        var values = new float[length];
        for (var index = 0; index < length; index++) values[index] = ReadFinite(reader);
        return values;
    }

    private static void ValidateVector(float[]? values, int expectedLength, string name)
    {
        if (values is null || values.Length != expectedLength) throw new InvalidDataException($"{name} must contain exactly {expectedLength} values.");
        foreach (var value in values) RequireFinite(value, name);
    }

    private static float ReadFinite(BinaryReader reader)
    {
        var value = reader.ReadSingle();
        RequireFinite(value, "payload");
        return value;
    }

    private static void RequireFinite(float value, string name)
    {
        if (!float.IsFinite(value)) throw new InvalidDataException($"{name} must contain only finite values.");
    }

    private static void EnsureUniqueIds(IEnumerable<uint> ids)
    {
        var seen = new HashSet<uint>();
        if (ids.Any(id => !seen.Add(id))) throw new InvalidDataException("Agent IDs must be unique.");
    }

    private static void ValidateHello(HelloMessage message)
    {
        if (message.Version != ProtocolConstants.Version
            || message.ObservationDimensions != ProtocolConstants.ObservationDimensions
            || message.ActionDimensions != ProtocolConstants.ActionDimensions)
            throw new InvalidDataException("HELLO contract version or dimensions are incompatible.");
    }
}
