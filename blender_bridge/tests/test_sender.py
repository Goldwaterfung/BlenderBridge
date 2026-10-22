import socket
import struct
import threading
import time
import unittest

from blender_bridge.protocol import (
    PacketType,
    decode_frame,
    encode_frame,
    encode_resync_complete,
    read_packet,
)
from blender_bridge.sender import BridgeSender, OutboundPacket


class SenderTests(unittest.TestCase):
    def test_project_root_request_receives_unity_path(self):
        listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        listener.bind(("127.0.0.1", 0))
        listener.listen(1)
        listener.settimeout(3.0)
        received = []
        server_errors = []

        def serve():
            try:
                connection, _ = listener.accept()
                with connection:
                    connection.settimeout(3.0)
                    received.append(read_packet(connection))
                    received.append(read_packet(connection))
                    response = encode_frame(
                        PacketType.PROJECT_ROOT,
                        "D:/Unity/Current Project".encode("utf-8"),
                        0,
                    )
                    connection.sendall(response)
            except Exception as exc:
                server_errors.append(exc)

        server = threading.Thread(target=serve, daemon=True)
        server.start()
        sender = BridgeSender(listener.getsockname()[1], b"hello")
        try:
            sender.start()
            deadline = time.monotonic() + 3.0
            while time.monotonic() < deadline:
                if any(event == "connected" for event, _ in sender.poll_events()):
                    break
                time.sleep(0.01)
            else:
                self.fail(f"Sender did not connect: {sender.last_error}")

            sender.enqueue_snapshot([])
            self.assertEqual(
                sender.request_project_root(timeout=3.0),
                "D:/Unity/Current Project",
            )
            self.assertEqual(
                [packet.packet_type for packet in received],
                [PacketType.HELLO, PacketType.PROJECT_ROOT_REQUEST],
            )
            self.assertEqual(received[1].payload, b"")
            self.assertFalse(server_errors)
        finally:
            sender.stop()
            listener.close()
            server.join(1.0)

    def test_handshake_precedes_ordered_snapshot(self):
        listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        listener.bind(("127.0.0.1", 0))
        listener.listen(1)
        listener.settimeout(3.0)
        received = []
        server_errors = []
        server_done = threading.Event()
        server_release = threading.Event()

        def serve():
            try:
                connection, _ = listener.accept()
                with connection:
                    connection.settimeout(3.0)
                    received.append(read_packet(connection))
                    received.append(read_packet(connection))
                    received.append(read_packet(connection))
                    server_done.set()
                    server_release.wait(3.0)
            except Exception as exc:
                server_errors.append(exc)
            finally:
                server_done.set()

        server = threading.Thread(target=serve, daemon=True)
        server.start()
        sender = BridgeSender(listener.getsockname()[1], b"hello")
        try:
            sender.start()
            deadline = time.monotonic() + 3.0
            while time.monotonic() < deadline:
                events = sender.poll_events()
                if any(event == "connected" for event, _ in events):
                    break
                time.sleep(0.01)
            else:
                self.fail(f"Sender did not connect: {sender.last_error}")
            sender.enqueue_snapshot(
                [
                    OutboundPacket(PacketType.MATERIALS, b'{"materials":[]}'),
                    OutboundPacket(
                        PacketType.RESYNC_COMPLETE,
                        encode_resync_complete(0, 0),
                    )
                ]
            )
            self.assertTrue(server_done.wait(3.0))
            self.assertFalse(server_errors)
            self.assertEqual(
                [packet.packet_type for packet in received],
                [PacketType.HELLO, PacketType.MATERIALS, PacketType.RESYNC_COMPLETE],
            )
            self.assertEqual([packet.sequence for packet in received], [0, 1, 2])
            self.assertTrue(received[1].flags)
            self.assertEqual(received[1].payload, b'{"materials":[]}')
            self.assertEqual(received[2].payload, struct.pack("<II", 0, 0))
        finally:
            sender.stop()
            server_release.set()
            listener.close()
            server.join(1.0)


if __name__ == "__main__":
    unittest.main()
