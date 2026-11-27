"""Genealogy database (T2.7a).

The simulator appends every birth and death to ``genealogy.jsonl`` (append-only, one JSON object per
line, written as it happens). This module loads it into SQLite for queries and reports:

    python -m creature_brain.genealogy build benchmarks/runs/<run>        # -> genealogy.sqlite
    python -m creature_brain.genealogy report benchmarks/runs/<run>       # generations, causes, genes

Tables: ``births`` (id, world, tick, mother, father, lineage, sex, generation, rescue) and
``genes`` (id, name, value), ``deaths`` (id, world, tick, cause, age). Ids are unique per run
(the simulator gives each world a disjoint range).
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import sqlite3
from typing import Iterable

SCHEMA = """
CREATE TABLE IF NOT EXISTS births (
    id INTEGER PRIMARY KEY, world INTEGER, tick INTEGER, mother INTEGER, father INTEGER,
    lineage TEXT, sex TEXT, generation INTEGER, rescue INTEGER
);
CREATE TABLE IF NOT EXISTS genes (id INTEGER, name TEXT, value REAL, PRIMARY KEY (id, name));
CREATE TABLE IF NOT EXISTS deaths (id INTEGER PRIMARY KEY, world INTEGER, tick INTEGER, cause TEXT, age REAL);
CREATE INDEX IF NOT EXISTS genes_name ON genes (name);
CREATE INDEX IF NOT EXISTS births_generation ON births (generation);
"""


def read_records(path: Path) -> Iterable[dict]:
    with path.open(encoding="utf-8") as stream:
        for line in stream:
            line = line.strip()
            if line:
                yield json.loads(line)


def build(jsonl: Path, database: Path) -> sqlite3.Connection:
    """(Re)builds the SQLite database from the append-only JSONL; returns an open connection."""
    temporary = database.with_suffix(".tmp")
    temporary.unlink(missing_ok=True)
    connection = sqlite3.connect(temporary)
    connection.executescript(SCHEMA)
    births, genes, deaths = [], [], []
    for r in read_records(jsonl):
        if r["kind"] == "birth":
            births.append((r["id"], r["world"], r["tick"], r["mother"], r["father"], r["lineage"], r["sex"],
                           r["generation"], 1 if r.get("rescue") else 0))
            genes.extend((r["id"], name, float(value)) for name, value in r.get("genes", {}).items())
        else:
            deaths.append((r["id"], r["world"], r["tick"], r["cause"], float(r["age"])))
    connection.executemany("INSERT OR REPLACE INTO births VALUES (?,?,?,?,?,?,?,?,?)", births)
    connection.executemany("INSERT OR REPLACE INTO genes VALUES (?,?,?)", genes)
    connection.executemany("INSERT OR REPLACE INTO deaths VALUES (?,?,?,?,?)", deaths)
    connection.commit()
    connection.close()
    temporary.replace(database)
    return sqlite3.connect(database)


def generations(connection: sqlite3.Connection) -> list[tuple]:
    """(generation, births, rescued, mean age at death in s, share dying of age)."""
    return connection.execute(
        """
        SELECT b.generation, COUNT(*), SUM(b.rescue), AVG(d.age),
               AVG(CASE WHEN d.cause = 'Age' THEN 1.0 ELSE 0.0 END)
        FROM births b LEFT JOIN deaths d ON d.id = b.id
        GROUP BY b.generation ORDER BY b.generation
        """
    ).fetchall()


def gene_by_period(connection: sqlite3.Connection, gene: str, periods: int = 4, *, born_only: bool = True) -> list[tuple]:
    """Mean and count of a gene among individuals born in each period of the run (rescues excluded)."""
    (last,) = connection.execute("SELECT MAX(tick) FROM births").fetchone()
    if last is None:
        return []
    width = max(1, (last + 1) // periods)
    rescue = "AND b.rescue = 0 AND b.generation > 0" if born_only else ""
    return connection.execute(
        f"""
        SELECT b.tick / ? AS period, AVG(g.value), COUNT(*)
        FROM births b JOIN genes g ON g.id = b.id AND g.name = ?
        WHERE 1 = 1 {rescue}
        GROUP BY period ORDER BY period
        """,
        (width, gene),
    ).fetchall()


def gene_values(connection: sqlite3.Connection, gene: str, *, from_tick: int = 0) -> list[float]:
    """Gene values of the individuals born (not rescued) from ``from_tick`` on."""
    rows = connection.execute(
        "SELECT g.value FROM births b JOIN genes g ON g.id = b.id AND g.name = ? "
        "WHERE b.rescue = 0 AND b.generation > 0 AND b.tick >= ? ORDER BY b.tick, b.id",
        (gene, from_tick),
    ).fetchall()
    return [r[0] for r in rows]


def causes(connection: sqlite3.Connection) -> list[tuple]:
    return connection.execute("SELECT cause, COUNT(*) FROM deaths GROUP BY cause ORDER BY COUNT(*) DESC").fetchall()


def report(run: Path, genes: Iterable[str]) -> str:
    connection = build(run / "genealogy.jsonl", run / "genealogy.sqlite")
    lines = ["| Geração | Nascidos | Resgatados | Idade média na morte (s) | Morre de velhice |", "|---|---|---|---|---|"]
    for generation, born, rescued, age, old in generations(connection):
        lines.append(f"| {generation} | {born} | {rescued or 0} | {age or 0:.0f} | {(old or 0):.0%} |")
    lines += ["", "Causas de morte: " + ", ".join(f"{c} {n}" for c, n in causes(connection)), ""]
    for gene in genes:
        values = gene_by_period(connection, gene)
        if values:
            lines.append(f"- {gene} por quarto do run (nascidos): " + " → ".join(f"{v:.3f} (n={n})" for _, v, n in values))
    connection.close()
    return "\n".join(lines) + "\n"


def main() -> None:  # pragma: no cover - thin CLI
    parser = argparse.ArgumentParser(description="Genealogy database of a Sim run (T2.7a)")
    parser.add_argument("command", choices=("build", "report"))
    parser.add_argument("run", type=Path)
    parser.add_argument("--genes", nargs="*", default=["size", "carnivory", "m_libido", "m_care", "litter_size", "longevity_years"])
    args = parser.parse_args()
    if args.command == "build":
        build(args.run / "genealogy.jsonl", args.run / "genealogy.sqlite").close()
        print(f"built {args.run / 'genealogy.sqlite'}")
    else:
        print(report(args.run, args.genes))


if __name__ == "__main__":  # pragma: no cover
    main()
