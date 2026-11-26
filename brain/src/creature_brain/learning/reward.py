"""Homeostatic reward computed strictly from interoception and events."""

from __future__ import annotations

from dataclasses import dataclass
import math
from typing import Sequence


INTEROCEPTION_DIM = 12


@dataclass(frozen=True, slots=True)
class RewardConfig:
    energy_setpoint: float = 0.8
    water_setpoint: float = 0.8
    temperature_setpoint: float = 37.0
    integrity_setpoint: float = 1.0
    fatigue_setpoint: float = 0.15
    libido_setpoint: float = 0.5
    energy_weight: float = 1.0
    water_weight: float = 1.0
    temperature_weight: float = 0.08
    integrity_weight: float = 1.25
    fatigue_weight: float = 0.35
    libido_weight: float = 0.05
    pain_weight: float = 0.5
    # Fear is a feeling (Task 002): punished so the brain learns what makes it go away. The weight
    # becomes a gene in etapa 2; kept below pain so fear alone never dominates (paralysis risk).
    fear_weight: float = 0.3
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
