from pathlib import Path

import pytest

torch = pytest.importorskip("torch")

from creature_brain import ACTION_DIM, OBSERVATION_DIM
from creature_brain.learning.ppo import (
    PPOConfig,
    PPOTrainer,
    RolloutBuffer,
    SharedActorCritic,
    seed_everything,
)


def test_should_match_parameter_budget_when_model_is_created() -> None:
    model = SharedActorCritic()

    # Observation v3 (1124 inputs, 13 actions): same 512-256 trunk, wider first layer.
    assert 690_000 <= model.trainable_parameter_count <= 730_000


def test_should_return_bounded_actions_when_policy_acts() -> None:
    seed_everything(7)
    model = SharedActorCritic()
    observations = torch.zeros((48, OBSERVATION_DIM))

    actions, log_probabilities, values = model.act(observations)

    assert actions.shape == (48, ACTION_DIM)
    assert log_probabilities.shape == (48,)
    assert values.shape == (48,)
    assert torch.all(actions >= -1.0)
    assert torch.all(actions <= 1.0)
    assert torch.isfinite(log_probabilities).all()


def test_should_compute_finite_update_when_rollout_is_valid() -> None:
    seed_everything(11)
    model = SharedActorCritic()
    trainer = PPOTrainer(
        model,
        PPOConfig(epochs=1, minibatch_size=8),
        seed=11,
    )
    buffer = RolloutBuffer()
    for _ in range(2):
        observations = torch.randn((4, OBSERVATION_DIM))
        actions, log_probabilities, values = model.act(observations)
        buffer.add(
            observations,
            actions,
            log_probabilities,
            values,
            torch.full((4,), 0.1),
            torch.zeros((4,), dtype=torch.bool),
        )
    rollout = buffer.finish(torch.zeros(4))

    metrics = trainer.update(rollout)

    assert metrics.updates == 1
    assert all(
        torch.isfinite(torch.tensor(value))
        for value in (
            metrics.policy_loss,
            metrics.value_loss,
            metrics.entropy,
            metrics.gradient_norm,
        )
    )


def test_should_restore_model_when_checkpoint_is_loaded(tmp_path: Path) -> None:
    seed_everything(17)
    model = SharedActorCritic()
    trainer = PPOTrainer(model)
    target = tmp_path / "policy.pt"
    trainer.save_checkpoint(target, step=42)
    expected = {key: value.clone() for key, value in model.state_dict().items()}
    with torch.no_grad():
        for parameter in model.parameters():
            parameter.zero_()

    step = trainer.load_checkpoint(target)

    assert step == 42
    assert all(torch.equal(model.state_dict()[key], value) for key, value in expected.items())



def test_should_keep_acting_when_ids_die_and_new_ids_appear() -> None:
    import struct

    from creature_brain import OBSERVATION_DIM
    from creature_brain.bridge.protocol import Event, EventType, Step
    from creature_brain.learning.online import OnlinePPOConfig, OnlinePPOPolicy

    policy = OnlinePPOPolicy(online_config=OnlinePPOConfig(rollout_horizon=4, seed=0))
    obs = (0.5,) * OBSERVATION_DIM
    death = Event(EventType.DEATH, struct.pack("<IB", 1, 3))
    steps = [
        Step(0, (1, 2), (obs, obs)),
        Step(1, (2,), (obs,), (death,)),
        Step(2, (2, 3), (obs, obs)),
        Step(3, (2, 3), (obs, obs)),
        Step(4, (2, 3), (obs, obs)),
        Step(5, (2, 3), (obs, obs)),
    ]
    for step in steps:
        actions = policy.act(step)
        assert len(actions) == len(step.creature_ids)
    assert policy.update_count >= 1


def test_should_write_metrics_csv_when_metrics_path_given(tmp_path) -> None:
    from creature_brain import OBSERVATION_DIM
    from creature_brain.bridge.protocol import Step
    from creature_brain.learning.online import OnlinePPOConfig, OnlinePPOPolicy

    metrics = tmp_path / "metrics.csv"
    policy = OnlinePPOPolicy(
        online_config=OnlinePPOConfig(rollout_horizon=2, seed=0, metrics_path=metrics)
    )
    obs = (0.5,) * OBSERVATION_DIM
    for tick in range(4):
        policy.act(Step(tick, (1, 2), (obs, obs)))

    lines = metrics.read_text(encoding="utf-8").splitlines()
    assert lines[0].startswith("update,steps,reward_mean")
    assert len(lines) >= 2


def _death_event(creature_id: int):
    import struct

    from creature_brain.bridge.protocol import Event, EventType

    return Event(EventType.DEATH, struct.pack("<IB", creature_id, 1))


def test_should_not_force_update_when_creature_dies_and_respawns() -> None:
    from creature_brain import OBSERVATION_DIM
    from creature_brain.bridge.protocol import Step
    from creature_brain.learning.online import OnlinePPOConfig, OnlinePPOPolicy

    policy = OnlinePPOPolicy(online_config=OnlinePPOConfig(rollout_horizon=8, seed=0))
    obs = (0.5,) * OBSERVATION_DIM
    policy.act(Step(0, (1, 2), (obs, obs)))
    policy.act(Step(1, (2,), (obs,), (_death_event(1),)))  # 1 died, slot empty this tick
    assert policy.slots == [None, 2]
    policy.act(Step(2, (2, 3), (obs, obs)))  # newborn 3 takes the free slot
    assert policy.slots == [3, 2]
    assert policy.update_count == 0
    for tick in range(3, 9):
        policy.act(Step(tick, (2, 3), (obs, obs)))
    assert policy.update_count == 1
    assert len(policy.slots) == 2


def test_should_return_actions_in_step_order_when_slots_are_permuted() -> None:
    from creature_brain import OBSERVATION_DIM
    from creature_brain.bridge.protocol import Step
    from creature_brain.learning.online import OnlinePPOConfig, OnlinePPOPolicy

    policy = OnlinePPOPolicy(
        # Stateless brain: the check relies on "same observation -> same action", which memory breaks.
        online_config=OnlinePPOConfig(rollout_horizon=64, seed=0, deterministic_actions=True, brain="mlp")
    )
    low = tuple([0.0] * OBSERVATION_DIM)
    high = tuple([1.0] * OBSERVATION_DIM)
    # Freeze normalisation for a stable comparison.
    policy.observation_rms.update = lambda batch: None
    policy.observation_rms.update_from_moments = lambda mean, var, count: None
    policy.act(Step(0, (1, 2), (low, high)))
    policy.act(Step(1, (2,), (high,), (_death_event(1),)))
    actions = policy.act(Step(2, (2, 3), (high, low)))  # slots are now [3, 2]
    expected_for_low = policy.act(Step(3, (2, 3), (high, low)))[1]
    assert actions[1] == expected_for_low  # creature 3 (low obs) gets its own row back
    assert actions[0] != actions[1]


def test_should_drop_padding_rows_when_finishing_rollout() -> None:
    buffer = RolloutBuffer()
    obs = torch.zeros(2, OBSERVATION_DIM)
    act = torch.zeros(2, ACTION_DIM)
    zeros = torch.zeros(2)
    buffer.add(obs, act, zeros, zeros, torch.ones(2), torch.tensor([False, True]), torch.tensor([True, False]))
    batch = buffer.finish(torch.zeros(2))
    assert batch.observations.shape == (1, OBSERVATION_DIM)


def test_should_reject_non_terminal_padding_row() -> None:
    buffer = RolloutBuffer()
    zeros = torch.zeros(1)
    with pytest.raises(ValueError):
        buffer.add(torch.zeros(1, OBSERVATION_DIM), torch.zeros(1, ACTION_DIM), zeros, zeros, zeros,
                   torch.tensor([False]), torch.tensor([False]))


def test_should_restore_normalisers_when_loading_checkpoint(tmp_path) -> None:
    from creature_brain import OBSERVATION_DIM
    from creature_brain.bridge.protocol import Step
    from creature_brain.learning.online import OnlinePPOConfig, OnlinePPOPolicy

    path = tmp_path / "ppo.pt"
    config = OnlinePPOConfig(rollout_horizon=2, seed=0, checkpoint_path=path, checkpoint_every_updates=1)
    policy = OnlinePPOPolicy(online_config=config)
    obs = tuple([37.0] + [0.5] * (OBSERVATION_DIM - 1))
    for tick in range(4):
        policy.act(Step(tick, (1, 2), (obs, obs)))

    restored = OnlinePPOPolicy(online_config=config)
    assert restored.update_count == policy.update_count >= 1
    assert restored.observation_rms.mean[0] > 30.0  # saw the raw 37 °C feature


def test_should_keep_raw_actions_bounded_by_std_cap() -> None:
    model = SharedActorCritic()
    with torch.no_grad():
        model.actor_log_std.fill_(5.0)
    mean, log_std, _ = model(torch.zeros(1, OBSERVATION_DIM))
    assert float(log_std.max()) <= 0.0
