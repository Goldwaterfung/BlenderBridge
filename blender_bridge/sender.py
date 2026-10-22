"""Background TCP client; Blender API access is intentionally kept on the main thread."""

from __future__ import annotations

import logging
import queue
import socket
import threading
import time
import zlib
from collections import OrderedDict, deque
from dataclasses import dataclass
from typing import Any

from .protocol import (
    MAX_PAYLOAD_BYTES,
    Packet,
    PacketType,
    decode_frame,
    encode_frame,
    encode_ping,
    frame_from_buffer,
    parse_header,
)

log = logging.getLogger("blender_bridge")
MAX_CONTROL_EVENTS = 256


@dataclass(frozen=True)
class OutboundPacket:
    packet_type: int
    payload: bytes
    key: tuple[str, int] | None = None


class BridgeSender(threading.Thread):
    """Owns only plain bytes, queues, and sockets; never reads Blender data."""

    def __init__(self, port: int, hello_payload: bytes, *, connect_timeout: float = 2.0) -> None:
        super().__init__(name="BlenderBridgeSender", daemon=True)
        if not 1 <= int(port) <= 65535:
            raise ValueError("Bridge port must be between 1 and 65535.")
        self.port = int(port)
        self.connect_timeout = connect_timeout
        self._hello_payload = bytes(hello_payload)
        self._lock = threading.RLock()
        self._stop_event = threading.Event()
        self._control: deque[OutboundPacket] = deque()
        self._snapshot: deque[OutboundPacket] = deque()
        self._latest: OrderedDict[tuple[str, int], OutboundPacket] = OrderedDict()
        self._events: queue.SimpleQueue[tuple[str, Any]] = queue.SimpleQueue()
        self._sock: socket.socket | None = None
        self._connected = False
        self._snapshot_ready = False
        self._sequence = 0
        self._incoming_sequence: int | None = None
        self._generation = 0
        self._last_ping = 0.0
        self._last_pong = time.monotonic()
        self._pending_pings: dict[int, float] = {}
        self._project_root: str | None = None
        self._project_root_event = threading.Event()
        self._connection_state = "disconnected"
        self._last_error = ""
        self._latency_ms: float | None = None

    @property
    def connection_state(self) -> str:
        with self._lock:
            return self._connection_state

    @property
    def last_error(self) -> str:
        with self._lock:
            return self._last_error

    @property
    def latency_ms(self) -> float | None:
        with self._lock:
            return self._latency_ms

    def set_hello_payload(self, payload: bytes) -> None:
        with self._lock:
            self._hello_payload = bytes(payload)

    def request_project_root(self, timeout: float = 5.0) -> str:
        if timeout <= 0:
            raise ValueError("Project root request timeout must be positive.")
        with self._lock:
            if not self._connected or self._stop_event.is_set():
                raise ConnectionError("Connect to Unity with Live Updates before baking.")
            self._project_root = None
            self._project_root_event.clear()

        self.enqueue_control(PacketType.PROJECT_ROOT_REQUEST, b"")
        deadline = time.monotonic() + timeout
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("Unity did not provide its project root.")
            if self._project_root_event.wait(min(remaining, 0.05)):
                with self._lock:
                    if self._project_root is None:
                        raise ValueError("Unity returned an empty project root.")
                    return self._project_root
            with self._lock:
                if not self._connected or self._stop_event.is_set():
                    raise ConnectionError("The Unity bridge disconnected before returning its project root.")

    def enqueue_state(self, packet_type: int | PacketType, object_uuid: Any, payload: bytes) -> None:
        key = (str(object_uuid), int(packet_type))
        if len(payload) > MAX_PAYLOAD_BYTES:
            raise ValueError("Outbound payload exceeds the protocol limit.")
        with self._lock:
            if not self._connected or self._stop_event.is_set():
                return
            self._latest[key] = OutboundPacket(int(packet_type), bytes(payload), key)
            self._latest.move_to_end(key)
    def enqueue_control(
        self,
        packet_type: int | PacketType,
        payload: bytes,
        *,
        object_uuid: Any | None = None,
    ) -> None:
        if len(payload) > MAX_PAYLOAD_BYTES:
            raise ValueError("Outbound payload exceeds the protocol limit.")
        key = (str(object_uuid), int(packet_type)) if object_uuid is not None else None
        packet = OutboundPacket(int(packet_type), bytes(payload), key)
        with self._lock:
            if not self._connected or self._stop_event.is_set():
                return
            if int(packet_type) == int(PacketType.OBJECT_DELETED) and key is not None:
                for state_key in tuple(self._latest):
                    if state_key[0] == key[0]:
                        del self._latest[state_key]
            if len(self._control) >= MAX_CONTROL_EVENTS:
                self._last_error = (
                    "Lifecycle event queue overflowed; closing the connection to force a fresh snapshot."
                )
                self._close_socket_locked()
                self._events.put(("error", self._last_error))
                raise BufferError(self._last_error)
            self._control.append(packet)
    def enqueue_snapshot(self, packets: list[OutboundPacket]) -> None:
        """Atomically publish an ordered snapshot and open the post-handshake send gate."""
        with self._lock:
            if not self._connected or self._stop_event.is_set():
                return
            self._snapshot = deque(packets)
            self._control.clear()
            self._latest.clear()
            self._snapshot_ready = True

    def fail_snapshot(self, message: str) -> None:
        with self._lock:
            self._last_error = message
            self._close_socket_locked()
        self._events.put(("error", message))

    def poll_events(self) -> list[tuple[str, Any]]:
        result = []
        while True:
            try:
                result.append(self._events.get_nowait())
            except queue.Empty:
                return result

    def stop(self, timeout: float = 3.0) -> None:
        self._stop_event.set()
        with self._lock:
            self._close_socket_locked()
        if self.is_alive() and threading.current_thread() is not self:
            self.join(timeout)
            if self.is_alive():
                log.error("Bridge sender did not stop within %.1f seconds.", timeout)

    def _close_socket_locked(self) -> None:
        sock, self._sock = self._sock, None
        self._connected = False
        self._snapshot_ready = False
        self._connection_state = "disconnected"
        self._snapshot.clear()
        if sock is not None:
            try:
                sock.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            try:
                sock.close()
            except OSError:
                log.exception("Could not close bridge socket.")

    def _connect(self) -> socket.socket:
        with self._lock:
            self._connection_state = "connecting"
        sock = socket.create_connection(("127.0.0.1", self.port), timeout=self.connect_timeout)
        sock.settimeout(0.1)
        with self._lock:
            self._sock = sock
            self._connected = True
            self._connection_state = "connected"
            self._sequence = 0
            self._incoming_sequence = None
            self._snapshot_ready = False
            self._generation += 1
            self._pending_pings.clear()
            self._last_pong = time.monotonic()
            self._last_ping = self._last_pong
            self._control.clear()
            self._latest.clear()
            hello_payload = self._hello_payload
        self._send_packet(sock, PacketType.HELLO, hello_payload)
        with self._lock:
            self._snapshot_ready = False
            generation = self._generation
        self._events.put(("connected", generation))
        return sock

    def _send_packet(self, sock: socket.socket, packet_type: int, payload: bytes) -> None:
        with self._lock:
            sequence = self._sequence
            self._sequence = (self._sequence + 1) & 0xFFFFFFFF
        compress = packet_type in (
            int(PacketType.HELLO),
            int(PacketType.MESH_FULL),
            int(PacketType.MATERIALS),
        )
        frame = encode_frame(packet_type, payload, sequence, compress=compress)
        sock.settimeout(30.0)
        try:
            sock.sendall(frame)
        finally:
            sock.settimeout(0.1)

    def _next_packet(self) -> OutboundPacket | None:
        with self._lock:
            if not self._connected:
                return None
            if not self._snapshot_ready:
                return None
            if self._snapshot:
                return self._snapshot.popleft()
            if self._control:
                return self._control.popleft()
            if self._latest:
                _, packet = self._latest.popitem(last=False)
                return packet
            return None

    def _receive(self, sock: socket.socket, buffer: bytearray) -> None:
        try:
            data = sock.recv(65536)
        except socket.timeout:
            return
        if not data:
            raise ConnectionError("Unity closed the bridge connection.")
        buffer.extend(data)
        while True:
            frame = frame_from_buffer(buffer)
            if frame is None:
                return
            packet = decode_frame(frame)
            with self._lock:
                if self._incoming_sequence is not None and packet.sequence <= self._incoming_sequence:
                    raise ValueError("Unity packet sequence is not strictly increasing.")
                self._incoming_sequence = packet.sequence
            self._handle_packet(packet)

    def _handle_packet(self, packet: Packet) -> None:
        if packet.packet_type == int(PacketType.PONG):
            if len(packet.payload) != 4:
                raise ValueError("Pong payload must contain exactly one u32 timestamp.")
            sent_ms = int.from_bytes(packet.payload, "little")
            now = time.monotonic()
            sent_at = self._pending_pings.pop(sent_ms, None)
            if sent_at is not None:
                with self._lock:
                    self._latency_ms = (now - sent_at) * 1000.0
                    self._last_pong = now
            return
        if packet.packet_type == int(PacketType.SCENE_RESYNC):
            if len(packet.payload) != 4:
                raise ValueError("SceneResync payload must contain exactly one u32 ID.")
            resync_id = int.from_bytes(packet.payload, "little")
            with self._lock:
                self._snapshot_ready = False
                self._snapshot.clear()
                self._latest.clear()
            self._events.put(("resync", resync_id))
            return
        if packet.packet_type == int(PacketType.PROJECT_ROOT):
            try:
                project_root = packet.payload.decode("utf-8")
            except UnicodeDecodeError as exc:
                raise ValueError("Unity project root is not valid UTF-8.") from exc
            if not project_root.strip():
                raise ValueError("Unity returned an empty project root.")
            with self._lock:
                self._project_root = project_root
                self._project_root_event.set()
            self._events.put(("project_root", project_root))
            return
        if packet.packet_type not in {int(item) for item in PacketType}:
            log.warning("Ignoring unknown Unity packet type 0x%02x.", packet.packet_type)
            return
        log.warning("Ignoring unexpected Unity packet type 0x%02x.", packet.packet_type)

    def _send_ping_if_due(self, sock: socket.socket) -> None:
        now = time.monotonic()
        if now - self._last_ping < 1.0:
            return
        timestamp = int(now * 1000.0) & 0xFFFFFFFF
        self._pending_pings[timestamp] = now
        self._last_ping = now
        self._send_packet(sock, PacketType.PING, encode_ping(timestamp))
        if now - self._last_pong > 5.0:
            raise TimeoutError("Unity has not replied to a ping for more than five seconds.")

    def run(self) -> None:
        backoff = 0.5
        while not self._stop_event.is_set():
            sock = None
            connected_at = 0.0
            try:
                sock = self._connect()
                connected_at = time.monotonic()
                receive_buffer = bytearray()
                while not self._stop_event.is_set():
                    packet = self._next_packet()
                    if packet is not None:
                        self._send_packet(sock, packet.packet_type, packet.payload)
                    self._send_ping_if_due(sock)
                    self._receive(sock, receive_buffer)
            except Exception as exc:
                # Never let one unexpected payload kill the sender thread. A dead
                # sender used to leave connection_state at "connected" while no
                # packets were sent, which looked like the bridge had frozen.
                if not self._stop_event.is_set():
                    message = f"Bridge connection failed ({type(exc).__name__}): {exc}"
                    if isinstance(exc, (BufferError, ConnectionError, OSError, ValueError, zlib.error)):
                        log.warning("%s; reconnecting in %.1f seconds.", message, backoff)
                    else:
                        log.exception("%s; reconnecting in %.1f seconds.", message, backoff)
                    with self._lock:
                        self._last_error = message
                        self._connection_state = "disconnected"
                        self._close_socket_locked()
                    self._events.put(("error", message))
                    if connected_at and time.monotonic() - connected_at >= 30.0:
                        backoff = 0.5
                    self._stop_event.wait(backoff)
                    backoff = min(backoff * 2.0, 10.0)
            finally:
                if sock is not None:
                    with self._lock:
                        if self._sock is sock:
                            self._close_socket_locked()
        with self._lock:
            self._connection_state = "disconnected"
            self._close_socket_locked()