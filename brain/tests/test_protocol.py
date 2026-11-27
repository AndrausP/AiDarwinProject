from pathlib import Path
import struct

import pytest

from creature_brain import ACTION_DIM, OBSERVATION_DIM
from creature_brain.bridge.protocol import (
    Act,
    Config,
    Event,
    EventType,
    Hello,
    MessageType,
    ProtocolError,
    Spawn,
    Step,
    decode_frame,
    encode_message,
)


def test_should_round_trip_hello_when_frame_is_valid() -> None:
    message = Hello(world_seed=123456789)

    encoded = encode_message(message)

    assert struct.unpack_from("<I", encoded)[0] == len(encoded) - 4
    assert encoded[4] == MessageType.HELLO
    assert decode_frame(encoded) == message


def test_should_match_contract_fixture_when_encoding_hello_v3() -> None:
    fixture = Path(__file__).resolve().parents[2] / "contracts" / "fixtures" / "hello-v3.hex"

    encoded = encode_message(Hello(world_seed=0x0102030405060708))

    assert encoded == bytes.fromhex(fixture.read_text(encoding="utf-8").strip())


def test_should_round_trip_config_when_values_are_finite() -> None:
    message = Config(time_scale=4.0, physics_hz=60, brain_hz=10, population=48)

    assert decode_frame(encode_message(message)) == message


def test_should_round_trip_step_when_observation_layout_matches() -> None:
    observation = tuple(0.0 for _ in range(OBSERVATION_DIM))
    message = Step(
        tick=7,
        creature_ids=(2, 9),
        observations=(observation, observation),
        events=(Event(EventType.DEATH, b"\x02\x01"),),
    )

    assert decode_frame(encode_message(message)) == message


def test_should_round_trip_actions_and_spawn_when_valid() -> None:
    action = tuple(0.25 for _ in range(ACTION_DIM))
    act = Act(3, (1, 5), (action, action))
    spawn = Spawn(8, (0.5, 1.0), (1.0, 2.0, 3.0))

    assert decode_frame(encode_message(act)) == act
    assert decode_frame(encode_message(spawn)) == spawn


@pytest.mark.parametrize(
    "frame",
    [
        b"",
        b"\x01\x00\x00",
        struct.pack("<IB", 2, MessageType.HELLO),
        struct.pack("<IB", 1, 255),
        struct.pack("<IB", 0, MessageType.HELLO),
    ],
)
def test_should_reject_frame_when_truncated_or_unknown(frame: bytes) -> None:
    with pytest.raises(ProtocolError):
        decode_frame(frame)


def test_should_reject_step_when_ids_are_not_sorted_unique() -> None:
    observation = tuple(0.0 for _ in range(OBSERVATION_DIM))

    with pytest.raises(ProtocolError, match="unique and ascending"):
        encode_message(Step(1, (2, 1), (observation, observation)))


def test_should_reject_observation_when_value_is_not_finite() -> None:
    observation = [0.0 for _ in range(OBSERVATION_DIM)]
    observation[0] = float("nan")

    with pytest.raises(ProtocolError, match="non-finite"):
        encode_message(Step(1, (1,), (tuple(observation),)))



def test_should_behave_like_tuples_when_decoded_observations_are_lazy_rows() -> None:
    from creature_brain import OBSERVATION_DIM
    from creature_brain.bridge.protocol import ObservationRows, Step, decode_frame, encode_message

    rows = (tuple([0.5] * OBSERVATION_DIM), tuple([0.25] * OBSERVATION_DIM))
    decoded = decode_frame(encode_message(Step(7, (1, 2), rows)))

    assert isinstance(decoded.observations, ObservationRows)
    assert decoded.observations == rows
    assert len(decoded.observations) == 2
    assert float(decoded.observations[1][3]) == 0.25
    assert decoded == Step(7, (1, 2), rows)
