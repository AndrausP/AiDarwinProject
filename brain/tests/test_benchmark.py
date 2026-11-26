import json
from pathlib import Path

import pytest

from creature_brain.benchmarks.baseline import (
    EpisodeRecord,
    evaluate,
    write_artifacts,
)


def _records(ppo_lifetime: float = 60.0) -> tuple[EpisodeRecord, ...]:
    records: list[EpisodeRecord] = []
    for seed in (1, 2, 3):
        records.append(
            EpisodeRecord(
                "random",
                seed,
                seed,
                10.0,
                low_energy_steps=100,
                ate_while_low_energy=5,
                low_water_steps=100,
                drank_while_low_water=4,
                death_cause="hunger",
            )
        )
        records.append(
            EpisodeRecord(
                "ppo",
                seed,
                seed,
                ppo_lifetime,
                low_energy_steps=100,
                ate_while_low_energy=40,
                low_water_steps=100,
                drank_while_low_water=35,
                death_cause="age",
            )
        )
    return tuple(records)


def test_should_pass_empirical_gates_when_ratio_and_search_improve() -> None:
    summary = evaluate(_records(), bootstrap_seed=3, bootstrap_samples=100)

    assert summary.lifetime_ratio == pytest.approx(6.0)
    assert summary.survival_gate == "PASS"
    assert summary.learned_search_gate == "PASS"
    assert summary.overall_gate == "PASS"


def test_should_fail_survival_gate_when_lifetime_is_below_target() -> None:
    summary = evaluate(_records(ppo_lifetime=40.0), bootstrap_samples=50)

    assert summary.survival_gate == "FAIL"
    assert summary.overall_gate == "FAIL"


def test_should_reject_benchmark_when_seeds_are_not_matched() -> None:
    with pytest.raises(ValueError, match="same seeds"):
        evaluate(
            (
                EpisodeRecord("random", 1, 1, 10.0),
                EpisodeRecord("ppo", 2, 1, 60.0),
            ),
            bootstrap_samples=10,
        )


def test_should_reject_zero_lifetime_random_baseline() -> None:
    with pytest.raises(ValueError, match="must be positive"):
        evaluate((EpisodeRecord("random", 1, 1, 0.0), EpisodeRecord("ppo", 1, 1, 10.0)))


def test_should_reject_unpaired_episode_counts_for_same_seed() -> None:
    with pytest.raises(ValueError, match="paired"):
        evaluate((EpisodeRecord("random", 1, 1, 10.0), EpisodeRecord("ppo", 1, 2, 60.0)))


def test_should_write_auditable_artifacts_when_evaluation_finishes(tmp_path: Path) -> None:
    records = _records()
    summary = evaluate(records, bootstrap_samples=20)

    write_artifacts(tmp_path, records, summary, config={"world_seed": 1})

    assert (tmp_path / "events.jsonl").read_text(encoding="utf-8").count("\n") == 6
    stored = json.loads((tmp_path / "summary.json").read_text(encoding="utf-8"))
    assert stored["overall_gate"] == "PASS"
    assert json.loads((tmp_path / "config.json").read_text(encoding="utf-8"))[
        "observation_privilege"
    ] == "public_observation_vector_only"
