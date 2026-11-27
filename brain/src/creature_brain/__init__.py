"""Python brain runtime for Criatura IA."""

OBSERVATION_DIM = 1130
ACTION_DIM = 13
PROTOCOL_VERSION = 3
# Protocol v3 offsets (contracts/protocol-v3.json; CreatureIA.Core.Simulation.ObservationLayout).
PROPRIOCEPTION_OFFSET = 1067
INTEROCEPTION_OFFSET = 1077
MOTIVATIONS_OFFSET = 1121  # 9 individual reward weights (motivation genes)
MOTIVATIONS_DIM = 9
# Ids from 2**30 up are created by the simulator (births); the brain's SPAWN ids stay below.
SIMULATOR_ID_BASE = 1 << 30

__all__ = ["OBSERVATION_DIM", "ACTION_DIM", "PROTOCOL_VERSION", "PROPRIOCEPTION_OFFSET", "INTEROCEPTION_OFFSET",
           "MOTIVATIONS_OFFSET", "MOTIVATIONS_DIM", "SIMULATOR_ID_BASE"]

