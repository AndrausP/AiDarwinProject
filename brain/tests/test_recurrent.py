import struct

import pytest
import torch

from creature_brain import ACTION_DIM, OBSERVATION_DIM
from creature_brain.bridge.protocol import Event, EventType, Step
from creature_brain.learning.online import OnlinePPOConfig, OnlinePPOPolicy
from creature_brain.learning.recurrent import (
    CuriosityConfig,
    RandomNetworkDistillation,
    RecurrentActorCritic,
)


def test_should_have_millions_of_parameters_when_brain_v3_is_created() -> None:
    model = RecurrentActorCritic()

    assert 3_500_000 <= model.trainable_parameter_count <= 4_500_000


def test_should_replay_same_outputs_when_sequence_matches_stepwise_acting() -> None:
    torch.manual_seed(0)
    model = RecurrentActorCritic()
    observations = torch.randn(5, 3, OBSERVATION_DIM)
    starts = torch.zeros(5, 3, dtype=torch.bool)
    starts[0] = True
    starts[3, 1] = True  # slot 1 gets a newborn at t=3
    hidden = model.initial_state(3)
    stepwise = []
    with torch.no_grad():
        for t in range(5):
            hidden = hidden * (~starts[t]).unsqueeze(-1).float()
            mean, _, value, hidden = model.step(observations[t], hidden)
            stepwise.append((mean, value))
        mean_seq, _, value_seq = model.sequence(observations, model.initial_state(3), starts)
    for t, (mean, value) in enumerate(stepwise):
        assert torch.allclose(mean, mean_seq[t], atol=1e-5)
        assert torch.allclose(value, value_seq[t], atol=1e-5)


def test_should_remember_past_observation_when_memory_is_carried() -> None:
    torch.manual_seed(0)
    model = RecurrentActorCritic()
    first_a, first_b = torch.randn(1, OBSERVATION_DIM), torch.randn(1, OBSERVATION_DIM)
    same_now = torch.zeros(1, OBSERVATION_DIM)
    with torch.no_grad():
        _, _, _, memory_a = model.step(first_a, model.initial_state(1))
        _, _, _, memory_b = model.step(first_b, model.initial_state(1))
        mean_a, _, _, _ = model.step(same_now, memory_a)
        mean_b, _, _, _ = model.step(same_now, memory_b)
    # Same current input, different past -> different decision: the past is remembered.
    assert not torch.allclose(mean_a, mean_b)


def _death(creature_id: int) -> Event:
    return Event(EventType.DEATH, struct.pack("<IB", creature_id, 1))


def test_should_zero_memory_for_newborn_when_slot_changes_owner() -> None:
    policy = OnlinePPOPolicy(online_config=OnlinePPOConfig(rollout_horizon=50, seed=0))
    obs = (0.5,) * OBSERVATION_DIM
    policy.act(Step(0, (1, 2), (obs, obs)))
    policy.act(Step(1, (1, 2), (obs, obs)))
    assert policy.pending is not None and policy.pending.starts is not None
    assert not bool(policy.pending.starts.any())
    policy.act(Step(2, (2,), (obs,), (_death(1),)))
    policy.act(Step(3, (2, 3), (obs, obs)))
    starts = policy.pending.starts
    hidden_in = policy.pending.hidden_in
    assert starts is not None and hidden_in is not None
    assert policy.slots == [3, 2]
    assert bool(starts[0]) and not bool(starts[1])
    assert torch.count_nonzero(hidden_in[0]) == 0
    assert torch.count_nonzero(hidden_in[1]) > 0


def test_should_update_and_resume_when_gru_brain_trains(tmp_path) -> None:
    checkpoint = tmp_path / "ppo.pt"
    config = OnlinePPOConfig(rollout_horizon=4, seed=0, checkpoint_path=checkpoint)
    policy = OnlinePPOPolicy(online_config=config)
    policy.online_config = OnlinePPOConfig(
        rollout_horizon=4, seed=0, checkpoint_path=checkpoint, checkpoint_every_updates=1
    )
    obs = tuple(float(i % 7) / 7 for i in range(OBSERVATION_DIM))
    for tick in range(10):
        actions = policy.act(Step(tick, (1, 2), (obs, obs)))
        assert len(actions) == 2 and len(actions[0]) == ACTION_DIM
    assert policy.update_count >= 2
    assert checkpoint.exists()

    resumed = OnlinePPOPolicy(online_config=config)
    assert resumed.update_count == policy.update_count
    for name, tensor in policy.model.state_dict().items():
        assert torch.equal(tensor, resumed.model.state_dict()[name])


def test_should_refuse_mlp_checkpoint_when_gru_brain_loads(tmp_path) -> None:
    checkpoint = tmp_path / "old.pt"
    mlp = OnlinePPOPolicy(online_config=OnlinePPOConfig(brain="mlp", rollout_horizon=2, seed=0))
    mlp.trainer.save_checkpoint(checkpoint, step=1)

    with pytest.raises(ValueError, match="--brain mlp"):
        OnlinePPOPolicy(online_config=OnlinePPOConfig(brain="gru", checkpoint_path=checkpoint))


def test_should_lose_novelty_when_situation_repeats() -> None:
    torch.manual_seed(0)
    curiosity = RandomNetworkDistillation()
    optimizer = torch.optim.Adam(curiosity.predictor.parameters(), lr=1e-3)
    familiar = torch.randn(64, OBSERVATION_DIM)
    before = float(curiosity.error(familiar).mean())
    for _ in range(200):
        loss = curiosity.error(familiar).mean()
        optimizer.zero_grad()
        loss.backward()
        optimizer.step()
    after = float(curiosity.error(familiar).mean())
    novel = float(curiosity.error(torch.randn(64, OBSERVATION_DIM) * 2).mean())
    assert after < before * 0.2
    assert novel > after * 3


def test_should_run_without_curiosity_when_weight_is_zero() -> None:
    policy = OnlinePPOPolicy(
        online_config=OnlinePPOConfig(rollout_horizon=2, seed=0, curiosity=CuriosityConfig(weight=0.0))
    )
    obs = (0.5,) * OBSERVATION_DIM
    for tick in range(5):
        policy.act(Step(tick, (1,), (obs,)))
    assert policy.curiosity is None
    assert policy.update_count >= 1
