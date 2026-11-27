import pytest

from creature_brain.benchmarks.stage_eval import evaluate, to_markdown


def _life(seconds: float, *, ate=0, hungry=10, drank=0, thirsty=10, slept=0, tired=10, cause="starvation") -> dict:
    return {"lifetime_seconds": seconds, "ate_while_low_energy": ate, "low_energy_steps": hungry,
            "drank_while_low_water": drank, "low_water_steps": thirsty, "slept_while_tired": slept,
            "high_fatigue_steps": tired, "death_cause": cause}


def test_should_pass_stage_when_policy_beats_every_criterion() -> None:
    random = [_life(100, ate=1, drank=1, slept=1) for _ in range(30)]
    good = [_life(400, ate=3, drank=3, slept=2, cause="alive_at_horizon") for _ in range(30)]

    result = evaluate({"random": random, "v3": good}, samples=200)

    v3 = result["policies"]["v3"]
    assert v3["life_ratio"] == pytest.approx(4.0)
    assert v3["gates"] == {"life": "PASS", "food": "PASS", "water": "PASS", "sleep": "PASS"}
    assert v3["stage_gate"] == "PASS"
    assert v3["censored_share"] == 1.0
    assert "v3" in to_markdown(result, {})


def test_should_fail_stage_when_one_criterion_is_missed() -> None:
    random = [_life(100, ate=1, drank=1, slept=1) for _ in range(30)]
    no_sleep = [_life(400, ate=3, drank=3, slept=1, tired=40) for _ in range(30)]  # tired as often as random

    v3 = evaluate({"random": random, "v3": no_sleep}, samples=200)["policies"]["v3"]

    assert v3["gates"]["sleep"] == "FAIL"
    assert v3["stage_gate"] == "FAIL"


def test_should_pass_sleep_when_policy_never_gets_tired() -> None:
    random = [_life(100, ate=1, drank=1, slept=0, tired=20) for _ in range(30)]
    rested = [_life(400, ate=3, drank=3, slept=0, tired=0) for _ in range(30)]

    v3 = evaluate({"random": random, "v3": rested}, samples=200)["policies"]["v3"]

    assert v3["tired_share"] == 0.0
    assert v3["gates"]["sleep"] == "PASS"


def test_should_refuse_report_when_random_baseline_is_missing() -> None:
    with pytest.raises(ValueError):
        evaluate({"v3": [_life(10)]})
