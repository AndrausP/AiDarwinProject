"""Online PPO controller wired to protocol STEP messages.

Reward for STEP t is assigned to the action emitted for STEP t-1. The
controller never reads resource positions or other privileged state: policy
input is exactly the public 1000-float observation (v2), and reward uses only its
interoception slice plus DEATH events.
"""

from __future__ import annotations

from dataclasses import dataclass
import logging
from pathlib import Path
import struct
from typing import Sequence

import numpy as np
import torch
from torch import Tensor

from creature_brain import OBSERVATION_DIM
from creature_brain.bridge.protocol import EventType, Step
from creature_brain.learning.ppo import (
    PPOConfig,
    PPOTrainer,
    RolloutBuffer,
    RunningMeanStd,
    SharedActorCritic,
    UpdateMetrics,
    seed_everything,
)
from creature_brain.learning.recurrent import (
    CuriosityConfig,
    RandomNetworkDistillation,
    RecurrentActorCritic,
    RecurrentPPOTrainer,
    SequenceRolloutBuffer,
)
from creature_brain.learning.reward import HomeostaticReward, RewardConfig


LOGGER = logging.getLogger(__name__)

INTEROCEPTION_START = 986  # observation v2
INTEROCEPTION_END = 998


@dataclass(frozen=True, slots=True)
class OnlinePPOConfig:
    rollout_horizon: int = 64
    checkpoint_every_updates: int = 10
    checkpoint_path: Path | None = None
    device: str = "cpu"
    seed: int = 0
    deterministic_actions: bool = False
    metrics_path: Path | None = None
    # "gru" = brain v3 (memory + curiosity, ~4M params); "mlp" = brain v2 (no memory), kept for
    # comparison and to resume runs started before v3 (e.g. v2-ppo-a).
    brain: str = "gru"
    curiosity: CuriosityConfig = CuriosityConfig()


@dataclass(slots=True)
class _PendingDecision:
    """Decision taken at the previous tick, one row per agent slot (None = empty slot)."""

    slot_ids: tuple[int | None, ...]
    observations: Tensor
    actions: Tensor
    log_probabilities: Tensor
    values: Tensor
    interoception: dict[int, tuple[float, ...]]
    hidden_in: Tensor | None = None  # gru: memory entering this decision (already reset if new life)
    starts: Tensor | None = None  # gru: slots whose memory was zeroed for this decision


class OnlinePPOPolicy:
    """Collects on-policy transitions and updates one shared actor/critic."""

    def __init__(
        self,
        *,
        online_config: OnlinePPOConfig = OnlinePPOConfig(),
        ppo_config: PPOConfig = PPOConfig(),
        reward_config: RewardConfig = RewardConfig(),
    ) -> None:
        if online_config.rollout_horizon <= 0:
            raise ValueError("rollout horizon must be positive")
        seed_everything(online_config.seed)
        self.online_config = online_config
        self.device = torch.device(online_config.device)
        if online_config.brain not in ("gru", "mlp"):
            raise ValueError("brain must be gru or mlp")
        self.recurrent = online_config.brain == "gru"
        self.curiosity: RandomNetworkDistillation | None = None
        if self.recurrent:
            self.model = RecurrentActorCritic().to(self.device)
            if online_config.curiosity.weight > 0:
                self.curiosity = RandomNetworkDistillation().to(self.device)
            self.trainer = RecurrentPPOTrainer(
                self.model, ppo_config, curiosity=self.curiosity,
                curiosity_config=online_config.curiosity, seed=online_config.seed,
            )
        else:
            self.model = SharedActorCritic().to(self.device)
            self.trainer = PPOTrainer(self.model, ppo_config, seed=online_config.seed)
        self.reward = HomeostaticReward(reward_config)
        self.rollout = self._new_rollout()
        self.hidden: Tensor | None = None
        self.novelty_rms = RunningMeanStd(())
        self._step_novelty: list[float] = []
        self.pending: _PendingDecision | None = None
        self.slots: list[int | None] = []
        # Standard PPO normalisation: observations mix 0..1 rays with a ~37 °C temperature, and
        # per-step homeostatic rewards are ~1e-4, too small for the critic to learn from.
        self.observation_rms = RunningMeanStd((OBSERVATION_DIM,))
        self.return_rms = RunningMeanStd(())
        self._slot_returns = np.zeros(0, dtype=np.float64)
        self.update_count = 0
        self.last_update: UpdateMetrics | None = None
        self.last_mean_reward = 0.0
        self._step_rewards: list[float] = []
        checkpoint = online_config.checkpoint_path
        if checkpoint is not None and checkpoint.exists():
            self.update_count, extra = self.trainer.load_checkpoint_with_extra(checkpoint)
            if "observation_rms" in extra:
                self.observation_rms.load_state_dict(extra["observation_rms"])
            if "return_rms" in extra:
                self.return_rms.load_state_dict(extra["return_rms"])
            if "novelty_rms" in extra:
                self.novelty_rms.load_state_dict(extra["novelty_rms"])

    _SESSION_FIELDS = ("rollout", "pending", "slots", "_slot_returns", "_step_rewards", "hidden")

    def _new_rollout(self) -> RolloutBuffer | SequenceRolloutBuffer:
        return SequenceRolloutBuffer() if self.recurrent else RolloutBuffer()

    def new_session(self) -> "PolicySession":
        """Per-world state (slots, pending decision, rollout, return accumulators) sharing this
        policy's model, optimiser and normalisers. One per connected Godot world."""
        return PolicySession(self)

    def _swap_session_state(self, state: dict) -> dict:
        previous = {name: getattr(self, name) for name in self._SESSION_FIELDS}
        for name, value in state.items():
            setattr(self, name, value)
        return previous

    def _fresh_session_state(self) -> dict:
        return {
            "hidden": None,
            "rollout": self._new_rollout(),
            "pending": None,
            "slots": [],
            "_slot_returns": np.zeros(0, dtype=np.float64),
            "_step_rewards": [],
        }

    def act(self, step: Step) -> tuple[tuple[float, ...], ...]:
        """Vectorised-env style: a fixed set of agent slots. A creature that dies ends its
        slot's trajectory with a terminal transition; a newborn takes over a free slot. So
        deaths/respawns never force an early, tiny PPO update.

        One forward pass per tick: a creature that survives keeps its slot, so the value
        computed for the next decision doubles as the GAE bootstrap of the last transition."""
        array = step.observation_array
        if array is None:
            array = np.asarray(step.observations, dtype=np.float32).reshape(-1, OBSERVATION_DIM)
        # Rows of one float64 copy (not views of the decoded frame, which is reused per tick).
        interoception_rows = np.array(array[:, INTEROCEPTION_START:INTEROCEPTION_END], dtype=np.float64)
        current_interoception = dict(zip(step.creature_ids, interoception_rows))
        row_of = {creature_id: row for row, creature_id in enumerate(step.creature_ids)}
        observations = self._observation_tensor(array)

        transition = None
        layout_changed = False
        if self.pending is not None:
            death_ids = self._death_ids(step)
            rewards, terminals, valid, continuing = self._slot_transitions(
                self.pending, current_interoception, row_of, death_ids
            )
            self._step_rewards.append(float(rewards[valid].mean()) if bool(valid.any()) else 0.0)
            transition = (self._scale_rewards(rewards, terminals, valid), terminals, valid, continuing)
            layout_changed = not self._assign_slots(step.creature_ids, death_ids)
            if layout_changed:
                # More creatures than slots (population grew): rebuild the layout. The pending
                # rollout is flushed with a zero bootstrap (treated as truncation; rare).
                self.slots = list(sorted(step.creature_ids))
        else:
            self.slots = list(sorted(step.creature_ids))

        slot_observations = self._slot_observations(observations, row_of)
        hidden_in, starts = self._memory_for_slots(layout_changed)
        actions, raw_actions, log_probabilities, values, hidden_out = self._decide(slot_observations, hidden_in)

        if transition is not None:
            rewards, terminals, valid, continuing = transition
            pending = self.pending
            assert pending is not None
            if layout_changed:
                bootstrap = torch.zeros(len(pending.slot_ids), dtype=torch.float32, device=self.device)
            else:
                bootstrap = values.detach() * continuing
                rewards = rewards + self._novelty_bonus(slot_observations, valid, continuing)
            if self.recurrent:
                assert pending.hidden_in is not None and pending.starts is not None
                self.rollout.add(
                    pending.observations, pending.actions, pending.log_probabilities, pending.values,
                    rewards, terminals, valid, pending.starts, pending.hidden_in,
                )
            else:
                self.rollout.add(
                    pending.observations,
                    pending.actions,
                    pending.log_probabilities,
                    pending.values,
                    rewards,
                    terminals,
                    valid,
                )
            if layout_changed or len(self.rollout) >= self.online_config.rollout_horizon:
                self._update(bootstrap)
                # Sample the next decision with the freshly updated policy.
                actions, raw_actions, log_probabilities, values, hidden_out = self._decide(
                    slot_observations, hidden_in
                )

        self.hidden = hidden_out
        self.pending = _PendingDecision(
            tuple(self.slots),
            slot_observations.detach(),
            raw_actions.detach(),
            log_probabilities.detach(),
            values.detach(),
            current_interoception,
            hidden_in,
            starts,
        )
        slot_of = {creature_id: slot for slot, creature_id in enumerate(self.slots) if creature_id is not None}
        rows = actions.detach().cpu().tolist()
        return tuple(tuple(rows[slot_of[creature_id]]) for creature_id in step.creature_ids)

    def _memory_for_slots(self, layout_changed: bool) -> tuple[Tensor | None, Tensor | None]:
        """gru only: memory entering this tick per slot. A slot whose occupant changed (newborn,
        empty, new layout) starts from zero memory: nothing learned in a life is inherited."""
        if not self.recurrent:
            return None, None
        count = len(self.slots)
        previous = () if (self.pending is None or layout_changed) else self.pending.slot_ids
        starts = torch.tensor(
            [cid is None or i >= len(previous) or previous[i] != cid for i, cid in enumerate(self.slots)],
            dtype=torch.bool, device=self.device,
        )
        if self.hidden is None or self.hidden.shape[0] != count:
            self.hidden = self.model.initial_state(count, self.device)
            starts = torch.ones(count, dtype=torch.bool, device=self.device)
        return self.hidden * (~starts).unsqueeze(-1).to(self.hidden.dtype), starts

    def _decide(self, observations: Tensor, hidden_in: Tensor | None):
        deterministic = self.online_config.deterministic_actions
        if self.recurrent:
            assert hidden_in is not None
            return self.model.act_step(observations, hidden_in, deterministic=deterministic)
        actions, raw, log_probabilities, values = self.model.act_with_raw(observations, deterministic=deterministic)
        return actions, raw, log_probabilities, values, None

    @torch.no_grad()
    def _novelty_bonus(self, observations: Tensor, valid: Tensor, continuing: Tensor) -> Tensor:
        """Curiosity reward for reaching the current observation (only for creatures still alive)."""
        if self.curiosity is None or observations.shape[0] != continuing.shape[0]:
            return torch.zeros_like(continuing)
        novelty = self.curiosity.error(observations)
        alive = valid & (continuing > 0)
        if bool(alive.any()):
            values = novelty[alive].detach().cpu().numpy()
            self.novelty_rms.update(values)
            self._step_novelty.append(float(values.mean()))
        scale = float(np.sqrt(self.novelty_rms.var + 1e-8))
        return (self.online_config.curiosity.weight * novelty / scale).clamp(max=1.0) * continuing

    def _scale_rewards(self, rewards: Tensor, terminals: Tensor, valid: Tensor) -> Tensor:
        """Divide by the running std of the discounted return (per slot), reset at life end."""
        raw = rewards.detach().cpu().numpy().astype(np.float64)
        if self._slot_returns.shape != raw.shape:
            self._slot_returns = np.zeros_like(raw)
        mask = valid.detach().cpu().numpy()
        self._slot_returns = self._slot_returns * self.trainer.config.discount + raw
        if mask.any():
            self.return_rms.update(self._slot_returns[mask])
        self._slot_returns[terminals.detach().cpu().numpy()] = 0.0
        scale = float(np.sqrt(self.return_rms.var + 1e-8))
        return (rewards / scale).clamp(-10.0, 10.0)

    def _assign_slots(self, current_ids: Sequence[int], death_ids: set[int]) -> bool:
        """Free slots of creatures that are gone; give newcomers the lowest free slots
        (deterministic). Returns False when there are more creatures than slots."""
        present = set(current_ids)
        self.slots = [
            creature_id if creature_id is not None and creature_id in present and creature_id not in death_ids else None
            for creature_id in self.slots
        ]
        seated = {creature_id for creature_id in self.slots if creature_id is not None}
        free = [slot for slot, creature_id in enumerate(self.slots) if creature_id is None]
        newcomers = sorted(present - seated)
        if len(newcomers) > len(free):
            return False
        for slot, creature_id in zip(free, newcomers):
            self.slots[slot] = creature_id
        return True

    def _slot_observations(self, observations: Tensor, row_of: dict[int, int]) -> Tensor:
        """Gather rows into slot order in one indexed copy; empty slots get zeros."""
        rows = [row_of[creature_id] if creature_id is not None else 0 for creature_id in self.slots]
        occupied = [creature_id is not None for creature_id in self.slots]
        if not rows:
            return torch.zeros((0, OBSERVATION_DIM), dtype=torch.float32, device=self.device)
        if observations.shape[0] == 0:
            return torch.zeros((len(rows), OBSERVATION_DIM), dtype=torch.float32, device=self.device)
        index = torch.as_tensor(rows, dtype=torch.long, device=self.device)
        mask = torch.as_tensor(occupied, dtype=torch.float32, device=self.device).unsqueeze(-1)
        return observations.index_select(0, index) * mask

    def _slot_transitions(
        self,
        pending: _PendingDecision,
        current_interoception: dict[int, tuple[float, ...]],
        row_of: dict[int, int],
        death_ids: set[int],
    ) -> tuple[Tensor, Tensor, Tensor, Tensor]:
        """Rewards/terminals per slot plus a mask of slots whose creature survives (and so
        keeps its slot, making the next tick's value its bootstrap)."""
        count = len(pending.slot_ids)
        rewards = np.zeros(count, dtype=np.float64)
        # Padding rows for empty slots: terminal so they cut GAE, dropped from the batch.
        terminals = np.ones(count, dtype=bool)
        valid = np.zeros(count, dtype=bool)
        occupied = [slot for slot, creature_id in enumerate(pending.slot_ids) if creature_id is not None]
        if occupied:
            previous = np.stack([pending.interoception[pending.slot_ids[slot]] for slot in occupied])
            current = np.stack([
                current_interoception.get(pending.slot_ids[slot], previous[i]) for i, slot in enumerate(occupied)
            ])
            died = np.array([
                pending.slot_ids[slot] in death_ids or pending.slot_ids[slot] not in row_of for slot in occupied
            ], dtype=bool)
            rewards[occupied] = self.reward.compute_totals(previous, current, died)
            terminals[occupied] = died
            valid[occupied] = True
        continuing = (valid & ~terminals).astype(np.float32)
        return (
            torch.tensor(rewards, dtype=torch.float32, device=self.device),
            torch.tensor(terminals, dtype=torch.bool, device=self.device),
            torch.tensor(valid, dtype=torch.bool, device=self.device),
            torch.tensor(continuing, dtype=torch.float32, device=self.device),
        )

    def _update(self, bootstrap_values: Tensor) -> None:
        steps = len(self.rollout)
        # Logged in raw (unscaled) homeostatic units so runs stay comparable.
        mean_reward = float(np.mean(self._step_rewards)) if self._step_rewards else 0.0
        self._step_rewards = []
        batch = self.rollout.finish(
            bootstrap_values,
            discount=self.trainer.config.discount,
            gae_lambda=self.trainer.config.gae_lambda,
        )
        self.last_update = self.trainer.update(batch)
        self.update_count += 1
        self.rollout = self._new_rollout()
        self.last_mean_reward = mean_reward
        novelty = float(np.mean(self._step_novelty)) if self._step_novelty else 0.0
        self._step_novelty = []
        metrics = self.last_update
        LOGGER.info(
            "ppo update=%d steps=%d reward_mean=%.5f policy_loss=%.4f value_loss=%.4f entropy=%.3f grad=%.3f novelty=%.4f",
            self.update_count, steps, mean_reward, metrics.policy_loss, metrics.value_loss,
            metrics.entropy, metrics.gradient_norm, novelty,
        )
        if self.online_config.metrics_path is not None:
            self._append_metrics(steps, mean_reward, metrics)
        checkpoint = self.online_config.checkpoint_path
        if (
            checkpoint is not None
            and self.update_count % self.online_config.checkpoint_every_updates == 0
        ):
            self.trainer.save_checkpoint(
                checkpoint,
                step=self.update_count,
                extra={
                    "observation_rms": self.observation_rms.state_dict(),
                    "return_rms": self.return_rms.state_dict(),
                    "novelty_rms": self.novelty_rms.state_dict(),
                },
            )

    def _append_metrics(self, steps: int, mean_reward: float, metrics: UpdateMetrics) -> None:
        path = self.online_config.metrics_path
        assert path is not None
        new_file = not path.exists()
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("a", encoding="utf-8") as stream:
            if new_file:
                stream.write("update,steps,reward_mean,policy_loss,value_loss,entropy,gradient_norm\n")
            stream.write(
                f"{self.update_count},{steps},{mean_reward:.6f},{metrics.policy_loss:.6f},"
                f"{metrics.value_loss:.6f},{metrics.entropy:.6f},{metrics.gradient_norm:.6f}\n"
            )

    @staticmethod
    def _death_ids(step: Step) -> set[int]:
        result: set[int] = set()
        for event in step.events:
            if event.event_type is EventType.DEATH and len(event.payload) >= 4:
                (creature_id,) = struct.unpack_from("<I", event.payload)
                result.add(creature_id)
        return result

    def _observation_tensor(self, array: np.ndarray) -> Tensor:
        if array.shape[0] == 0:
            return torch.empty((0, OBSERVATION_DIM), dtype=torch.float32, device=self.device)
        if array.ndim != 2 or array.shape[1] != OBSERVATION_DIM:
            raise ValueError("invalid protocol observation layout")
        # Same float64 statistics and normalisation as before, computed on the device (T2.0e):
        # the numpy version cost ~11 ms per tick at 288 creatures.
        raw = torch.from_numpy(np.require(array, dtype=np.float32, requirements=("C", "W"))).to(self.device, torch.float64)
        self.observation_rms.update_from_moments(
            raw.mean(dim=0).cpu().numpy(), raw.var(dim=0, unbiased=False).cpu().numpy(), raw.shape[0]
        )
        mean = torch.as_tensor(self.observation_rms.mean, device=self.device)
        std = torch.as_tensor(np.sqrt(self.observation_rms.var + 1e-8), device=self.device)
        return ((raw - mean) / std).clamp(-10.0, 10.0).to(torch.float32)

    @staticmethod
    def _interoception(
        creature_ids: Sequence[int], observations: Sequence[Sequence[float]]
    ) -> dict[int, tuple[float, ...]]:
        return {
            creature_id: tuple(
                float(value)
                for value in observation[INTEROCEPTION_START:INTEROCEPTION_END]
            )
            for creature_id, observation in zip(
                creature_ids, observations, strict=True
            )
        }

    @torch.no_grad()
    def _values(self, observations: Tensor) -> Tensor:
        if observations.shape[0] == 0:
            return torch.empty((0,), dtype=torch.float32, device=self.device)
        _, _, values = self.model(observations)
        return values


class PolicySession:
    """Adapter giving one world its own trajectory state over a shared OnlinePPOPolicy.

    The bridge serves worlds sequentially on one asyncio thread, so swapping the per-world
    fields in and out around each ``act`` is race-free.
    """

    def __init__(self, policy: OnlinePPOPolicy) -> None:
        self.policy = policy
        self._state = policy._fresh_session_state()

    def act(self, step: Step) -> tuple[tuple[float, ...], ...]:
        previous = self.policy._swap_session_state(self._state)
        try:
            return self.policy.act(step)
        finally:
            self._state = self.policy._swap_session_state(previous)
