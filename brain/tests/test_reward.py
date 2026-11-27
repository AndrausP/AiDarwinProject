import pytest

from creature_brain.learning.reward import HomeostaticReward, RewardConfig


def _intero(
    energy: float = 0.5,
    water: float = 0.5,
    temperature: float = 37.0,
    head: float = 1.0,
    torso: float = 1.0,
    legs: float = 1.0,
    fatigue: float = 0.15,
    pain: float = 0.0,
    libido: float = 0.5,
    fear: float = 0.0,
) -> tuple[float, ...]:
    return (
        energy,
        water,
        temperature,
        head,
        torso,
        legs,
        fatigue,
        pain,
        libido,
        0.2,
        1.0,
        fear,
    )


def test_should_reward_creature_when_energy_moves_toward_setpoint() -> None:
    reward = HomeostaticReward()

    result = reward.compute(_intero(energy=0.3), _intero(energy=0.5))

    assert result.total > 0
    assert result.homeostatic == pytest.approx(0.2)


def test_should_penalize_creature_when_pain_and_death_occur() -> None:
    reward = HomeostaticReward(RewardConfig(pain_weight=0.5, death_penalty=1.0))

    result = reward.compute(_intero(), _intero(pain=0.4), died=True)

    assert result.pain_penalty == pytest.approx(0.2)
    assert result.death_penalty == 1.0
    assert result.total == pytest.approx(-1.2)


def test_should_not_use_age_or_maturity_when_reward_is_computed() -> None:
    reward = HomeostaticReward()
    previous = list(_intero())
    current = list(_intero())
    previous[9:11] = [0.0, 0.0]
    current[9:11] = [1.0, 1.0]

    assert reward.compute(previous, current).total == pytest.approx(0.0)


def test_should_reject_interoception_when_layout_is_invalid() -> None:
    with pytest.raises(ValueError, match="12"):
        HomeostaticReward().compute((0.0,), (0.0,))



def test_should_penalize_fear_when_feeling_is_high() -> None:
    reward = HomeostaticReward(RewardConfig(fear_weight=0.3))

    calm = reward.compute(_intero(), _intero())
    afraid = reward.compute(_intero(), _intero(fear=0.5))

    assert calm.fear_penalty == 0.0
    assert afraid.fear_penalty == pytest.approx(0.15)
    assert afraid.total == pytest.approx(-0.15)

def test_should_match_per_creature_reward_when_computed_in_batch() -> None:
    import numpy as np

    from creature_brain.learning.reward import HomeostaticReward

    rng = np.random.default_rng(3)
    previous = rng.uniform(0.0, 1.0, size=(50, 12))
    current = rng.uniform(0.0, 1.0, size=(50, 12))
    previous[:, 2] = rng.uniform(30.0, 40.0, size=50)
    current[:, 2] = rng.uniform(30.0, 40.0, size=50)
    died = rng.uniform(size=50) < 0.2
    reward = HomeostaticReward()

    totals = reward.compute_totals(previous, current, died)

    expected = [reward.compute(tuple(p), tuple(c), died=bool(d)).total for p, c, d in zip(previous, current, died)]
    assert np.allclose(totals, expected, atol=1e-12)


def test_should_give_different_rewards_when_individual_motivations_differ() -> None:
    import numpy as np

    reward = HomeostaticReward()
    previous = np.tile(np.array([0.4, 0.4, 37.0, 1, 1, 1, 0.1, 0.0, 0.5, 0.1, 0, 0.2]), (2, 1))
    current = previous.copy()
    current[:, 0] += 0.1  # both ate the same amount
    motivations = reward.default_motivations(2)
    motivations[1, 0] *= 3.0  # the second individual cares three times more about food
    totals = reward.compute_totals(previous, current, np.zeros(2, dtype=bool), motivations)

    assert totals[1] > totals[0]
    assert totals[0] == pytest.approx(reward.compute_totals(previous[:1], current[:1], np.zeros(1, dtype=bool))[0])


def test_should_reject_negative_motivation_when_weights_are_given() -> None:
    import numpy as np

    reward = HomeostaticReward()
    rows = np.zeros((1, 12))
    rows[0, 2] = 37.0
    bad = reward.default_motivations(1)
    bad[0, 7] = -1.0
    with pytest.raises(ValueError):
        reward.compute_totals(rows, rows, np.zeros(1, dtype=bool), bad)

def test_should_reward_care_when_creature_feeds_its_young() -> None:
    import numpy as np

    reward = HomeostaticReward()
    rows = np.tile(np.array([0.8, 0.8, 37.0, 1, 1, 1, 0.15, 0.0, 0.0, 0.1, 1, 0.0]), (2, 1))
    motivations = reward.default_motivations(2)
    motivations[1, 8] = 0.0  # second individual does not care about its young
    fed = np.array([0.01, 0.01])

    totals = reward.compute_totals(rows, rows, np.zeros(2, dtype=bool), motivations, fed)

    assert totals[0] > 0.0
    assert totals[1] == pytest.approx(0.0)


def test_should_reward_mating_when_libido_drops_to_setpoint() -> None:
    import numpy as np

    reward = HomeostaticReward()
    before = np.array([[0.8, 0.8, 37.0, 1, 1, 1, 0.15, 0.0, 0.9, 0.3, 1, 0.0]])
    after = before.copy()
    after[0, 8] = 0.0  # mating satisfies libido

    assert reward.compute_totals(before, after, np.zeros(1, dtype=bool))[0] > 0.2