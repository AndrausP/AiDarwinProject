"""Inherited instinct (T2.6a): a tiny per-individual network added to the learned policy.

``action = base + alpha(age) * instinct(reduced observation)``. The instinct is a *gene*: a
newborn's weights are a uniform crossover of its parents' weights plus mutation, founders get small
random weights, and nothing learned in life is ever written into them (Darwin rule). Gradient
descent never touches it; it changes only because individuals whose instinct helps them breed pass
it on. ``alpha`` falls with age, so instinct steers the young and the learned brain takes over.

Reduced observation (89 floats of protocol v3): creature sectors, audio, olfaction,
proprioception, interoception and time -- everything but the 1008 vision features and the genes.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import struct
from typing import Protocol, Sequence

import numpy as np

from creature_brain import ACTION_DIM, INTEROCEPTION_OFFSET
from creature_brain.bridge.protocol import EventType, Step

REDUCED_START = 1008  # creature sectors .. end of the time block
REDUCED_END = 1097
REDUCED_DIM = REDUCED_END - REDUCED_START  # 89
AGE_FRACTION_INDEX = INTEROCEPTION_OFFSET + 9
HIDDEN = 32
WEIGHT_COUNT = REDUCED_DIM * HIDDEN + HIDDEN + HIDDEN * ACTION_DIM + ACTION_DIM  # 3309


@dataclass(frozen=True, slots=True)
class InstinctConfig:
    alpha0: float = 0.5
    # alpha = alpha0 * exp(-age_fraction / decay): ~0.37 * alpha0 at 5 % of the longevity.
    decay: float = 0.05
    mutation_rate: float = 0.05
    mutation_scale: float = 0.05
    founder_scale: float = 0.1
    seed: int = 0


class _Policy(Protocol):
    def act(self, step: Step) -> tuple[tuple[float, ...], ...]: ...


class InstinctPool:
    """Instinct genomes of the living creatures, keyed by id."""

    def __init__(self, config: InstinctConfig = InstinctConfig()) -> None:
        self.config = config
        self.rng = np.random.default_rng(config.seed)
        self.weights: dict[int, np.ndarray] = {}

    def founder(self) -> np.ndarray:
        return (self.rng.standard_normal(WEIGHT_COUNT) * self.config.founder_scale).astype(np.float32)

    def child(self, mother: np.ndarray, father: np.ndarray) -> np.ndarray:
        take_mother = self.rng.random(WEIGHT_COUNT) < 0.5
        genome = np.where(take_mother, mother, father).astype(np.float32)
        mutate = self.rng.random(WEIGHT_COUNT) < self.config.mutation_rate
        genome[mutate] += (self.rng.standard_normal(int(mutate.sum())) * self.config.mutation_scale).astype(np.float32)
        return genome

    def observe_births(self, step: Step) -> None:
        for event in step.events:
            if event.event_type is EventType.BIRTH and len(event.payload) >= 12:
                child, mother, father = struct.unpack_from("<III", event.payload)
                if mother in self.weights and father in self.weights:
                    self.weights[child] = self.child(self.weights[mother], self.weights[father])
                elif mother in self.weights:
                    self.weights[child] = self.child(self.weights[mother], self.weights[mother])

    def ensure(self, creature_ids: Sequence[int]) -> None:
        for creature_id in creature_ids:
            if creature_id not in self.weights:
                self.weights[creature_id] = self.founder()

    def forget_dead(self, alive: Sequence[int]) -> None:
        keep = set(alive)
        # Parents stay while alive; the dead leave (their genes live on in their young).
        for creature_id in [k for k in self.weights if k not in keep]:
            del self.weights[creature_id]

    def alpha(self, age_fraction: np.ndarray) -> np.ndarray:
        return self.config.alpha0 * np.exp(-np.clip(age_fraction, 0.0, 1.0) / self.config.decay)

    def output(self, creature_ids: Sequence[int], observations: np.ndarray) -> np.ndarray:
        """(N, ACTION_DIM) alpha-scaled instinct for the creatures of a step."""
        if not creature_ids:
            return np.zeros((0, ACTION_DIM), dtype=np.float32)
        w = np.stack([self.weights[i] for i in creature_ids])
        x = np.clip(observations[:, REDUCED_START:REDUCED_END], -5.0, 5.0).astype(np.float32)
        o = 0
        w1 = w[:, o:o + REDUCED_DIM * HIDDEN].reshape(-1, REDUCED_DIM, HIDDEN); o += REDUCED_DIM * HIDDEN
        b1 = w[:, o:o + HIDDEN]; o += HIDDEN
        w2 = w[:, o:o + HIDDEN * ACTION_DIM].reshape(-1, HIDDEN, ACTION_DIM); o += HIDDEN * ACTION_DIM
        b2 = w[:, o:o + ACTION_DIM]
        hidden = np.tanh(np.einsum("ni,nih->nh", x, w1) + b1)
        out = np.tanh(np.einsum("nh,nha->na", hidden, w2) + b2)
        alpha = self.alpha(observations[:, AGE_FRACTION_INDEX]).astype(np.float32)
        return out * alpha[:, None]

    def save(self, path: Path) -> None:
        ids = np.array(sorted(self.weights), dtype=np.uint32)
        weights = np.stack([self.weights[int(i)] for i in ids]) if len(ids) else np.zeros((0, WEIGHT_COUNT), np.float32)
        temporary = path.with_suffix(".tmp.npz")
        np.savez(temporary, ids=ids, weights=weights)
        temporary.replace(path)

    def load(self, path: Path) -> None:
        data = np.load(path)
        self.weights = {int(i): w.astype(np.float32) for i, w in zip(data["ids"], data["weights"])}


class InstinctPolicy:
    """Wraps a policy: adds each creature's inherited instinct to the action it chose."""

    def __init__(self, base: _Policy, pool: InstinctPool, save_path: Path | None = None) -> None:
        self.base = base
        self.pool = pool
        self.save_path = save_path
        if save_path is not None and save_path.exists():
            pool.load(save_path)

    def act(self, step: Step) -> tuple[tuple[float, ...], ...]:
        actions = self.base.act(step)
        self.pool.observe_births(step)
        ids = list(step.creature_ids)
        self.pool.ensure(ids)
        array = step.observation_array
        if array is None:
            array = np.asarray(step.observations, dtype=np.float32).reshape(len(ids), -1)
        bias = self.pool.output(ids, np.asarray(array))
        combined = np.clip(np.asarray(actions, dtype=np.float32).reshape(len(ids), ACTION_DIM) + bias, -1.0, 1.0)
        self.pool.forget_dead(ids)
        return tuple(tuple(float(v) for v in row) for row in combined)

    def flush(self) -> None:
        if hasattr(self.base, "flush"):
            self.base.flush()
        if self.save_path is not None:
            self.pool.save(self.save_path)

    def new_session(self) -> "InstinctPolicy":
        """Per-connection trajectory state of the base policy; instinct genomes are shared."""
        base_session = getattr(self.base, "new_session", None)
        if not callable(base_session):
            return self
        session = InstinctPolicy(base_session(), self.pool)
        session.save_path = self.save_path
        return session

    def __getattr__(self, name: str):
        return getattr(self.base, name)
