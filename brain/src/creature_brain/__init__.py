"""Python brain runtime for Criatura IA."""

OBSERVATION_DIM = 1124
ACTION_DIM = 13
PROTOCOL_VERSION = 3
# Protocol v3 offsets (contracts/protocol-v3.json; CreatureIA.Core.Simulation.ObservationLayout).
PROPRIOCEPTION_OFFSET = 1067
INTEROCEPTION_OFFSET = 1077

__all__ = ["OBSERVATION_DIM", "ACTION_DIM", "PROTOCOL_VERSION", "PROPRIOCEPTION_OFFSET", "INTEROCEPTION_OFFSET"]

