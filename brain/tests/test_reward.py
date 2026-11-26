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