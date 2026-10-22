using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BlenderBridge.Editor;
using NUnit.Framework;

namespace BlenderBridge.Tests
{
    public sealed class BridgeServerTests
    {
        [Test]
        public void ServerRespondsToHelloAndPingAndRejectsAdditionalPeers()
        {
            if (BridgeServer.Status == BridgeServerStatus.AwaitingHello ||
                BridgeServer.Status == BridgeServerStatus.Connected ||
                BridgeServer.Status == BridgeServerStatus.Resyncing)
            {
                Assert.Ignore("A Blender peer is already using the bridge server.");
            }

            Assert.That(BridgeServer.Status, Is.EqualTo(BridgeServerStatus.Listening));
            int port = BridgeServer.ListeningPort;

            using (var client = new TcpClient())
            {
                client.Connect("127.0.0.1", port);
                NetworkStream stream = client.GetStream();
                stream.ReadTimeout = 5000;
                stream.WriteTimeout = 5000;
                Send(stream, BridgePacketType.Hello,
                    BridgePayloadCodec.EncodeHello(
                        5,
                        0,
                        1f,
                        "integration.blend",
                        0,
                        "{\"session_id\":\"ffeeddcc-bbaa-9988-7766-554433221100\",\"objects\":[]}"),
                    0,
                    true);

                BridgePacket resyncPacket = BridgeFrameCodec.ReadPacket(stream);
                Assert.That(resyncPacket.Type, Is.EqualTo(BridgePacketType.SceneResync));
                Assert.That(resyncPacket.Sequence, Is.EqualTo(0));
                var resync = (SceneResyncPayload)BridgePayloadCodec.Decode(resyncPacket);

                BridgePacket initialProjectRootPacket = BridgeFrameCodec.ReadPacket(stream);
                Assert.That(initialProjectRootPacket.Type, Is.EqualTo(BridgePacketType.ProjectRoot));
                Assert.That(initialProjectRootPacket.Sequence, Is.EqualTo(1));
                var initialProjectRoot =
                    (ProjectRootPayload)BridgePayloadCodec.Decode(initialProjectRootPacket);
                Assert.That(
                    initialProjectRoot.ProjectRoot,
                    Is.EqualTo(Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."))));

                Send(stream, BridgePacketType.Ping, BridgePayloadCodec.EncodePing(123), 1);
                BridgePacket pongPacket = BridgeFrameCodec.ReadPacket(stream);
                Assert.That(pongPacket.Type, Is.EqualTo(BridgePacketType.Pong));
                Assert.That(pongPacket.Sequence, Is.EqualTo(2));
                var pong = (PingPayload)BridgePayloadCodec.Decode(pongPacket);
                Assert.That(pong.TimestampMilliseconds, Is.EqualTo(123));

                Send(stream, BridgePacketType.ProjectRootRequest, Array.Empty<byte>(), 2);
                BridgePacket projectRootPacket = BridgeFrameCodec.ReadPacket(stream);
                Assert.That(projectRootPacket.Type, Is.EqualTo(BridgePacketType.ProjectRoot));
                Assert.That(projectRootPacket.Sequence, Is.EqualTo(3));
                var projectRootPayload =
                    (ProjectRootPayload)BridgePayloadCodec.Decode(projectRootPacket);
                Assert.That(
                    projectRootPayload.ProjectRoot,
                    Is.EqualTo(Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."))));

                Send(stream, BridgePacketType.Hello,
                    BridgePayloadCodec.EncodeHello(
                        5,
                        0,
                        1f,
                        "integration.blend",
                        0,
                        "{\"session_id\":\"ffeeddcc-bbaa-9988-7766-554433221100\",\"objects\":[]}"),
                    3,
                    true);
                Send(stream, BridgePacketType.ResyncComplete,
                    BridgePayloadCodec.EncodeResyncComplete(resync.ResyncId, 0),
                    4);
                Assert.That(
                    SpinWait.SpinUntil(
                        () => BridgeServer.PendingMessageCount >= 3,
                        TimeSpan.FromSeconds(2)),
                    Is.True,
                    "The server did not queue the complete resync snapshot.");

                BridgeInboundMessage[] messages = BridgeServer.DrainPendingMessages(8);
                Assert.That(messages.Length, Is.EqualTo(3));
                Assert.That(messages[0].Type, Is.EqualTo(BridgePacketType.Hello));
                Assert.That(messages[1].Type, Is.EqualTo(BridgePacketType.Hello));
                Assert.That(messages[1].IsSnapshot, Is.True);
                Assert.That(messages[2].Type, Is.EqualTo(BridgePacketType.ResyncComplete));
                Assert.That(messages[2].IsSnapshot, Is.True);
                Assert.That(BridgeServer.Status, Is.EqualTo(BridgeServerStatus.Resyncing));

                using (var additionalPeer = new TcpClient())
                {
                    additionalPeer.Connect("127.0.0.1", port);
                    NetworkStream additionalStream = additionalPeer.GetStream();
                    additionalStream.ReadTimeout = 2000;
                    Assert.That(additionalStream.ReadByte(), Is.EqualTo(-1));
                }
            }
        }

        private static void Send(
            NetworkStream stream,
            BridgePacketType packetType,
            byte[] payload,
            uint sequence,
            bool compress = false)
        {
            byte[] frame = BridgeFrameCodec.EncodeFrame((byte)packetType, payload, sequence, compress);
            stream.Write(frame, 0, frame.Length);
            stream.Flush();
        }
    }
}
