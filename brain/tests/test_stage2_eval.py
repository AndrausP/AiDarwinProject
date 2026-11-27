import json
from pathlib import Path

import numpy as np

from creature_brain.benchmarks.stage2_eval import evaluate, to_markdown
from creature_brain.genealogy import build, generations

YEAR_TICKS = 12 * 600 * 60


def _write(run: Path, *, drift: float, seasonal: bool, seed: int) -> None:
    rng = np.random.default_rng(seed)
    run.mkdir(parents=True)
    lines = []
    for i in range(1, 9):  # founders
        lines.append({"kind": "birth", "tick": 0, "world": 0, "id": i, "mother": 0, "father": 0, "lineage": "A" if i < 6 else "B",
                      "sex": "Female", "generation": 0, "rescue": False, "genes": {"size": 1.0}})
    for k in range(400):
        tick = k * 2000
        if seasonal:
            tick = (k // 40) * YEAR_TICKS + int(rng.integers(0, YEAR_TICKS // 10))
        size = 1.0 + drift * k / 400 + rng.normal(0, 0.05)
        cid = 1000 + k
        lines.append({"kind": "birth", "tick": tick, "world": 0, "id": cid, "mother": 1, "father": 2, "lineage": "A" if k % 4 else "B",
                      "sex": "Male", "generation": 1 + k // (40 if k % 4 else 200), "rescue": False, "genes": {"size": size}})
        lines.append({"kind": "death", "tick": tick + 500, "world": 0, "id": cid, "mother": 1, "father": 2, "lineage": "A",
                      "sex": "Male", "generation": 1, "cause": "Age", "age": 300.0})
    (run / "genealogy.jsonl").write_text("\n".join(json.dumps(x) for x in lines) + "\n", encoding="utf-8")
    episodes = [{"creature_id": 1000 + k, "first_meal_seconds": 100.0 - k / 8, "lifetime_seconds": 300.0} for k in range(400)]
    (run / "ppo-episodes.jsonl").write_text("\n".join(json.dumps(e) for e in episodes) + "\n", encoding="utf-8")


def test_should_find_selected_gene_and_seasonal_births_when_selection_differs_from_control(tmp_path: Path) -> None:
    _write(tmp_path / "sel", drift=0.5, seasonal=True, seed=1)
    _write(tmp_path / "ctl", drift=0.0, seasonal=False, seed=2)

    result = evaluate(tmp_path / "sel", tmp_path / "ctl", genes=("size",))

    assert result["genes"]["size"]["welch_t_last"] > 3
    assert result["criteria"]["directional_change_vs_control"] is True
    assert result["criteria"]["seasonal_births"] is True
    assert result["seasonality"]["control"]["rayleigh_r"] < result["seasonality"]["selection"]["rayleigh_r"]
    assert result["criteria"]["baldwin_first_meal_falls"] is True
    assert result["criteria"]["r_more_generations_than_k"] is True
    assert "size" in to_markdown(result)


def test_should_count_generations_when_building_genealogy(tmp_path: Path) -> None:
    _write(tmp_path / "run", drift=0.0, seasonal=False, seed=3)

    connection = build(tmp_path / "run" / "genealogy.jsonl", tmp_path / "run" / "genealogy.sqlite")
    rows = generations(connection)

    assert rows[0][0] == 0 and rows[0][1] == 8
    assert sum(r[1] for r in rows) == 408