"""Population keeper: answers each DEATH with a SPAWN carrying a brand-new id.

Python owns the creature life cycle (later phases replace this with reproduction and
genomes). Ids are never reused, so every life is a distinct episode for the learner and
the episode recorder.
"""

from __future__ import annotations

import struct

from creature_brain import SIMULATOR_ID_BASE

from .protocol import EventType, Spawn, Step


class Respawner:
    def __init__(self) -> None:
        self._next_id = 1
        self.spawned = 0

    def spawns_for(self, step: Step) -> tuple[Spawn, ...]:
        # Only the brain's own id range counts: simulator-born ids (>= 2**30) live in their own range.
        own = [i for i in step.creature_ids if i < SIMULATOR_ID_BASE]
        if own:
            self._next_id = max(self._next_id, max(own) + 1)
        deaths: list[int] = []
        for event in step.events:
            if event.event_type is EventType.DEATH and len(event.payload) >= 4:
                (creature_id,) = struct.unpack_from("<I", event.payload, 0)
                if creature_id < SIMULATOR_ID_BASE:
                    self._next_id = max(self._next_id, creature_id + 1)
                deaths.append(creature_id)
        spawns = []
        for _ in deaths:
            spawns.append(Spawn(self._next_id, (), None))
            self._next_id += 1
        self.spawned += len(spawns)
        return tuple(spawns)
