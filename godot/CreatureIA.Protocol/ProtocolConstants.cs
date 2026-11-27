namespace CreatureIA.Protocol;

public static class ProtocolConstants
{
    public const ushort Version = 3;
    public const ushort ObservationDimensions = 1124;
    public const ushort ActionDimensions = 13;
    public const int MaxBodyLength = 64 * 1024 * 1024;
}

public enum MessageType : byte
{
    Hello = 1,
    Config = 2,
    Step = 3,
    Act = 4,
    Spawn = 5
}

public enum EventType : byte
{
    Death = 1,
    MateRequest = 2,
    Ate = 3,
    Damaged = 4
}

public enum DeathCauseCode : byte
{
    None = 0,
    Starvation = 1,
    Dehydration = 2,
    Cold = 3,
    Heat = 4,
    HeadInjury = 5,
    TorsoInjury = 6,
    Age = 7,
    Fall = 8,
    Bite = 9
}
