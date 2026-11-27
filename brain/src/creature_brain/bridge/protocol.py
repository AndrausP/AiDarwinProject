"""Version 2 binary protocol shared with the Godot runtime.

Every wire frame is ``[u32 little-endian body length][u8 type][payload]``.
The body length includes the one-byte type. Floating point values are IEEE-754
float32 in little-endian order. Decoders reject truncated, oversized, trailing
and non-finite payloads so a corrupt frame cannot silently reach the simulator.
"""

from __future__ import annotations

import collections.abc as _abc
from dataclasses import dataclass, field
from enum import IntEnum
import math
import struct
from typing import Iterable, Sequence, TypeAlias

import numpy as np

from creature_brain import ACTION_DIM, OBSERVATION_DIM, PROTOCOL_VERSION


class ObservationRows(_abc.Sequence):
    """Read-only sequence of observation rows backed by a decoded float32 (N, D) array.

    Behaves like the tuple of tuples it replaces (len, iteration, indexing, equality with tuples)
    without materialising N x D Python floats; each row is a numpy view."""

    __slots__ = ("_array",)

    def __init__(self, array: np.ndarray) -> None:
        self._array = array

    def __len__(self) -> int:
        return self._array.shape[0]

    def __getitem__(self, index):
        if isinstance(index, slice):
            return ObservationRows(self._array[index])
        return self._array[index]

    def __iter__(self):
        return iter(self._array)

    def __eq__(self, other: object) -> bool:
        if isinstance(other, ObservationRows):
            return np.array_equal(self._array, other._array)
        if isinstance(other, (tuple, list)):
            return len(other) == len(self) and all(
                len(row) == self._array.shape[1] and tuple(map(float, row)) == tuple(self._array[i].tolist())
                for i, row in enumerate(other)
            )
        return NotImplemented

    __hash__ = None  # type: ignore[assignment]

    def __repr__(self) -> str:
        return f"ObservationRows(shape={self._array.shape})"


MAX_FRAME_SIZE = 64 * 1024 * 1024
# Compatibility name for callers created before the shared contract was fixed.
MAX_FRAME_BYTES = MAX_FRAME_SIZE
_HEADER = struct.Struct("<IB")


class ProtocolError(ValueError):
    """Raised when bytes violate protocol v1."""


class MessageType(IntEnum):
    HELLO = 1
    CONFIG = 2
    STEP = 3
    ACT = 4
    SPAWN = 5


class EventType(IntEnum):
    DEATH = 1
    MATE_REQUEST = 2
    ATE = 3
    DAMAGED = 4
    BIRTH = 5  # child u32, mother u32, father u32
    MATED = 6  # female u32, male u32
    FED = 7  # feeder u32, child u32, energy f32


@dataclass(frozen=True, slots=True)
class Event:
    event_type: EventType
    payload: bytes


@dataclass(frozen=True, slots=True)
class Hello:
    version: int = PROTOCOL_VERSION
    observation_dim: int = OBSERVATION_DIM
    action_dim: int = ACTION_DIM
    world_seed: int = 0


@dataclass(frozen=True, slots=True)
class Config:
    time_scale: float = 1.0
    physics_hz: int = 60
    brain_hz: int = 10
    population: int = 48


@dataclass(frozen=True, slots=True)
class Step:
    tick: int
    creature_ids: tuple[int, ...]
    observations: tuple[tuple[float, ...], ...]
    events: tuple[Event, ...] = ()
    # Decoder-provided float32 (N, OBSERVATION_DIM) view of ``observations``; lets the learner
    # skip rebuilding an array from Python tuples every tick. Not part of equality.
    observation_array: np.ndarray | None = field(default=None, compare=False, repr=False)


@dataclass(frozen=True, slots=True)
class Act:
    tick: int
    creature_ids: tuple[int, ...]
    actions: tuple[tuple[float, ...], ...]


@dataclass(frozen=True, slots=True)
class Spawn:
    creature_id: int
    traits: tuple[float, ...]
    position: tuple[float, float, float] | None = None


Message: TypeAlias = Hello | Config | Step | Act | Spawn


def _require_u16(value: int, label: str) -> None:
    if not 0 <= value <= 0xFFFF:
        raise ProtocolError(f"{label} is outside uint16")


def _require_u32(value: int, label: str) -> None:
    if not 0 <= value <= 0xFFFFFFFF:
        raise ProtocolError(f"{label} is outside uint32")


def _require_u64(value: int, label: str) -> None:
    if not 0 <= value <= 0xFFFFFFFFFFFFFFFF:
        raise ProtocolError(f"{label} is outside uint64")


_AGENT_RECORD = np.dtype([("id", "<u4"), ("observation", "<f4", (OBSERVATION_DIM,))])


def _finite(values: Iterable[float], label: str) -> tuple[float, ...]:
    result = tuple(float(value) for value in values)
    if not all(math.isfinite(value) for value in result):
        raise ProtocolError(f"{label} contains non-finite values")
    return result


def _validated_ids(ids: Sequence[int]) -> tuple[int, ...]:
    result = tuple(int(value) for value in ids)
    if result != tuple(sorted(set(result))):
        raise ProtocolError("creature ids must be unique and ascending")
    for value in result:
        _require_u32(value, "creature id")
    _require_u16(len(result), "creature count")
    return result


def encode_message(message: Message) -> bytes:
    """Encode one typed message, including the five-byte frame header."""

    if isinstance(message, Hello):
        _require_u16(message.version, "version")
        _require_u16(message.observation_dim, "observation dim")
        _require_u16(message.action_dim, "action dim")
        _require_u64(message.world_seed, "world seed")
        message_type = MessageType.HELLO
        payload = struct.pack(
            "<HHHQ",
            message.version,
            message.observation_dim,
            message.action_dim,
            message.world_seed,
        )
    elif isinstance(message, Config):
        values = _finite((message.time_scale,), "config")
        _require_u16(message.physics_hz, "physics hz")
        _require_u16(message.brain_hz, "brain hz")
        _require_u16(message.population, "population")
        message_type = MessageType.CONFIG
        payload = struct.pack(
            "<fHHH",
            values[0],
            message.physics_hz,
            message.brain_hz,
            message.population,
        )
    elif isinstance(message, Step):
        message_type = MessageType.STEP
        payload = _encode_step(message)
    elif isinstance(message, Act):
        message_type = MessageType.ACT
        payload = _encode_act(message)
    elif isinstance(message, Spawn):
        message_type = MessageType.SPAWN
        payload = _encode_spawn(message)
    else:  # pragma: no cover - defensive API guard
        raise TypeError(f"unsupported protocol message {type(message)!r}")

    body_length = 1 + len(payload)
    if body_length > MAX_FRAME_BYTES:
        raise ProtocolError("frame exceeds configured maximum")
    return _HEADER.pack(body_length, int(message_type)) + payload


def decode_frame(frame: bytes | bytearray | memoryview) -> Message:
    """Decode exactly one complete frame."""

    view = memoryview(frame)
    if len(view) < _HEADER.size:
        raise ProtocolError("truncated frame header")
    body_length, raw_type = _HEADER.unpack_from(view)
    if body_length < 1 or body_length > MAX_FRAME_BYTES:
        raise ProtocolError("invalid frame length")
    if len(view) != 4 + body_length:
        raise ProtocolError("frame length does not match payload")
    try:
        message_type = MessageType(raw_type)
    except ValueError as exc:
        raise ProtocolError(f"unknown message type {raw_type}") from exc
    payload = view[_HEADER.size :]
    return decode_payload(message_type, payload)


def decode_payload(message_type: MessageType, payload: bytes | memoryview) -> Message:
    view = memoryview(payload)
    if message_type is MessageType.HELLO:
        _exact_size(view, struct.calcsize("<HHHQ"))
        return Hello(*struct.unpack_from("<HHHQ", view))
    if message_type is MessageType.CONFIG:
        _exact_size(view, struct.calcsize("<fHHH"))
        result = Config(*struct.unpack_from("<fHHH", view))
        _finite((result.time_scale,), "config")
        return result
    if message_type is MessageType.STEP:
        return _decode_step(view)
    if message_type is MessageType.ACT:
        return _decode_act(view)
    if message_type is MessageType.SPAWN:
        return _decode_spawn(view)
    raise ProtocolError(f"unknown message type {message_type}")


def _encode_step(message: Step) -> bytes:
    _require_u64(message.tick, "tick")
    ids = _validated_ids(message.creature_ids)
    if len(ids) != len(message.observations):
        raise ProtocolError("observation count does not match ids")
    output = bytearray(struct.pack("<QH", message.tick, len(ids)))
    for creature_id, observation in zip(ids, message.observations, strict=True):
        values = _finite(observation, "observation")
        if len(values) != OBSERVATION_DIM:
            raise ProtocolError(f"observation must have {OBSERVATION_DIM} floats")
        output.extend(struct.pack("<I", creature_id))
        output.extend(struct.pack(f"<{OBSERVATION_DIM}f", *values))
    _require_u16(len(message.events), "event count")
    output.extend(struct.pack("<H", len(message.events)))
    for event in message.events:
        payload = bytes(event.payload)
        _require_u16(len(payload), "event payload")
        output.extend(struct.pack("<BH", int(event.event_type), len(payload)))
        output.extend(payload)
    return bytes(output)


def _decode_step(view: memoryview) -> Step:
    cursor = _Cursor(view)
    tick, count = cursor.unpack("<QH")
    # Agents are contiguous (u32 id + OBSERVATION_DIM f32 each): decode them in one vectorised pass.
    agents = np.frombuffer(cursor.take(count * _AGENT_RECORD.itemsize), dtype=_AGENT_RECORD, count=count)
    array = np.ascontiguousarray(agents["observation"], dtype=np.float32)
    if not np.isfinite(array).all():
        raise ProtocolError("observation contains non-finite values")
    ids = agents["id"].tolist()
    # Lazy rows: converting 288 x 1000 floats to Python tuples cost ~10 ms per tick (T2.0e).
    observations = ObservationRows(array)
    (event_count,) = cursor.unpack("<H")
    events: list[Event] = []
    for _ in range(event_count):
        raw_type, size = cursor.unpack("<BH")
        try:
            event_type = EventType(raw_type)
        except ValueError as exc:
            raise ProtocolError(f"unknown event type {raw_type}") from exc
        events.append(Event(event_type, cursor.take(size)))
    cursor.finish()
    return Step(tick, _validated_ids(ids), observations, tuple(events), observation_array=array)


def _encode_act(message: Act) -> bytes:
    _require_u64(message.tick, "tick")
    ids = _validated_ids(message.creature_ids)
    if len(ids) != len(message.actions):
        raise ProtocolError("action count does not match ids")
    output = bytearray(struct.pack("<QH", message.tick, len(ids)))
    for creature_id, action in zip(ids, message.actions, strict=True):
        values = _finite(action, "action")
        if len(values) != ACTION_DIM:
            raise ProtocolError(f"action must have {ACTION_DIM} floats")
        output.extend(struct.pack("<I", creature_id))
        output.extend(struct.pack(f"<{ACTION_DIM}f", *values))
    return bytes(output)


def _decode_act(view: memoryview) -> Act:
    cursor = _Cursor(view)
    tick, count = cursor.unpack("<QH")
    ids: list[int] = []
    actions: list[tuple[float, ...]] = []
    for _ in range(count):
        (creature_id,) = cursor.unpack("<I")
        values = _finite(cursor.unpack(f"<{ACTION_DIM}f"), "action")
        ids.append(creature_id)
        actions.append(values)
    cursor.finish()
    return Act(tick, _validated_ids(ids), tuple(actions))


def _encode_spawn(message: Spawn) -> bytes:
    _require_u32(message.creature_id, "creature id")
    traits = _finite(message.traits, "traits")
    _require_u16(len(traits), "trait count")
    output = bytearray(struct.pack("<IH", message.creature_id, len(traits)))
    output.extend(struct.pack(f"<{len(traits)}f", *traits))
    output.extend(struct.pack("<B", message.position is not None))
    if message.position is not None:
        position = _finite(message.position, "position")
        if len(position) != 3:
            raise ProtocolError("position must contain three floats")
        output.extend(struct.pack("<3f", *position))
    return bytes(output)


def _decode_spawn(view: memoryview) -> Spawn:
    cursor = _Cursor(view)
    creature_id, trait_count = cursor.unpack("<IH")
    traits = _finite(cursor.unpack(f"<{trait_count}f"), "traits") if trait_count else ()
    (has_position,) = cursor.unpack("<B")
    if has_position not in (0, 1):
        raise ProtocolError("invalid position flag")
    position = _finite(cursor.unpack("<3f"), "position") if has_position else None
    cursor.finish()
    return Spawn(creature_id, traits, position)  # type: ignore[arg-type]


def _exact_size(view: memoryview, expected: int) -> None:
    if len(view) != expected:
        raise ProtocolError(f"payload must be exactly {expected} bytes")


class _Cursor:
    def __init__(self, data: memoryview) -> None:
        self._data = data
        self._offset = 0

    def unpack(self, format_string: str) -> tuple:
        size = struct.calcsize(format_string)
        if self._offset + size > len(self._data):
            raise ProtocolError("truncated payload")
        result = struct.unpack_from(format_string, self._data, self._offset)
        self._offset += size
        return result

    def take(self, size: int) -> bytes:
        if self._offset + size > len(self._data):
            raise ProtocolError("truncated payload")
        result = bytes(self._data[self._offset : self._offset + size])
        self._offset += size
        return result

    def finish(self) -> None:
        if self._offset != len(self._data):
            raise ProtocolError("payload contains trailing bytes")
