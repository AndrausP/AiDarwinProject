import struct

import numpy as np

from creature_brain import ACTION_DIM, OBSERVATION_DIM
from creature_brain.bridge.protocol import Event, EventType, Step
from creature_brain.learning.instinct import (
    AGE_FRACTION_INDEX,
    WEIGHT_COUNT,
    InstinctConfig,
    InstinctPolicy,
    InstinctPool,
)


class _Zero:
    def act(self, step: Step) -> tuple[tuple[float, ...], ...]:
        return tuple((0.0,) * ACTION_DIM for _ in step.creature_ids)


def _step(tick: int, ids: tuple[int, ...], age: float = 0.0, events: tuple[Event, ...] = ()) -> Step:
    obs = np.zeros((len(ids), OBSERVATION_DIM), dtype=np.float32)
    obs[:, AGE_FRACTION_INDEX] = age
    obs[:, 1008:1100] = 0.5
    return Step(tick, ids, tuple(tuple(row) for row in obs), events)


def test_should_inherit_mutated_mix_of_parents_when_child_is_born() -> None:
    pool = InstinctPool(InstinctConfig(seed=3))
    mother = np.zeros(WEIGHT_COUNT, dtype=np.float32)
    father = np.ones(WEIGHT_COUNT, dtype=np.float32)

    child = pool.child(mother, father)

    from_mother = np.isclose(child, 0.0, atol=0.2).mean()
    from_father = np.isclose(child, 1.0, atol=0.2).mean()
    assert 0.4 < from_mother < 0.6 and 0.4 < from_father < 0.6
    mutated = ~(np.isclose(child, 0.0) | np.isclose(child, 1.0))
    assert 0.02 < mutated.mean() < 0.08


def test_should_fade_instinct_when_creature_ages() -> None:
    pool = InstinctPool()

    young, old = pool.alpha(np.array([0.0, 0.5]))

    assert young == 0.5 and old < 0.01 * young


def test_should_give_newborn_its_parents_instinct_when_birth_event_arrives() -> None:
    pool = InstinctPool(InstinctConfig(seed=1))
    policy = InstinctPolicy(_Zero(), pool)
    policy.act(_step(0, (1, 2)))
    birth = Event(EventType.BIRTH, struct.pack("<III", 9, 1, 2))

    actions = policy.act(_step(1, (1, 2, 9), events=(birth,)))

    child, mother, father = pool.weights[9], pool.weights[1], pool.weights[2]
    from_parents = np.isclose(child, mother) | np.isclose(child, father)
    assert from_parents.mean() > 0.9
    assert len(actions) == 3 and any(abs(v) > 0 for v in actions[2])


def test_should_forget_dead_and_keep_weights_unchanged_when_acting() -> None:
    pool = InstinctPool(InstinctConfig(seed=2))
    policy = InstinctPolicy(_Zero(), pool)
    policy.act(_step(0, (1, 2)))
    before = pool.weights[2].copy()

    policy.act(_step(1, (2,)))

    assert 1 not in pool.weights
    assert np.array_equal(pool.weights[2], before)  # nothing learned in life is written into a gene
