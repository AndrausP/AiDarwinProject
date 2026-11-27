"""Episode recorder: turns the live STEP stream into benchmark JSONL lines.

It is a passive bridge observer. Like the reward, it reads only what the brain
legitimately receives (interoception slice and STEP events), never world state,
so the random and PPO runs are measured through the exact same lens.

One line per life, in the schema consumed by ``creature_brain.benchmarks.baseline``.
"""

from __future__ import annotations

from dataclasses import dataclass
import json
import struct
import time
from typing import TextIO

from creature_brain.bridge.protocol import EventType, Step

from creature_brain import INTEROCEPTION_OFFSET, PROPRIOCEPTION_OFFSET  # observation v3
ENERGY_INDEX = INTEROCEPTION_OFFSET
WATER_INDEX = INTEROCEPTION_OFFSET + 1
FATIGUE_INDEX = INTEROCEPTION_OFFSET + 6
SLEEPING_INDEX = PROPRIOCEPTION_OFFSET + 6  # proprioception: sleeping flag
# "High fatigue" (T1.3): twice the reward's fatigue setpoint (0.15). Movement halves only above 0.8,
# which a short-lived random walker never reaches, so 0.3 keeps the metric defined for the baseline.
HIGH_FATIGUE_THRESHOLD = 0.3
BRAIN_SECONDS_PER_TICK = 0.1

# Mirrors CreatureIA.Core.Physiology.DeathCause (byte enum).
DEATH_CAUSES = {
    0: "none",
    1: "starvation",
    2: "dehydration",
    3: "cold",
    4: "heat",
    5: "head_injury",
    6: "torso_injury",
    7: "age",
    8: "fall",
    9: "bite",
}
CENSORED_CAUSE = "alive_at_horizon"


@dataclass(slots=True)
class _Life:
    first_tick: int
    last_tick: int
    water: float
    energy_low: bool
    water_low: bool
    low_energy_steps: int = 0
    ate_while_low_energy: int = 0
    low_water_steps: int = 0
    drank_while_low_water: int = 0
    fatigue_high: bool = False
    high_fatigue_steps: int = 0
    slept_while_tired: int = 0


class EpisodeRecorder:
    """Callable STEP observer; pass it to ``LockstepServer(observers=[...])``.

    Consumption between ticks ``t-1`` and ``t`` is attributed to the deficit state
    observed at ``t-1`` (the state the creature acted on), so a response can never
    be credited without a matching deficit opportunity.
    """

    def __init__(
        self,
        sink: TextIO,
        *,
        policy: str,
        seed: int,
        config_id: str = "default",
        horizon: int = 0,
        low_threshold: float = 0.35,
        drink_epsilon: float = 1e-5,
        clock=time.perf_counter,
    ) -> None:
        if policy not in ("random", "ppo"):
            raise ValueError("policy must be random or ppo")
        self._sink = sink
        self._policy = policy
        self._seed = seed
        self._config_id = config_id
        self._horizon = horizon
        self._low_threshold = low_threshold
        self._drink_epsilon = drink_epsilon
        self._clock = clock
        self._lives: dict[int, _Life] = {}
        self._previous_tick: int | None = None
        self._first_tick: int | None = None
        self._started_at: float | None = None
        self.tick_loss = 0
        self.episodes_written = 0

    def __call__(self, step: Step) -> None:
        if self._previous_tick is not None and step.tick != self._previous_tick + 1:
            self.tick_loss += max(0, step.tick - self._previous_tick - 1)
        if self._first_tick is None:
            self._first_tick = step.tick
            self._started_at = self._clock()
        self._previous_tick = step.tick

        ate_ids, deaths = self._parse_events(step)
        for creature_id, observation in zip(step.creature_ids, step.observations, strict=True):
            self._observe(step.tick, creature_id, observation, creature_id in ate_ids)
        for creature_id, cause in deaths.items():
            life = self._lives.pop(creature_id, None)
            if life is not None:
                self._write(creature_id, life, step.tick, DEATH_CAUSES.get(cause, "unknown"))

    def close(self) -> None:
        """Flush creatures still alive at the horizon as right-censored lives."""
        for creature_id, life in sorted(self._lives.items()):
            self._write(creature_id, life, life.last_tick + 1, CENSORED_CAUSE)
        self._lives.clear()
        self._sink.flush()

    def _observe(self, tick: int, creature_id: int, observation, ate: bool) -> None:
        energy = float(observation[ENERGY_INDEX])
        water = float(observation[WATER_INDEX])
        fatigue = float(observation[FATIGUE_INDEX])
        sleeping = float(observation[SLEEPING_INDEX]) > 0.5
        life = self._lives.get(creature_id)
        if life is None:
            life = _Life(tick, tick, water, False, False)
            self._lives[creature_id] = life
        else:
            if ate and life.energy_low:
                life.ate_while_low_energy += 1
            if water > life.water + self._drink_epsilon and life.water_low:
                life.drank_while_low_water += 1
            # Same attribution as eating/drinking: tired at t-1, asleep at t.
            if life.fatigue_high:
                life.high_fatigue_steps += 1
                life.slept_while_tired += sleeping
        life.fatigue_high = fatigue > HIGH_FATIGUE_THRESHOLD
        life.energy_low = energy < self._low_threshold
        life.water_low = water < self._low_threshold
        life.low_energy_steps += life.energy_low
        life.low_water_steps += life.water_low
        life.water = water
        life.last_tick = tick

    @staticmethod
    def _parse_events(step: Step) -> tuple[set[int], dict[int, int]]:
        ate: set[int] = set()
        deaths: dict[int, int] = {}
        for event in step.events:
            if len(event.payload) < 4:
                continue
            (creature_id,) = struct.unpack_from("<I", event.payload, 0)
            if event.event_type is EventType.ATE:
                ate.add(creature_id)
            elif event.event_type is EventType.DEATH and len(event.payload) >= 5:
                deaths[creature_id] = event.payload[4]
        return ate, deaths

    def _realtime_factor(self, tick: int) -> float:
        if self._first_tick is None or self._started_at is None:
            return 0.0
        wall = self._clock() - self._started_at
        simulated = (tick - self._first_tick) * BRAIN_SECONDS_PER_TICK
        return simulated / wall if wall > 0 else 0.0

    def _write(self, creature_id: int, life: _Life, end_tick: int, cause: str) -> None:
        record = {
            "policy": self._policy,
            "seed": self._seed,
            "creature_id": creature_id,
            "lifetime_seconds": max(0, end_tick - life.first_tick) * BRAIN_SECONDS_PER_TICK,
            "low_energy_steps": life.low_energy_steps,
            "ate_while_low_energy": life.ate_while_low_energy,
            "low_water_steps": life.low_water_steps,
            "drank_while_low_water": life.drank_while_low_water,
            "high_fatigue_steps": life.high_fatigue_steps,
            "slept_while_tired": life.slept_while_tired,
            "death_cause": cause,
            "config_id": self._config_id,
            "horizon": self._horizon,
            "tick_loss": self.tick_loss,
            "realtime_factor": round(self._realtime_factor(end_tick), 4),
        }
        self._sink.write(json.dumps(record, sort_keys=True) + "\n")
        self.episodes_written += 1
