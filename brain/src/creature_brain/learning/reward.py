"""Homeostatic reward computed strictly from interoception and events."""

from __future__ import annotations

from dataclasses import dataclass
import math
from typing import Sequence

import numpy as np


INTEROCEPTION_DIM = 12


@dataclass(frozen=True, slots=True)
class RewardConfig:
    energy_setpoint: float = 0.8
    water_setpoint: float = 0.8
    temperature_setpoint: float = 37.0
    integrity_setpoint: float = 1.0
    fatigue_setpoint: float = 0.15
    # Etapa 2: libido builds in fed adults and mating resets it, so the setpoint is satisfaction (0).
    libido_setpoint: float = 0.0
    energy_weight: float = 1.0
    water_weight: float = 1.0
    temperature_weight: float = 0.08
    integrity_weight: float = 1.25
    fatigue_weight: float = 0.35
    libido_weight: float = 0.3
    pain_weight: float = 0.5
    # Fear is a feeling (Task 002): punished so the brain learns what makes it go away. The weight
    # becomes a gene in etapa 2; kept below pain so fear alone never dominates (paralysis risk).
    fear_weight: float = 0.3
    # Parental care (D3): reward per unit of energy handed to one's own young (FED event).
    care_weight: float = 0.5
    care_scale: float = 10.0
    death_penalty: float = 1.0


@dataclass(frozen=True, slots=True)
class RewardResult:
    total: float
    homeostatic: float
    pain_penalty: float
    fear_penalty: float
    death_penalty: float


class HomeostaticReward:
    """Rewards reductions in deviation from physiological setpoints.

    Input layout is the 12-element interoceptive slice of observation v2:
    energy, water, temperature, head, torso, legs, fatigue, pain, libido, age,
    mature, fear. Age and maturity are intentionally ignored by the reward.
    """

    def __init__(self, config: RewardConfig = RewardConfig()) -> None:
        self.config = config

    def compute(
        self,
        previous: Sequence[float],
        current: Sequence[float],
        *,
        died: bool = False,
    ) -> RewardResult:
        previous_values = self._validate(previous)
        current_values = self._validate(current)
        config = self.config
        previous_drives = self._drives(previous_values)
        current_drives = self._drives(current_values)
        setpoints = (
            config.energy_setpoint,
            config.water_setpoint,
            config.temperature_setpoint,
            config.integrity_setpoint,
            config.fatigue_setpoint,
            config.libido_setpoint,
        )
        weights = (
            config.energy_weight,
            config.water_weight,
            config.temperature_weight,
            config.integrity_weight,
            config.fatigue_weight,
            config.libido_weight,
        )
        homeostatic = sum(
            weight
            * (
                abs(previous_value - setpoint)
                - abs(current_value - setpoint)
            )
            for previous_value, current_value, setpoint, weight in zip(
                previous_drives, current_drives, setpoints, weights, strict=True
            )
        )
        pain_penalty = config.pain_weight * max(current_values[7], 0.0)
        fear_penalty = config.fear_weight * min(max(current_values[11], 0.0), 1.0)
        death_penalty = config.death_penalty if died else 0.0
        total = homeostatic - pain_penalty - fear_penalty - death_penalty
        return RewardResult(total, homeostatic, pain_penalty, fear_penalty, death_penalty)

    #: Column order of per-individual motivation weights (T2.2e, decision D3: weights become genes).
    MOTIVATION_ORDER = ("energy", "water", "temperature", "integrity", "fatigue", "libido", "pain", "fear", "care")

    def default_motivations(self, count: int) -> np.ndarray:
        """(count, 9) weights equal to this config (the motivation genes' defaults)."""
        c = self.config
        row = [c.energy_weight, c.water_weight, c.temperature_weight, c.integrity_weight,
               c.fatigue_weight, c.libido_weight, c.pain_weight, c.fear_weight, c.care_weight]
        return np.tile(np.asarray(row, dtype=np.float64), (count, 1))

    def compute_totals(
        self, previous: np.ndarray, current: np.ndarray, died: np.ndarray, motivations: np.ndarray | None = None,
        fed: np.ndarray | None = None,
    ) -> np.ndarray:
        """Vectorised ``compute(...).total`` for N creatures: (N, 12) arrays and an (N,) bool mask.
        Same formula as :meth:`compute`; per-creature Python was ~20 ms per tick at 288 creatures.

        ``motivations`` (N, 9), in :attr:`MOTIVATION_ORDER`, gives each individual its own weights
        (motivations as genes, D3); None uses the config for everyone. ``fed`` (N,) is the energy each
        creature handed to its own young this step (FED events), rewarded by the care weight."""
        previous = np.asarray(previous, dtype=np.float64)
        current = np.asarray(current, dtype=np.float64)
        if previous.ndim != 2 or previous.shape[1] != INTEROCEPTION_DIM or previous.shape != current.shape:
            raise ValueError(f"interoception must be (N, {INTEROCEPTION_DIM}) arrays of equal shape")
        if not (np.isfinite(previous).all() and np.isfinite(current).all()):
            raise ValueError("interoception contains non-finite values")
        config = self.config
        setpoints = np.array([config.energy_setpoint, config.water_setpoint, config.temperature_setpoint,
                              config.integrity_setpoint, config.fatigue_setpoint, config.libido_setpoint])
        if motivations is None:
            motivations = self.default_motivations(previous.shape[0])
        motivations = np.asarray(motivations, dtype=np.float64)
        if motivations.shape != (previous.shape[0], len(self.MOTIVATION_ORDER)):
            raise ValueError(f"motivations must be (N, {len(self.MOTIVATION_ORDER)})")
        if not np.isfinite(motivations).all() or (motivations < 0).any():
            raise ValueError("motivation weights must be finite and non-negative")

        def drives(values: np.ndarray) -> np.ndarray:
            integrity = (values[:, 3] + values[:, 4] + values[:, 5]) / 3.0
            return np.stack((values[:, 0], values[:, 1], values[:, 2], integrity, values[:, 6], values[:, 8]), axis=1)

        homeostatic = ((np.abs(drives(previous) - setpoints) - np.abs(drives(current) - setpoints)) * motivations[:, :6]).sum(axis=1)
        pain_penalty = motivations[:, 6] * np.maximum(current[:, 7], 0.0)
        fear_penalty = motivations[:, 7] * np.clip(current[:, 11], 0.0, 1.0)
        death_penalty = np.where(np.asarray(died, dtype=bool), config.death_penalty, 0.0)
        care = 0.0 if fed is None else motivations[:, 8] * config.care_scale * np.maximum(np.asarray(fed, dtype=np.float64), 0.0)
        return homeostatic + care - pain_penalty - fear_penalty - death_penalty

    @staticmethod
    def _drives(values: tuple[float, ...]) -> tuple[float, ...]:
        integrity = (values[3] + values[4] + values[5]) / 3.0
        return values[0], values[1], values[2], integrity, values[6], values[8]

    @staticmethod
    def _validate(values: Sequence[float]) -> tuple[float, ...]:
        result = tuple(float(value) for value in values)
        if len(result) != INTEROCEPTION_DIM:
            raise ValueError(f"interoception must have {INTEROCEPTION_DIM} values")
        if not all(math.isfinite(value) for value in result):
            raise ValueError("interoception contains non-finite values")
        return result
