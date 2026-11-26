import asyncio
import struct

from creature_brain import ACTION_DIM, OBSERVATION_DIM
from creature_brain.bridge.protocol import (
    Act,
    Config,
    Event,
    EventType,
    Hello,
    MessageType,
    Spawn,
    Step,
    decode_payload,
    encode_message,
)
from creature_brain.bridge.server import LockstepServer, RandomPolicy, ServerSettings


class _StepAwarePolicy:
    """Stands in for OnlinePPOPolicy, which needs the full STEP (events included)."""

    def __init__(self) -> None:
        self.steps: list[Step] = []

    def act(self, step: Step) -> tuple[tuple[float, ...], ...]:
        self.steps.append(step)
        return tuple((0.0,) * ACTION_DIM for _ in step.creature_ids)


async def _read(reader: asyncio.StreamReader):
    (length,) = struct.unpack("<I", await reader.readexactly(4))
    body = await reader.readexactly(length)
    return decode_payload(MessageType(body[0]), body[1:])


async def _exchange(policy, observers=(), respawn=False, spawns_out=None) -> tuple[Config, Act]:
    server = LockstepServer(policy, ServerSettings(port=0, respawn=respawn), observers=observers)
    listener = await asyncio.start_server(server._handle_client, "127.0.0.1", 0)
    port = listener.sockets[0].getsockname()[1]
    reader, writer = await asyncio.open_connection("127.0.0.1", port)
    try:
        writer.write(encode_message(Hello(world_seed=3)))
        config = await _read(reader)
        death = Event(EventType.DEATH, struct.pack("<IB", 2, 1))
        step = Step(5, (1,), ((0.5,) * OBSERVATION_DIM,), (death,))
        writer.write(encode_message(step))
        act = await _read(reader)
        while isinstance(act, Spawn):
            if spawns_out is not None:
                spawns_out.append(act)
            act = await _read(reader)
        return config, act
    finally:
        writer.close()
        await writer.wait_closed()
        listener.close()
        await listener.wait_closed()


def test_should_pass_full_step_with_events_when_policy_is_step_aware() -> None:
    policy = _StepAwarePolicy()
    observed: list[Step] = []

    config, act = asyncio.run(_exchange(policy, observers=[observed.append]))

    assert isinstance(config, Config) and config.brain_hz == 10
    assert isinstance(act, Act) and act.tick == 5 and act.creature_ids == (1,)
    [step] = policy.steps
    assert step.events[0].event_type is EventType.DEATH
    assert observed == policy.steps


def test_should_answer_with_bounded_actions_when_policy_is_random() -> None:
    _, act = asyncio.run(_exchange(RandomPolicy(seed=1)))

    [action] = act.actions
    assert len(action) == ACTION_DIM
    assert all(-1.0 <= value <= 1.0 for value in action)


def test_should_serve_online_ppo_policy_when_selected(tmp_path) -> None:
    from creature_brain.learning.online import OnlinePPOConfig, OnlinePPOPolicy

    policy = OnlinePPOPolicy(
        online_config=OnlinePPOConfig(checkpoint_path=tmp_path / "ppo.pt", device="cpu", seed=0)
    )

    _, act = asyncio.run(_exchange(policy))

    [action] = act.actions
    assert len(action) == ACTION_DIM


def test_should_send_spawn_before_act_when_respawn_enabled() -> None:
    spawns: list[Spawn] = []

    _, act = asyncio.run(_exchange(RandomPolicy(seed=1), respawn=True, spawns_out=spawns))

    # The STEP carried a DEATH for creature 2 while creature 1 is alive -> one new id (3).
    assert [spawn.creature_id for spawn in spawns] == [3]
    assert act.creature_ids == (1,)


def test_should_give_each_world_its_own_session_when_two_worlds_share_one_brain() -> None:
    from creature_brain.learning.online import OnlinePPOConfig, OnlinePPOPolicy

    policy = OnlinePPOPolicy(online_config=OnlinePPOConfig(rollout_horizon=2, seed=0))
    seeds_seen: list[int] = []

    async def scenario() -> list[tuple[int, ...]]:
        server = LockstepServer(
            policy,
            ServerSettings(port=0, respawn=True),
            observer_factories=[lambda hello: (seeds_seen.append(hello.world_seed), (lambda step: None))[1]],
        )
        listener = await asyncio.start_server(server._handle_client, "127.0.0.1", 0)
        port = listener.sockets[0].getsockname()[1]

        async def world(seed: int, ids: tuple[int, ...]) -> tuple[int, ...]:
            reader, writer = await asyncio.open_connection("127.0.0.1", port)
            writer.write(encode_message(Hello(world_seed=seed)))
            await _read(reader)
            spawned: list[int] = []
            for tick in range(4):
                obs = tuple((0.5,) * OBSERVATION_DIM for _ in ids)
                writer.write(encode_message(Step(tick, ids, obs)))
                message = await _read(reader)
                while isinstance(message, Spawn):
                    spawned.append(message.creature_id)
                    message = await _read(reader)
                assert isinstance(message, Act) and message.creature_ids == ids
            writer.close()
            await writer.wait_closed()
            return tuple(spawned)

        results = await asyncio.gather(world(10, (1, 2)), world(20, (1, 2, 3)))
        listener.close()
        await listener.wait_closed()
        return results

    asyncio.run(scenario())
    assert sorted(seeds_seen) == [10, 20]
    assert policy.update_count >= 2  # both worlds fed the same learner
