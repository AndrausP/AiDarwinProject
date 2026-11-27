"""Stage-2 evaluation (T2.8 control + T2.9 report).

Compares a run WITH selection against its control run (``--control``: parents drawn at random,
same genetics and mutation) using the genealogy of both, plus the episode records:

1. reproduction sustained: births, share of the population born (not rescued), generations;
2. directional gene change vs control: per gene, last-quarter mean of the newborns in each run,
   Welch t statistic and the change from the first quarter;
3. Baldwin signal: time from birth to the first meal, by generation (slope < 0 = falls);
4. r/K: generations reached by founder lineage A (r) vs B (K);
5. seasonal births: Rayleigh test of the year phase of births (R near 1 = concentrated).

    python -m creature_brain.benchmarks.stage2_eval --selection runs/s2-sel --control runs/s2-ctl --output runs/eval-etapa2
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import sqlite3

import numpy as np

from creature_brain.genealogy import build, gene_values

PHYSICS_HZ = 60
YEAR_SECONDS = 12 * 600
YEAR_START = 0.25  # SimClock starts in spring
DEFAULT_GENES = ("size", "carnivory", "m_libido", "m_care", "litter_size", "longevity_years", "maturity_years",
                 "smell", "vision_range", "fur", "breeding_width")


def welch(a: np.ndarray, b: np.ndarray) -> float:
    if len(a) < 2 or len(b) < 2:
        return 0.0
    va, vb = a.var(ddof=1) / len(a), b.var(ddof=1) / len(b)
    return float((a.mean() - b.mean()) / math.sqrt(va + vb)) if va + vb > 0 else 0.0


def _last_tick(connection: sqlite3.Connection) -> int:
    (last,) = connection.execute("SELECT MAX(tick) FROM births").fetchone()
    return int(last or 0)


def reproduction(connection: sqlite3.Connection) -> dict:
    last = _last_tick(connection)
    born, rescued, generation = connection.execute(
        "SELECT SUM(rescue = 0 AND generation > 0), SUM(rescue), MAX(generation) FROM births").fetchone()
    late_born, late_all = connection.execute(
        "SELECT SUM(rescue = 0 AND generation > 0), COUNT(*) FROM births WHERE tick >= ?", (last * 3 // 4,)).fetchone()
    hours = last / PHYSICS_HZ / 3600 if last else 0.0
    return {
        "births": int(born or 0),
        "rescues": int(rescued or 0),
        "max_generation": int(generation or 0),
        "births_per_sim_hour": (born or 0) / hours if hours else 0.0,
        "born_share_last_quarter": (late_born or 0) / late_all if late_all else 0.0,
        "simulated_hours": hours,
    }


def gene_shift(selection: sqlite3.Connection, control: sqlite3.Connection, genes) -> dict:
    out = {}
    for gene in genes:
        rows = {}
        for label, connection in (("selection", selection), ("control", control)):
            last = _last_tick(connection)
            early = np.asarray(gene_values(connection, gene, from_tick=0), dtype=float)
            late = np.asarray(gene_values(connection, gene, from_tick=last * 3 // 4), dtype=float)
            first = np.asarray([v for v in early][: max(1, len(early) // 4)], dtype=float)
            rows[label] = (first, late)
        (s_first, s_late), (c_first, c_late) = rows["selection"], rows["control"]
        out[gene] = {
            "selection_first": float(s_first.mean()) if len(s_first) else None,
            "selection_last": float(s_late.mean()) if len(s_late) else None,
            "control_first": float(c_first.mean()) if len(c_first) else None,
            "control_last": float(c_late.mean()) if len(c_late) else None,
            "n_selection_last": int(len(s_late)),
            "n_control_last": int(len(c_late)),
            "welch_t_last": welch(s_late, c_late),
        }
    return out


def _episodes(run: Path) -> list[dict]:
    path = run / "ppo-episodes.jsonl"
    if not path.exists():
        return []
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def baldwin(connection: sqlite3.Connection, episodes: list[dict]) -> dict:
    generation = dict(connection.execute("SELECT id, generation FROM births WHERE rescue = 0").fetchall())
    by_gen: dict[int, list[float]] = {}
    for e in episodes:
        g = generation.get(int(e["creature_id"]))
        t = e.get("first_meal_seconds")
        if g is None or t is None or g == 0:
            continue
        by_gen.setdefault(int(g), []).append(float(t))
    gens = sorted(k for k, v in by_gen.items() if len(v) >= 5)
    means = {g: float(np.mean(by_gen[g])) for g in gens}
    slope = float(np.polyfit(gens, [means[g] for g in gens], 1)[0]) if len(gens) >= 3 else 0.0
    return {"first_meal_by_generation": means, "slope_seconds_per_generation": slope}


def r_k(connection: sqlite3.Connection) -> dict:
    rows = connection.execute("SELECT lineage, MAX(generation), COUNT(*) FROM births WHERE rescue = 0 GROUP BY lineage").fetchall()
    return {lineage: {"max_generation": int(g or 0), "births": int(n)} for lineage, g, n in rows}


def seasonality(connection: sqlite3.Connection) -> dict:
    ticks = [t for (t,) in connection.execute("SELECT tick FROM births WHERE rescue = 0 AND generation > 0")]
    if len(ticks) < 10:
        return {"births": len(ticks), "rayleigh_r": 0.0, "rayleigh_p": 1.0}
    phase = (np.asarray(ticks) / PHYSICS_HZ / YEAR_SECONDS + YEAR_START) % 1.0
    angles = phase * 2 * math.pi
    r = float(np.hypot(np.cos(angles).mean(), np.sin(angles).mean()))
    n = len(angles)
    z = n * r * r
    p = float(math.exp(-z) * (1 + (2 * z - z * z) / (4 * n)))
    return {"births": n, "rayleigh_r": r, "rayleigh_p": max(0.0, min(1.0, p)),
            "mean_phase": float((math.atan2(np.sin(angles).mean(), np.cos(angles).mean()) / (2 * math.pi)) % 1.0)}


def evaluate(selection_run: Path, control_run: Path, genes=DEFAULT_GENES) -> dict:
    selection = build(selection_run / "genealogy.jsonl", selection_run / "genealogy.sqlite")
    control = build(control_run / "genealogy.jsonl", control_run / "genealogy.sqlite")
    result = {
        "reproduction": {"selection": reproduction(selection), "control": reproduction(control)},
        "genes": gene_shift(selection, control, genes),
        "baldwin": baldwin(selection, _episodes(selection_run)),
        "r_k": r_k(selection),
        "seasonality": {"selection": seasonality(selection), "control": seasonality(control)},
    }
    s = result["reproduction"]["selection"]
    significant = [g for g, v in result["genes"].items() if abs(v["welch_t_last"]) >= 3.0]
    lineages = result["r_k"]
    result["criteria"] = {
        "reproduction_sustained": s["born_share_last_quarter"] >= 0.5 and s["max_generation"] >= 3,
        "directional_change_vs_control": len(significant) > 0,
        "significant_genes": significant,
        "baldwin_first_meal_falls": result["baldwin"]["slope_seconds_per_generation"] < 0,
        "r_more_generations_than_k": lineages.get("A", {}).get("max_generation", 0) > lineages.get("B", {}).get("max_generation", 0),
        "seasonal_births": result["seasonality"]["selection"]["rayleigh_p"] < 0.01,
    }
    selection.close()
    control.close()
    return result


def to_markdown(result: dict) -> str:
    r = result["reproduction"]
    lines = ["| | Nascidos | Resgates | Geração máx. | Nascidos/h simulada | Nascidos no último quarto |", "|---|---|---|---|---|---|"]
    for label in ("selection", "control"):
        x = r[label]
        lines.append(f"| {label} | {x['births']} | {x['rescues']} | {x['max_generation']} | {x['births_per_sim_hour']:.1f} | {x['born_share_last_quarter']:.0%} |")
    lines += ["", "| Gene | Seleção 1º→último quarto | Controle 1º→último | t de Welch (último quarto) |", "|---|---|---|---|"]
    fmt = lambda v: "—" if v is None else f"{v:.3f}"
    for gene, v in result["genes"].items():
        lines.append(f"| {gene} | {fmt(v['selection_first'])} → {fmt(v['selection_last'])} | {fmt(v['control_first'])} → {fmt(v['control_last'])} | {v['welch_t_last']:+.1f} |")
    b = result["baldwin"]
    lines += ["", f"Baldwin: inclinação da 1ª refeição por geração = {b['slope_seconds_per_generation']:+.2f} s/geração "
              f"({len(b['first_meal_by_generation'])} gerações com ≥ 5 vidas)",
              "r/K: " + ", ".join(f"{k} geração máx. {v['max_generation']} ({v['births']} nascidos)" for k, v in result["r_k"].items()),
              f"Estação: R de Rayleigh {result['seasonality']['selection']['rayleigh_r']:.2f} (p = {result['seasonality']['selection']['rayleigh_p']:.3g}); "
              f"controle R {result['seasonality']['control']['rayleigh_r']:.2f}", "", "Critérios:"]
    for k, v in result["criteria"].items():
        lines.append(f"- {k}: {v if not isinstance(v, bool) else ('PASS' if v else 'FAIL')}")
    return "\n".join(lines) + "\n"


def main() -> None:  # pragma: no cover - thin CLI
    parser = argparse.ArgumentParser(description="Stage-2 evaluation: selection vs control (T2.8/T2.9)")
    parser.add_argument("--selection", type=Path, required=True)
    parser.add_argument("--control", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = evaluate(args.selection, args.control)
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "summary.json").write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")
    markdown = to_markdown(result)
    (args.output / "summary.md").write_text(markdown, encoding="utf-8")
    print(markdown)


if __name__ == "__main__":  # pragma: no cover
    main()
