"""Brain v3: recurrent actor/critic (GRU memory) + curiosity (Random Network Distillation).

Task 002, etapa 1. The v2 MLP (``SharedActorCritic``) reacts only to the current observation,
so a lake that leaves the field of view is forgotten. This brain carries a hidden state through
the creature's whole life — memory starts empty at birth and is never inherited (Darwin: only
genes pass on; learned content does not).

Shapes: T = time steps of a rollout, N = agent slots, D = observation dim, A = action dim.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Iterator

import numpy as np
import torch
from torch import Tensor, nn

from creature_brain import ACTION_DIM, OBSERVATION_DIM
from creature_brain.learning.ppo import LOG_STD_MAX, LOG_STD_MIN, PPOConfig, UpdateMetrics


class RecurrentActorCritic(nn.Module):
    """~4.0M parameters: encoder 1124→768→768, GRU 512 (memory), head over [memory, encoder]."""

    def __init__(
        self,
        observation_dim: int = OBSERVATION_DIM,
        action_dim: int = ACTION_DIM,
        encoder_size: int = 768,
        hidden_size: int = 512,
        head_size: int = 512,
    ) -> None:
        super().__init__()
        self.observation_dim = observation_dim
        self.action_dim = action_dim
        self.hidden_size = hidden_size
        self.encoder = nn.Sequential(
            nn.Linear(observation_dim, encoder_size),
            nn.SiLU(),
            nn.Linear(encoder_size, encoder_size),
            nn.SiLU(),
        )
        # nn.GRU (not GRUCell) so training replays whole segments in one fused cuDNN call (T2.0e);
        # same maths and parameters as the GRUCell of the first v3 runs (old checkpoints are remapped).
        self.memory = nn.GRU(encoder_size, hidden_size)
        # Skip connection: reactive behaviour (see water → drink) does not have to pass through memory.
        self.head = nn.Sequential(nn.Linear(hidden_size + encoder_size, head_size), nn.SiLU())
        self.actor_mean = nn.Linear(head_size, action_dim)
        self.actor_log_std = nn.Parameter(torch.full((action_dim,), -0.5))
        self.critic = nn.Linear(head_size, 1)

    @property
    def trainable_parameter_count(self) -> int:
        return sum(p.numel() for p in self.parameters() if p.requires_grad)

    def initial_state(self, agents: int, device: torch.device | None = None) -> Tensor:
        return torch.zeros((agents, self.hidden_size), dtype=torch.float32, device=device)

    def _outputs(self, encoded: Tensor, hidden: Tensor) -> tuple[Tensor, Tensor, Tensor]:
        features = self.head(torch.cat((hidden, encoded), dim=-1))
        mean = self.actor_mean(features)
        log_std = self.actor_log_std.clamp(LOG_STD_MIN, LOG_STD_MAX).expand_as(mean)
        value = self.critic(features).squeeze(-1)
        return mean, log_std, value

    def step(self, observations: Tensor, hidden: Tensor) -> tuple[Tensor, Tensor, Tensor, Tensor]:
        """One tick for N agents: (mean, log_std, value, new hidden)."""
        if observations.shape[-1] != self.observation_dim:
            raise ValueError(f"expected observation dim {self.observation_dim}, got {observations.shape[-1]}")
        encoded = self.encoder(observations)
        _, new_hidden = self.memory(encoded.unsqueeze(0), hidden.unsqueeze(0).contiguous())
        new_hidden = new_hidden.squeeze(0)
        mean, log_std, value = self._outputs(encoded, new_hidden)
        return mean, log_std, value, new_hidden

    def sequence(self, observations: Tensor, initial_hidden: Tensor, starts: Tensor) -> tuple[Tensor, Tensor, Tensor]:
        """Replays T steps; memory is zeroed where ``starts`` marks a new life (or an empty slot).

        Runs one fused GRU call per segment between ticks where any slot restarts (deaths are
        rare, so a 64-step rollout is usually one or two segments) instead of T GRUCell steps."""
        steps, agents, _ = observations.shape
        encoded = self.encoder(observations.reshape(steps * agents, -1)).reshape(steps, agents, -1)
        restarts = (starts[1:].any(dim=1).nonzero().flatten() + 1).tolist()  # one device sync
        cuts = [0] + restarts + [steps]
        hidden = initial_hidden.unsqueeze(0)
        outputs = []
        for begin, end in zip(cuts[:-1], cuts[1:]):
            hidden = hidden * (~starts[begin]).unsqueeze(-1).to(hidden.dtype)
            segment, hidden = self.memory(encoded[begin:end], hidden.contiguous())
            outputs.append(segment)
        memory = torch.cat(outputs)
        return self._outputs(encoded, memory)

    def _load_from_state_dict(self, state_dict, prefix, *args, **kwargs):
        # First v3 runs (v3-gru-a) stored a GRUCell: memory.weight_ih -> memory.weight_ih_l0, etc.
        for name in ("weight_ih", "weight_hh", "bias_ih", "bias_hh"):
            old = f"{prefix}memory.{name}"
            if old in state_dict:
                state_dict[f"{old}_l0"] = state_dict.pop(old)
        super()._load_from_state_dict(state_dict, prefix, *args, **kwargs)

    @torch.no_grad()
    def act_step(
        self, observations: Tensor, hidden: Tensor, *, deterministic: bool = False
    ) -> tuple[Tensor, Tensor, Tensor, Tensor, Tensor]:
        """(squashed action, raw action, log-probability, value, new hidden)."""
        mean, log_std, value, new_hidden = self.step(observations, hidden)
        raw = mean if deterministic else mean + log_std.exp() * torch.randn_like(mean)
        action = torch.tanh(raw)
        return action, raw, squashed_log_probability(raw, mean, log_std, action), value, new_hidden

    def evaluate_sequence(
        self, observations: Tensor, raw_actions: Tensor, initial_hidden: Tensor, starts: Tensor, *, amp: bool = False
    ) -> tuple[Tensor, Tensor, Tensor]:
        with torch.autocast("cuda", dtype=torch.float16, enabled=amp):
            mean, log_std, value = self.sequence(observations, initial_hidden, starts)
        # Probability maths in fp32: PPO ratios are exp(differences) and lose precision in fp16.
        mean, log_std, value = mean.float(), log_std.float(), value.float()
        actions = torch.tanh(raw_actions)
        log_probability = squashed_log_probability(raw_actions, mean, log_std, actions)
        entropy = (log_std + 0.5 * (1.0 + np.log(2.0 * np.pi))).sum(dim=-1)
        return log_probability, entropy, value


def squashed_log_probability(raw: Tensor, mean: Tensor, log_std: Tensor, action: Tensor) -> Tensor:
    variance = (2.0 * log_std).exp()
    normal = -0.5 * (((raw - mean).square() / variance) + 2.0 * log_std + np.log(2.0 * np.pi))
    correction = torch.log(1.0 - action.square() + 1e-6)
    return (normal - correction).sum(dim=-1)


class RandomNetworkDistillation(nn.Module):
    """Curiosity: a fixed random target network and a predictor trained to imitate it. Situations
    seen often become predictable (low error); new ones are not — the error is the novelty bonus."""

    def __init__(self, observation_dim: int = OBSERVATION_DIM, embedding: int = 256) -> None:
        super().__init__()
        self.target = nn.Sequential(nn.Linear(observation_dim, 512), nn.LeakyReLU(), nn.Linear(512, embedding))
        self.predictor = nn.Sequential(
            nn.Linear(observation_dim, 512), nn.LeakyReLU(),
            nn.Linear(512, 512), nn.LeakyReLU(),
            nn.Linear(512, embedding),
        )
        for parameter in self.target.parameters():
            parameter.requires_grad_(False)

    def error(self, observations: Tensor) -> Tensor:
        x = observations.clamp(-5.0, 5.0)
        return (self.predictor(x) - self.target(x)).square().mean(dim=-1)


@dataclass(slots=True)
class SequenceBatch:
    observations: Tensor  # (T, N, D)
    actions: Tensor  # (T, N, A) raw pre-tanh
    old_log_probabilities: Tensor  # (T, N)
    returns: Tensor  # (T, N)
    advantages: Tensor  # (T, N), normalised over valid entries
    valid: Tensor  # (T, N) bool
    starts: Tensor  # (T, N) bool: memory zeroed before this step
    initial_hidden: Tensor  # (N, H)

    def slot_minibatches(self, slots_per_batch: int, *, generator: torch.Generator | None = None) -> Iterator["SequenceBatch"]:
        agents = self.observations.shape[1]
        order = torch.randperm(agents, device=self.observations.device, generator=generator)
        for start in range(0, agents, slots_per_batch):
            index = order[start : start + slots_per_batch]
            yield SequenceBatch(
                self.observations[:, index], self.actions[:, index], self.old_log_probabilities[:, index],
                self.returns[:, index], self.advantages[:, index], self.valid[:, index], self.starts[:, index],
                self.initial_hidden[index],
            )


class SequenceRolloutBuffer:
    """Time-major rollout that keeps whole slot sequences (memory needs the order)."""

    def __init__(self) -> None:
        self.observations: list[Tensor] = []
        self.actions: list[Tensor] = []
        self.log_probabilities: list[Tensor] = []
        self.values: list[Tensor] = []
        self.rewards: list[Tensor] = []
        self.terminals: list[Tensor] = []
        self.valid: list[Tensor] = []
        self.starts: list[Tensor] = []
        self.initial_hidden: Tensor | None = None

    def __len__(self) -> int:
        return len(self.rewards)

    def add(self, observations: Tensor, actions: Tensor, log_probabilities: Tensor, values: Tensor,
            rewards: Tensor, terminals: Tensor, valid: Tensor, starts: Tensor, hidden_in: Tensor) -> None:
        if self.initial_hidden is None:
            self.initial_hidden = hidden_in.detach()
        elif hidden_in.shape != self.initial_hidden.shape:
            raise ValueError("slot layout changed inside a rollout")
        if bool((~valid & ~terminals).any()):
            raise ValueError("padding rows must be terminal")
        self.observations.append(observations.detach())
        self.actions.append(actions.detach())
        self.log_probabilities.append(log_probabilities.detach())
        self.values.append(values.detach())
        self.rewards.append(rewards.detach())
        self.terminals.append(terminals.detach().to(torch.bool))
        self.valid.append(valid.detach().to(torch.bool))
        self.starts.append(starts.detach().to(torch.bool))

    def finish(self, final_values: Tensor, *, discount: float, gae_lambda: float) -> SequenceBatch:
        if not self.rewards or self.initial_hidden is None:
            raise ValueError("cannot finish an empty rollout")
        rewards = torch.stack(self.rewards)
        values = torch.stack(self.values)
        terminals = torch.stack(self.terminals)
        advantages = torch.zeros_like(rewards)
        running = torch.zeros_like(final_values)
        next_value = final_values.detach()
        for t in range(len(self.rewards) - 1, -1, -1):
            nonterminal = (~terminals[t]).to(rewards.dtype)
            delta = rewards[t] + discount * next_value * nonterminal - values[t]
            running = delta + discount * gae_lambda * nonterminal * running
            advantages[t] = running
            next_value = values[t]
        returns = advantages + values
        valid = torch.stack(self.valid)
        if not bool(valid.any()):
            raise ValueError("rollout has no valid transitions")
        selected = advantages[valid]
        advantages = (advantages - selected.mean()) / (selected.std(unbiased=False) + 1e-8)
        # Slots kept free for births (slot capacity) that stayed empty the whole rollout carry no
        # data: drop them so the update only replays sequences that exist.
        used = valid.any(dim=0).nonzero().flatten()
        return SequenceBatch(
            torch.stack(self.observations)[:, used], torch.stack(self.actions)[:, used],
            torch.stack(self.log_probabilities)[:, used], returns[:, used], (advantages * valid)[:, used],
            valid[:, used], torch.stack(self.starts)[:, used], self.initial_hidden[used],
        )


@dataclass(frozen=True, slots=True)
class CuriosityConfig:
    """``weight`` multiplies the novelty bonus normalised to unit std; 0 disables curiosity."""

    weight: float = 0.02
    learning_rate: float = 1e-4


class RecurrentPPOTrainer:
    def __init__(self, model: RecurrentActorCritic, config: PPOConfig = PPOConfig(), *,
                 curiosity: RandomNetworkDistillation | None = None,
                 curiosity_config: CuriosityConfig = CuriosityConfig(), seed: int = 0) -> None:
        self.model = model
        self.config = config
        self.optimizer = torch.optim.Adam(model.parameters(), lr=config.learning_rate)
        self.curiosity = curiosity
        self.curiosity_optimizer = (
            torch.optim.Adam(curiosity.predictor.parameters(), lr=curiosity_config.learning_rate)
            if curiosity is not None else None
        )
        self.generator = torch.Generator(device=next(model.parameters()).device)
        self.generator.manual_seed(seed)
        self.amp = bool(config.amp) and next(model.parameters()).device.type == "cuda"
        self.scaler = torch.amp.GradScaler("cuda", enabled=self.amp)

    def update(self, batch: SequenceBatch) -> UpdateMetrics:
        config = self.config
        steps = batch.observations.shape[0]
        slots_per_batch = max(1, config.minibatch_size // max(1, steps))
        policy_losses, value_losses, entropies, norms = [], [], [], []
        for _ in range(config.epochs):
            for mb in batch.slot_minibatches(slots_per_batch, generator=self.generator):
                mask = mb.valid.to(torch.float32)
                count = mask.sum().clamp_min(1.0)
                log_probability, entropy, value = self.model.evaluate_sequence(
                    mb.observations, mb.actions, mb.initial_hidden, mb.starts, amp=self.amp
                )
                ratio = (log_probability - mb.old_log_probabilities).exp()
                unclipped = ratio * mb.advantages
                clipped = ratio.clamp(1.0 - config.clip_ratio, 1.0 + config.clip_ratio) * mb.advantages
                policy_loss = -(torch.minimum(unclipped, clipped) * mask).sum() / count
                value_loss = 0.5 * ((value - mb.returns).square() * mask).sum() / count
                entropy_mean = (entropy * mask).sum() / count
                loss = policy_loss + config.value_coefficient * value_loss - config.entropy_coefficient * entropy_mean
                if not torch.isfinite(loss):
                    raise FloatingPointError("non-finite PPO loss")
                self.optimizer.zero_grad(set_to_none=True)
                self.scaler.scale(loss).backward()
                self.scaler.unscale_(self.optimizer)  # clip the true gradient, not the scaled one
                norm = nn.utils.clip_grad_norm_(self.model.parameters(), config.max_gradient_norm)
                self.scaler.step(self.optimizer)
                self.scaler.update()
                policy_losses.append(float(policy_loss.detach()))
                value_losses.append(float(value_loss.detach()))
                entropies.append(float(entropy_mean.detach()))
                norms.append(float(norm.detach()))
        self._train_curiosity(batch)
        count = len(policy_losses)
        return UpdateMetrics(sum(policy_losses) / count, sum(value_losses) / count,
                             sum(entropies) / count, sum(norms) / count, count)

    def save_checkpoint(self, path, *, step: int, extra: dict | None = None) -> None:
        from dataclasses import asdict
        from pathlib import Path

        target = Path(path)
        target.parent.mkdir(parents=True, exist_ok=True)
        temporary = target.with_suffix(target.suffix + ".tmp")
        state = {
            "step": step,
            "brain": "gru",
            "model": self.model.state_dict(),
            "optimizer": self.optimizer.state_dict(),
            "config": asdict(self.config),
            "extra": extra or {},
        }
        if self.curiosity is not None and self.curiosity_optimizer is not None:
            state["curiosity"] = self.curiosity.state_dict()
            state["curiosity_optimizer"] = self.curiosity_optimizer.state_dict()
        torch.save(state, temporary)
        temporary.replace(target)  # atomic: a killed run never leaves a truncated checkpoint

    def load_checkpoint_with_extra(self, path) -> tuple[int, dict]:
        checkpoint = torch.load(path, map_location=next(self.model.parameters()).device, weights_only=False)
        if checkpoint.get("brain") != "gru":
            raise ValueError(f"{path} is not a brain v3 (gru) checkpoint; use --brain mlp to resume it")
        self.model.load_state_dict(checkpoint["model"])
        self.optimizer.load_state_dict(checkpoint["optimizer"])
        if self.curiosity is not None and "curiosity" in checkpoint:
            self.curiosity.load_state_dict(checkpoint["curiosity"])
            assert self.curiosity_optimizer is not None
            self.curiosity_optimizer.load_state_dict(checkpoint["curiosity_optimizer"])
        return int(checkpoint["step"]), dict(checkpoint.get("extra", {}))

    def _train_curiosity(self, batch: SequenceBatch) -> None:
        if self.curiosity is None or self.curiosity_optimizer is None:
            return
        observations = batch.observations[batch.valid]
        order = torch.randperm(observations.shape[0], device=observations.device, generator=self.generator)
        for start in range(0, observations.shape[0], self.config.minibatch_size):
            chunk = observations[order[start : start + self.config.minibatch_size]]
            loss = self.curiosity.error(chunk).mean()
            self.curiosity_optimizer.zero_grad(set_to_none=True)
            loss.backward()
            self.curiosity_optimizer.step()
