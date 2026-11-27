"""Async TCP lockstep bridge.

Godot initiates the connection with HELLO. The server responds with CONFIG and
then performs one STEP -> ACT exchange per brain tick. Physics remains owned by
Godot; this module only observes snapshots and selects actions.
"""

from __future__ import annotations

import argparse
import asyncio
from dataclasses import dataclass, field
import logging
import random
import struct
import time
from pathlib import Path
from typing import Callable, Protocol, Sequence

from creature_brain import ACTION_DIM, OBSERVATION_DIM, PROTOCOL_VERSION
from .respawn import Respawner
from .protocol import (
    MAX_FRAME_BYTES,
    Act,
    Config,
    Hello,
    Message,
    MessageType,
    ProtocolError,
    Step,
    decode_payload,
    encode_message,
)


LOGGER = logging.getLogger(__name__)


class Policy(Protocol):
    """Receives the whole STEP so learners can see DEATH/ATE events, not only observations."""

    def act(self, step: Step) -> Sequence[Sequence[float]]: ...


StepObserver = Callable[[Step], None]


class RandomPolicy:
    """Seeded baseline with no access to privileged world state."""

    def __init__(self, seed: int = 0) -> None:
        self._random = random.Random(seed)

    def actions(
        self, creature_ids: Sequence[int], observations: Sequence[Sequence[float]]
    ) -> tuple[tuple[float, ...], ...]:
        del observations
        return tuple(
            tuple(self._random.uniform(-1.0, 1.0) for _ in range(ACTION_DIM))
            for _ in creature_ids
        )

    def act(self, step: Step) -> tuple[tuple[float, ...], ...]:
        return self.actions(step.creature_ids, step.observations)


@dataclass(slots=True)
class BridgeMetrics:
    ticks: int = 0
    out_of_order: int = 0
    started_at: float = field(default_factory=time.perf_counter)
    policy_ms: list[float] = field(default_factory=list)

    @property
    def steps_per_second(self) -> float:
        elapsed = max(time.perf_counter() - self.started_at, 1e-9)
        return self.ticks / elapsed


@dataclass(frozen=True, slots=True)
class ServerSettings:
    host: str = "127.0.0.1"
    port: int = 5555
    time_scale: float = 1.0
    population: int = 48
    read_timeout_seconds: float = 10.0
    exit_on_disconnect: bool = False
    respawn: bool = False
    sessions: int = 1  # with exit_on_disconnect: stop after this many worlds have finished


class LockstepServer:
    def __init__(
        self,
        policy: Policy,
        settings: ServerSettings = ServerSettings(),
        observers: Sequence[StepObserver] = (),
        observer_factories: Sequence[Callable[[Hello], StepObserver]] = (),
    ) -> None:
        self.policy = policy
        self.settings = settings
        self.observers = tuple(observers)
        # Built per connection from its HELLO (e.g. an episode recorder tagged with the world seed).
        self.observer_factories = tuple(observer_factories)
        self._session_ended = asyncio.Event()
        self._finished_sessions = 0
        self.metrics = BridgeMetrics()

    async def serve(self) -> None:
        server = await asyncio.start_server(
            self._handle_client, self.settings.host, self.settings.port
        )
        addresses = ", ".join(str(sock.getsockname()) for sock in server.sockets or ())
        LOGGER.info("brain bridge listening on %s", addresses)
        async with server:
            if self.settings.exit_on_disconnect:
                await self._session_ended.wait()
            else:
                await server.serve_forever()

    async def _handle_client(
        self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter
    ) -> None:
        peer = writer.get_extra_info("peername")
        LOGGER.info("Godot connected from %s", peer)
        expected_tick: int | None = None
        session_observers: list[StepObserver] = []
        try:
            hello = await self._read(reader)
            self._validate_hello(hello)
            # Several Godot worlds may train one shared brain: each connection gets its own
            # trajectory state, respawner (ids are per world) and observers.
            new_session = getattr(self.policy, "new_session", None)
            policy = new_session() if callable(new_session) else self.policy
            respawner = Respawner() if self.settings.respawn else None
            session_observers = [factory(hello) for factory in self.observer_factories]
            observers = (*self.observers, *session_observers)
            await self._write(
                writer,
                Config(
                    time_scale=self.settings.time_scale,
                    physics_hz=60,
                    brain_hz=10,
                    population=self.settings.population,
                ),
            )
            while True:
                message = await self._read(reader)
                if not isinstance(message, Step):
                    raise ProtocolError("expected STEP while server is running")
                if expected_tick is not None and message.tick <= expected_tick:
                    self.metrics.out_of_order += 1
                    raise ProtocolError(
                        f"tick {message.tick} is not newer than {expected_tick}"
                    )
                expected_tick = message.tick
                started = time.perf_counter()
                for observer in observers:
                    observer(message)
                actions = tuple(
                    tuple(float(value) for value in action)
                    for action in policy.act(message)
                )
                # Protocol flow: zero or more SPAWN frames, then exactly one ACT for this tick.
                if respawner is not None:
                    for spawn in respawner.spawns_for(message):
                        writer.write(encode_message(spawn))
                response = Act(message.tick, message.creature_ids, actions)
                await self._write(writer, response)
                self.metrics.ticks += 1
                self.metrics.policy_ms.append((time.perf_counter() - started) * 1000.0)
        except asyncio.IncompleteReadError:
            LOGGER.info("Godot disconnected from %s", peer)
        except (ProtocolError, asyncio.TimeoutError) as exc:
            LOGGER.warning("closing invalid lockstep session from %s: %s", peer, exc)
        finally:
            writer.close()
            try:
                await writer.wait_closed()
            except ConnectionError:
                pass  # peer already gone (e.g. Godot killed); nothing left to flush
            for observer in session_observers:
                close = getattr(observer, "close", None)
                if callable(close):
                    close()
            self._finished_sessions += 1
            if self._finished_sessions >= self.settings.sessions:
                self._session_ended.set()

    def _validate_hello(self, message: Message) -> None:
        if not isinstance(message, Hello):
            raise ProtocolError("first client frame must be HELLO")
        if message.version != PROTOCOL_VERSION:
            raise ProtocolError(f"unsupported protocol version {message.version}")
        if message.observation_dim != OBSERVATION_DIM:
            raise ProtocolError("observation dimension mismatch")
        if message.action_dim != ACTION_DIM:
            raise ProtocolError("action dimension mismatch")

    async def _read(self, reader: asyncio.StreamReader) -> Message:
        async with asyncio.timeout(self.settings.read_timeout_seconds):
            length_bytes = await reader.readexactly(4)
            (body_length,) = struct.unpack("<I", length_bytes)
            if body_length < 1 or body_length > MAX_FRAME_BYTES:
                raise ProtocolError("invalid frame length")
            body = await reader.readexactly(body_length)
        try:
            message_type = MessageType(body[0])
        except ValueError as exc:
            raise ProtocolError(f"unknown message type {body[0]}") from exc
        return decode_payload(message_type, body[1:])

    @staticmethod
    async def _write(writer: asyncio.StreamWriter, message: Message) -> None:
        writer.write(encode_message(message))
        await writer.drain()


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Criatura IA lockstep brain server")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=5555)
    parser.add_argument("--population", type=int, default=48)
    parser.add_argument("--slot-capacity", type=int, default=0,
                        help="agent slots kept for a growing population (births); 0 = creatures present")
    parser.add_argument("--time-scale", type=float, default=1.0)
    parser.add_argument("--seed", type=int, default=0)
    parser.add_argument("--policy", choices=("random", "ppo"), default="random")
    parser.add_argument("--checkpoint", type=Path, default=Path("checkpoints/ppo.pt"))
    parser.add_argument("--device", default="cpu")
    parser.add_argument("--episodes", type=Path, help="append one JSONL line per life (benchmark input)")
    parser.add_argument("--config-id", default="default")
    parser.add_argument("--horizon", type=int, default=0, help="run length in brain ticks, recorded per episode")
    parser.add_argument("--metrics", type=Path, help="PPO only: append one CSV row per update")
    parser.add_argument("--respawn", action="store_true", help="answer every DEATH with a SPAWN (keeps population)")
    parser.add_argument("--sessions", type=int, default=1, help="worlds to wait for with --exit-on-disconnect")
    parser.add_argument("--exit-on-disconnect", action="store_true", help="stop after the Godot session ends")
    parser.add_argument("--brain", choices=("gru", "mlp"), default="gru",
                        help="PPO only: gru = brain v3 (memory + curiosity); mlp = brain v2 (resume old runs)")
    parser.add_argument("--curiosity", type=float, default=0.0,
                        help="PPO gru only: weight of the novelty (RND) bonus; 0 (default since T1.1) disables curiosity")
    parser.add_argument("--freeze", action="store_true",
                        help="PPO only: frozen evaluation (fixed weights/normalisers, no updates, no checkpoint writes)")
    parser.add_argument("--amp", action="store_true", help="PPO gru only: fp16 autocast in the update (T2.0e, CUDA)")
    parser.add_argument("--deterministic", action="store_true", help="PPO only: act with the policy mean")
    parser.add_argument("--lr", type=float, default=3e-4,
                        help="PPO only: Adam learning rate for new runs (a resumed checkpoint keeps its optimizer state)")
    return parser.parse_args()


def main() -> None:
    args = _parse_args()
    logging.basicConfig(level=logging.INFO)
    settings = ServerSettings(
        host=args.host,
        port=args.port,
        time_scale=args.time_scale,
        population=args.population,
        exit_on_disconnect=args.exit_on_disconnect,
        respawn=args.respawn,
        sessions=args.sessions,
    )
    recorders: list = []
    episodes_stream = None
    policy = None
    try:
        if args.policy == "ppo":
            from creature_brain.learning.online import OnlinePPOConfig, OnlinePPOPolicy
            from creature_brain.learning.ppo import PPOConfig
            from creature_brain.learning.recurrent import CuriosityConfig

            policy = OnlinePPOPolicy(
                online_config=OnlinePPOConfig(
                    checkpoint_path=args.checkpoint,
                    device=args.device,
                    seed=args.seed,
                    metrics_path=args.metrics,
                    brain=args.brain,
                    curiosity=CuriosityConfig(weight=args.curiosity),
                    frozen=args.freeze,
                    deterministic_actions=args.deterministic,
                    slot_capacity=args.slot_capacity,
                ),
                ppo_config=PPOConfig(learning_rate=args.lr, amp=args.amp),
            )
            logging.getLogger(__name__).info(
                "brain=%s parameters=%d curiosity=%.3f lr=%.2e frozen=%s update=%d",
                args.brain, policy.model.trainable_parameter_count, args.curiosity, args.lr, args.freeze,
                policy.update_count,
            )
        else:
            policy = RandomPolicy(args.seed)
        factories = []
        if args.episodes is not None:
            from creature_brain.benchmarks.recorder import EpisodeRecorder

            args.episodes.parent.mkdir(parents=True, exist_ok=True)
            stream = episodes_stream = args.episodes.open("a", encoding="utf-8")

            def make_recorder(hello: Hello) -> EpisodeRecorder:
                # Episodes are tagged with the world seed announced by that world's HELLO.
                recorder = EpisodeRecorder(
                    stream,
                    policy=args.policy,
                    seed=hello.world_seed,
                    config_id=args.config_id,
                    horizon=args.horizon,
                )
                recorders.append(recorder)
                return recorder

            factories.append(make_recorder)
        asyncio.run(LockstepServer(policy, settings, observer_factories=factories).serve())
    except KeyboardInterrupt:
        LOGGER.info("brain bridge stopped")
    finally:
        flush = getattr(policy, "flush", None)
        if callable(flush):
            flush()  # keep the last updates when the simulator stops between periodic saves
        for recorder in recorders:
            recorder.close()  # idempotent: already closed at disconnect
            LOGGER.info("recorded %d episodes, tick_loss=%d", recorder.episodes_written, recorder.tick_loss)
        if episodes_stream is not None:
            episodes_stream.close()


if __name__ == "__main__":  # pragma: no cover
    main()

