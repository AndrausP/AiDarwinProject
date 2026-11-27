"""Stage evaluation report (T1.4 / T1.8): several frozen policies against random on the reserved seed.

Input: one episode JSONL per policy (written by ``EpisodeRecorder``), labelled ``label=path``; the
label ``random`` is the baseline. Optional Sim exploit reports as ``label=path`` via --exploits.
Output: ``summary.json`` and ``summary.md`` with bootstrap 95 % intervals and the stage-1 criteria:
life >= 3x random, eating when hungry >= 2x, drinking when thirsty >= 2x, sleeping when tired > random.
Nothing is inferred: a policy without episodes is an error, never a pass.
"""

from __future__ import annotations

import argparse
from collections import Counter
from dataclasses import dataclass
import json
from pathlib import Path
from typing import Sequence

import numpy as np

CRITERIA = {"life_ratio": 3.0, "food_ratio": 2.0, "water_ratio": 2.0}


@dataclass(frozen=True, slots=True)
class PolicyStats:
    label: str
    episodes: int
    mean_life: float
    median_life: float
    mean_life_ci95: tuple[float, float]
    censored_share: float
    food_rate: float
    water_rate: float
    sleep_rate: float
    death_causes: dict[str, float]


def read_episodes(path: Path) -> list[dict]:
    rows = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]
    if not rows:
        raise ValueError(f"{path} has no episodes")
    return rows


def _rate(rows: Sequence[dict], numerator: str, denominator: str) -> float:
    opportunities = sum(int(r.get(denominator, 0)) for r in rows)
    return sum(int(r.get(numerator, 0)) for r in rows) / opportunities if opportunities else 0.0


def _bootstrap_mean(values: np.ndarray, generator: np.random.Generator, samples: int) -> np.ndarray:
    index = generator.integers(0, len(values), size=(samples, len(values)))
    return values[index].mean(axis=1)


def policy_stats(label: str, rows: Sequence[dict], generator: np.random.Generator, samples: int) -> PolicyStats:
    life = np.asarray([float(r["lifetime_seconds"]) for r in rows])
    boot = _bootstrap_mean(life, generator, samples)
    causes = Counter(str(r.get("death_cause", "unknown")) for r in rows)
    return PolicyStats(
        label=label,
        episodes=len(rows),
        mean_life=float(life.mean()),
        median_life=float(np.median(life)),
        mean_life_ci95=(float(np.quantile(boot, 0.025)), float(np.quantile(boot, 0.975))),
        censored_share=causes.get("alive_at_horizon", 0) / len(rows),
        food_rate=_rate(rows, "ate_while_low_energy", "low_energy_steps"),
        water_rate=_rate(rows, "drank_while_low_water", "low_water_steps"),
        sleep_rate=_rate(rows, "slept_while_tired", "high_fatigue_steps"),
        death_causes={k: v / len(rows) for k, v in causes.most_common()},
    )


def _ratio(a: float, b: float) -> float:
    return a / b if b > 0 else (float("inf") if a > 0 else 0.0)


def evaluate(episodes: dict[str, Sequence[dict]], *, seed: int = 0, samples: int = 2000) -> dict:
    if "random" not in episodes:
        raise ValueError("the baseline label 'random' is required")
    generator = np.random.default_rng(seed)
    stats = {label: policy_stats(label, rows, generator, samples) for label, rows in episodes.items()}
    base = stats["random"]
    base_life = np.asarray([float(r["lifetime_seconds"]) for r in episodes["random"]])
    result: dict = {"policies": {}, "criteria": CRITERIA | {"sleep": "> random"}}
    for label, s in stats.items():
        entry = {k: getattr(s, k) for k in s.__dataclass_fields__}
        if label != "random":
            life = np.asarray([float(r["lifetime_seconds"]) for r in episodes[label]])
            ratios = _bootstrap_mean(life, generator, samples) / np.maximum(_bootstrap_mean(base_life, generator, samples), 1e-9)
            entry["life_ratio"] = _ratio(s.mean_life, base.mean_life)
            entry["life_ratio_ci95"] = (float(np.quantile(ratios, 0.025)), float(np.quantile(ratios, 0.975)))
            entry["food_ratio"] = _ratio(s.food_rate, base.food_rate)
            entry["water_ratio"] = _ratio(s.water_rate, base.water_rate)
            entry["sleep_ratio"] = _ratio(s.sleep_rate, base.sleep_rate)
            gates = {
                "life": entry["life_ratio"] >= CRITERIA["life_ratio"],
                "food": entry["food_ratio"] >= CRITERIA["food_ratio"],
                "water": entry["water_ratio"] >= CRITERIA["water_ratio"],
                "sleep": s.sleep_rate > base.sleep_rate,
            }
            entry["gates"] = {k: "PASS" if v else "FAIL" for k, v in gates.items()}
            entry["stage_gate"] = "PASS" if all(gates.values()) else "FAIL"
        result["policies"][label] = entry
    return result


def to_markdown(result: dict, exploits: dict[str, dict]) -> str:
    lines = ["| Política | Vidas | Vida média (s) [IC95] | × aleatório [IC95] | Come c/ fome | Bebe c/ sede | Dorme cansada | Censuradas | Etapa |",
             "|---|---|---|---|---|---|---|---|---|"]
    for label, p in result["policies"].items():
        ci = p["mean_life_ci95"]
        ratio = "—" if label == "random" else f"{p['life_ratio']:.2f} [{p['life_ratio_ci95'][0]:.2f}–{p['life_ratio_ci95'][1]:.2f}]"
        gate = "base" if label == "random" else p["stage_gate"]
        rel = (lambda key, rate: f"{rate:.4f}" if label == "random" else f"{rate:.4f} ({p[key]:.1f}×)")
        lines.append(
            f"| {label} | {p['episodes']} | {p['mean_life']:.0f} [{ci[0]:.0f}–{ci[1]:.0f}] | {ratio} | "
            f"{rel('food_ratio', p['food_rate'])} | {rel('water_ratio', p['water_rate'])} | {rel('sleep_ratio', p['sleep_rate'])} | "
            f"{p['censored_share']:.0%} | {gate} |")
    lines += ["", "Causas de morte:"]
    for label, p in result["policies"].items():
        lines.append(f"- {label}: " + ", ".join(f"{k} {v:.0%}" for k, v in p["death_causes"].items()))
    if exploits:
        lines += ["", "| Vigia (Sim) | em folhas | enterrada | na borda | perto d'água | bebe quando perto | c/ sede perto d'água |",
                  "|---|---|---|---|---|---|---|"]
        for label, e in exploits.items():
            lines.append(
                f"| {label} | {e['on_leaves_share']:.2%} | {e['buried_share']:.2%} | {e['at_border_share']:.2%} | "
                f"{e.get('near_water_share', 0):.2%} | {e.get('drink_on_when_near', 0):.1%} | {e.get('near_water_when_thirsty', 0):.2%} |")
    return "\n".join(lines) + "\n"


def _pairs(values: Sequence[str]) -> dict[str, Path]:
    out = {}
    for value in values:
        label, _, path = value.partition("=")
        if not path:
            raise ValueError(f"expected label=path, got {value}")
        out[label] = Path(path)
    return out


def main() -> None:  # pragma: no cover - thin CLI
    parser = argparse.ArgumentParser(description="Stage evaluation: frozen policies vs random")
    parser.add_argument("episodes", nargs="+", help="label=episodes.jsonl (label 'random' is the baseline)")
    parser.add_argument("--exploits", nargs="*", default=[], help="label=sim-report.json")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--bootstrap-samples", type=int, default=2000)
    args = parser.parse_args()
    episodes = {label: read_episodes(path) for label, path in _pairs(args.episodes).items()}
    exploits = {label: json.loads(path.read_text(encoding="utf-8")) for label, path in _pairs(args.exploits).items()}
    result = evaluate(episodes, samples=args.bootstrap_samples)
    result["exploits"] = exploits
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "summary.json").write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")
    markdown = to_markdown(result, exploits)
    (args.output / "summary.md").write_text(markdown, encoding="utf-8")
    print(markdown)


if __name__ == "__main__":  # pragma: no cover
    main()
