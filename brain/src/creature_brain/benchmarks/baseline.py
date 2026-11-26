"""Aggregate matched-seed random and PPO survival runs.

This module deliberately does not fabricate a learned result. It consumes
episode records emitted by the real Godot run, writes auditable artifacts and
marks each empirical gate PASS or FAIL. A missing run is an error, not a pass.
"""

from __future__ import annotations

import argparse
import csv
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
import json
from pathlib import Path
from typing import Iterable, Literal, Sequence

import numpy as np


PolicyName = Literal["random", "ppo"]


@dataclass(frozen=True, slots=True)
class EpisodeRecord:
    policy: PolicyName
    seed: int
    creature_id: int
    lifetime_seconds: float
    low_energy_steps: int = 0
    ate_while_low_energy: int = 0
    low_water_steps: int = 0
    drank_while_low_water: int = 0
    death_cause: str = "unknown"
    config_id: str = "default"
    horizon: int = 0
    rtt_ms: float = 0.0
    tick_loss: int = 0
    realtime_factor: float = 0.0

    @classmethod
    def from_mapping(cls, value: dict) -> "EpisodeRecord":
        policy = value.get("policy")
        if policy not in ("random", "ppo"):
            raise ValueError("policy must be random or ppo")
        record = cls(
            policy=policy,
            seed=int(value["seed"]),
            creature_id=int(value["creature_id"]),
            lifetime_seconds=float(value["lifetime_seconds"]),
            low_energy_steps=int(value.get("low_energy_steps", 0)),
            ate_while_low_energy=int(value.get("ate_while_low_energy", 0)),
            low_water_steps=int(value.get("low_water_steps", 0)),
            drank_while_low_water=int(value.get("drank_while_low_water", 0)),
            death_cause=str(value.get("death_cause", "unknown")),
            config_id=str(value.get("config_id", "default")),
            horizon=int(value.get("horizon", 0)),
            rtt_ms=float(value.get("rtt_ms", 0.0)),
            tick_loss=int(value.get("tick_loss", 0)),
            realtime_factor=float(value.get("realtime_factor", 0.0)),
        )
        record.validate()
        return record

    def validate(self) -> None:
        if self.lifetime_seconds < 0 or not np.isfinite(self.lifetime_seconds):
            raise ValueError("lifetime must be finite and non-negative")
        if not np.isfinite(self.rtt_ms) or self.rtt_ms < 0 or self.tick_loss < 0:
            raise ValueError("invalid bridge metrics")
        counts = (
            self.low_energy_steps,
            self.ate_while_low_energy,
            self.low_water_steps,
            self.drank_while_low_water,
        )
        if any(value < 0 for value in counts):
            raise ValueError("conditioned counts must be non-negative")
        if self.ate_while_low_energy > self.low_energy_steps:
            raise ValueError("energy response exceeds opportunities")
        if self.drank_while_low_water > self.low_water_steps:
            raise ValueError("water response exceeds opportunities")


@dataclass(frozen=True, slots=True)
class BenchmarkSummary:
    random_episodes: int
    ppo_episodes: int
    matched_seeds: tuple[int, ...]
    random_mean_lifetime: float
    ppo_mean_lifetime: float
    lifetime_ratio: float
    lifetime_ratio_ci95: tuple[float, float]
    random_food_response_rate: float
    ppo_food_response_rate: float
    random_water_response_rate: float
    ppo_water_response_rate: float
    survival_gate: Literal["PASS", "FAIL"]
    learned_search_gate: Literal["PASS", "FAIL"]

    @property
    def overall_gate(self) -> Literal["PASS", "FAIL"]:
        return (
            "PASS"
            if self.survival_gate == "PASS" and self.learned_search_gate == "PASS"
            else "FAIL"
        )


def _rate(records: Sequence[EpisodeRecord], numerator: str, denominator: str) -> float:
    opportunities = sum(getattr(record, denominator) for record in records)
    if opportunities == 0:
        return 0.0
    return sum(getattr(record, numerator) for record in records) / opportunities


def _bootstrap_ratio(
    random_lifetimes: np.ndarray,
    ppo_lifetimes: np.ndarray,
    *,
    seed: int,
    samples: int,
) -> tuple[float, float]:
    if samples <= 0:
        raise ValueError("bootstrap samples must be positive")
    generator = np.random.default_rng(seed)
    ratios = np.empty(samples, dtype=np.float64)
    for index in range(samples):
        random_sample = generator.choice(random_lifetimes, len(random_lifetimes), replace=True)
        ppo_sample = generator.choice(ppo_lifetimes, len(ppo_lifetimes), replace=True)
        denominator = max(float(random_sample.mean()), 1e-9)
        ratios[index] = float(ppo_sample.mean()) / denominator
    lower, upper = np.quantile(ratios, (0.025, 0.975))
    return float(lower), float(upper)


def evaluate(
    records: Iterable[EpisodeRecord], *, bootstrap_seed: int = 0, bootstrap_samples: int = 2_000
) -> BenchmarkSummary:
    all_records = tuple(records)
    for record in all_records:
        record.validate()
    random_records = tuple(record for record in all_records if record.policy == "random")
    ppo_records = tuple(record for record in all_records if record.policy == "ppo")
    if not random_records or not ppo_records:
        raise ValueError("benchmark requires both random and PPO episodes")
    random_seeds = {record.seed for record in random_records}
    ppo_seeds = {record.seed for record in ppo_records}
    if random_seeds != ppo_seeds:
        raise ValueError("random and PPO runs must use the same seeds")
    def episode_keys(items: Sequence[EpisodeRecord]) -> set[tuple[int, int, str, int]]:
        return {(r.seed, r.creature_id, r.config_id, r.horizon) for r in items}
    random_keys, ppo_keys = episode_keys(random_records), episode_keys(ppo_records)
    if len(random_keys) != len(random_records) or len(ppo_keys) != len(ppo_records):
        raise ValueError("duplicate episode key")
    if random_keys != ppo_keys:
        raise ValueError("random and PPO episodes must be paired by seed, creature, config and horizon")
    random_lifetimes = np.asarray(
        [record.lifetime_seconds for record in random_records], dtype=np.float64
    )
    ppo_lifetimes = np.asarray(
        [record.lifetime_seconds for record in ppo_records], dtype=np.float64
    )
    random_mean = float(random_lifetimes.mean())
    ppo_mean = float(ppo_lifetimes.mean())
    if random_mean <= 0:
        raise ValueError("random baseline mean lifetime must be positive")
    ratio = ppo_mean / random_mean
    food_random = _rate(
        random_records, "ate_while_low_energy", "low_energy_steps"
    )
    food_ppo = _rate(ppo_records, "ate_while_low_energy", "low_energy_steps")
    water_random = _rate(
        random_records, "drank_while_low_water", "low_water_steps"
    )
    water_ppo = _rate(ppo_records, "drank_while_low_water", "low_water_steps")
    return BenchmarkSummary(
        random_episodes=len(random_records),
        ppo_episodes=len(ppo_records),
        matched_seeds=tuple(sorted(random_seeds)),
        random_mean_lifetime=random_mean,
        ppo_mean_lifetime=ppo_mean,
        lifetime_ratio=ratio,
        lifetime_ratio_ci95=_bootstrap_ratio(
            random_lifetimes,
            ppo_lifetimes,
            seed=bootstrap_seed,
            samples=bootstrap_samples,
        ),
        random_food_response_rate=food_random,
        ppo_food_response_rate=food_ppo,
        random_water_response_rate=water_random,
        ppo_water_response_rate=water_ppo,
        survival_gate="PASS" if ratio >= 5.0 else "FAIL",
        learned_search_gate=(
            "PASS" if food_ppo > food_random and water_ppo > water_random else "FAIL"
        ),
    )


def read_jsonl(paths: Sequence[Path]) -> tuple[EpisodeRecord, ...]:
    records: list[EpisodeRecord] = []
    for path in paths:
        for line_number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            if not line.strip():
                continue
            try:
                records.append(EpisodeRecord.from_mapping(json.loads(line)))
            except (KeyError, TypeError, ValueError, json.JSONDecodeError) as exc:
                raise ValueError(f"invalid record at {path}:{line_number}: {exc}") from exc
    return tuple(records)


def write_artifacts(
    output_dir: Path,
    records: Sequence[EpisodeRecord],
    summary: BenchmarkSummary,
    *,
    config: dict,
) -> None:
    output_dir.mkdir(parents=True, exist_ok=True)
    config_document = {
        **config,
        "created_at": datetime.now(timezone.utc).isoformat(),
        "required_lifetime_ratio": 5.0,
        "observation_privilege": "public_observation_vector_only",
    }
    (output_dir / "config.json").write_text(
        json.dumps(config_document, indent=2, sort_keys=True), encoding="utf-8"
    )
    with (output_dir / "events.jsonl").open("w", encoding="utf-8") as stream:
        for record in records:
            stream.write(json.dumps(asdict(record), sort_keys=True) + "\n")
    summary_mapping = asdict(summary) | {"overall_gate": summary.overall_gate}
    (output_dir / "summary.json").write_text(
        json.dumps(summary_mapping, indent=2, sort_keys=True), encoding="utf-8"
    )
    with (output_dir / "summary.csv").open(
        "w", newline="", encoding="utf-8"
    ) as stream:
        writer = csv.writer(stream)
        writer.writerow(("metric", "value"))
        for key, value in summary_mapping.items():
            writer.writerow((key, json.dumps(value) if isinstance(value, (tuple, list)) else value))


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Evaluate random versus PPO survival")
    parser.add_argument("inputs", nargs="+", type=Path, help="episode JSONL files")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--bootstrap-seed", type=int, default=0)
    parser.add_argument("--bootstrap-samples", type=int, default=2_000)
    return parser.parse_args()


def main() -> None:
    args = _parse_args()
    records = read_jsonl(args.inputs)
    summary = evaluate(
        records,
        bootstrap_seed=args.bootstrap_seed,
        bootstrap_samples=args.bootstrap_samples,
    )
    write_artifacts(
        args.output,
        records,
        summary,
        config={
            "inputs": [str(path) for path in args.inputs],
            "bootstrap_seed": args.bootstrap_seed,
            "bootstrap_samples": args.bootstrap_samples,
        },
    )
    print(json.dumps(asdict(summary) | {"overall_gate": summary.overall_gate}, indent=2))


if __name__ == "__main__":  # pragma: no cover
    main()
