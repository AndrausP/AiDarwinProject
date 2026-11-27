import struct

from creature_brain import OBSERVATION_DIM
from creature_brain.bridge.protocol import Event, EventType, Step
from creature_brain.bridge.respawn import Respawner


def _death(creature_id: int) -> Event:
    return Event(EventType.DEATH, struct.pack("<IB", creature_id, 3))


def _obs() -> tuple[float, ...]:
    return (0.5,) * OBSERVATION_DIM


def test_should_spawn_one_new_id_per_death_when_creatures_die() -> None:
    respawner = Respawner()
    respawner.spawns_for(Step(0, (1, 2, 3), (_obs(), _obs(), _obs())))

    spawns = respawner.spawns_for(Step(1, (2,), (_obs(),), (_death(1), _death(3))))

    assert [spawn.creature_id for spawn in spawns] == [4, 5]
    assert all(spawn.traits == () and spawn.position is None for spawn in spawns)


def test_should_never_reuse_ids_when_deaths_repeat() -> None:
    respawner = Respawner()
    respawner.spawns_for(Step(0, (1,), (_obs(),)))
    first = respawner.spawns_for(Step(1, (), (), (_death(1),)))
    second = respawner.spawns_for(Step(2, (), (), (_death(first[0].creature_id),)))

    assert first[0].creature_id == 2
    assert second[0].creature_id == 3
    assert respawner.spawned == 2


def test_should_not_spawn_when_nobody_dies() -> None:
    assert Respawner().spawns_for(Step(0, (1,), (_obs(),))) == ()


def test_should_stay_in_brain_id_range_when_simulator_births_appear() -> None:
    from creature_brain import SIMULATOR_ID_BASE

    respawner = Respawner()
    born = SIMULATOR_ID_BASE + 7
    respawner.spawns_for(Step(0, (1, 2, born), (_obs(), _obs(), _obs())))

    spawns = respawner.spawns_for(Step(1, (2, born), (_obs(), _obs()), (_death(1),)))

    assert [spawn.creature_id for spawn in spawns] == [3]