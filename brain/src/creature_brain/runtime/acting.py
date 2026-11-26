"""Batched inference adapter for the lockstep bridge."""

from __future__ import annotations

from typing import Sequence

import torch

from creature_brain.bridge.protocol import Step
from creature_brain.learning.ppo import SharedActorCritic


class TorchPolicy:
    def __init__(
        self,
        model: SharedActorCritic,
        *,
        device: str | torch.device = "cpu",
        deterministic: bool = False,
    ) -> None:
        self.device = torch.device(device)
        self.model = model.to(self.device).eval()
        self.deterministic = deterministic

    def actions(
        self, creature_ids: Sequence[int], observations: Sequence[Sequence[float]]
    ) -> tuple[tuple[float, ...], ...]:
        if len(creature_ids) != len(observations):
            raise ValueError("observation count does not match ids")
        if not observations:
            return ()
        tensor = torch.as_tensor(observations, dtype=torch.float32, device=self.device)
        actions, _, _ = self.model.act(tensor, deterministic=self.deterministic)
        return tuple(tuple(float(value) for value in row) for row in actions.cpu().tolist())

    def act(self, step: Step) -> tuple[tuple[float, ...], ...]:
        return self.actions(step.creature_ids, step.observations)
