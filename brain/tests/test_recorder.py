import io
import json
import struct

import pytest

from creature_brain import OBSERVATION_DIM
from creature_brain.benchmarks.baseline import EpisodeRecord
from creature_brain.benchmarks.recorder import (
    CENSORED_CAUSE,
    ENERGY_INDEX,
    WATER_INDEX,
    EpisodeRecorder,
)
from creature_brain.bridge.protocol import Event, EventType, Step


def _obs(energy: float, water: float) -> tuple[float, ...]:
    values = [0.0] * OBSERVATION_DIM
    values[ENERGY_INDEX] = energy
    values[WATER_INDEX] = water
    return tuple(values)


def _death(creature_id: int, cause: int) -> Event:
    return Event(EventType.DEATH, struct.pack("<IB", creature_id, cause))


def _ate(creature_id: int) -> Event:
    return Event(EventType.ATE, struct.pack("<IBf", creature_id, 0, 0.1))


def _recorder(sink: io.StringIO) -> EpisodeRecorder:
    return EpisodeRecorder(sink, policy="random", seed=7, horizon=100, clock=lambda: 0.0)


def _lines(sink: io.StringIO) -> list[dict]:
    return [json.loads(line) for line in sink.getvalue().splitlines()]


def test_should_write_lifetime_and_cause_when_death_event_arrives() -> None:
    sink = io.StringIO()
    recorder = _recorder(sink)
    for tick in range(10, 15):
        recorder(Step(tick, (1,), (_obs(0.9, 0.9),)))
    recorder(Step(15, (), (), (_death(1, 2),)))

    [line] = _lines(sink)
    assert line["lifetime_seconds"] == pytest.approx(0.5)
    assert line["death_cause"] == "dehydration"
    assert line["seed"] == 7 and line["creature_id"] == 1
    EpisodeRecord.from_mapping(line)  # schema accepted by the benchmark


def test_should_credit_eating_only_when_previous_tick_was_in_deficit() -> None:
    sink = io.StringIO()
    recorder = _recorder(sink)
    recorder(Step(0, (1,), (_obs(0.9, 0.9),)))
    recorder(Step(1, (1,), (_obs(0.9, 0.9),), (_ate(1),)))  # sated: not credited
    recorder(Step(2, (1,), (_obs(0.2, 0.9),)))
    recorder(Step(3, (1,), (_obs(0.25, 0.9),), (_ate(1),)))  # after deficit: credited
    recorder(Step(4, (), (), (_death(1, 1),)))

    [line] = _lines(sink)
    assert line["low_energy_steps"] == 2
    assert line["ate_while_low_energy"] == 1


def test_should_infer_drinking_from_water_rise_when_in_deficit() -> None:
    sink = io.StringIO()
    recorder = _recorder(sink)
    recorder(Step(0, (1,), (_obs(0.9, 0.2),)))
    recorder(Step(1, (1,), (_obs(0.9, 0.3),)))
    recorder(Step(2, (1,), (_obs(0.9, 0.29),)))
    recorder.close()

    [line] = _lines(sink)
    assert line["low_water_steps"] == 3
    assert line["drank_while_low_water"] == 1
    assert line["death_cause"] == CENSORED_CAUSE


def test_should_count_tick_loss_when_ticks_skip() -> None:
    sink = io.StringIO()
    recorder = _recorder(sink)
    recorder(Step(0, (1,), (_obs(0.9, 0.9),)))
    recorder(Step(3, (1,), (_obs(0.9, 0.9),)))

    assert recorder.tick_loss == 2


def test_should_reject_unknown_policy_name() -> None:
    with pytest.raises(ValueError):
        EpisodeRecorder(io.StringIO(), policy="greedy", seed=0)
