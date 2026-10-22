using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using BlenderBridge;
using UnityEditor;
using UnityEngine;

namespace BlenderBridge.Editor
{
    public enum BridgeServerStatus
    {
        Stopped,
        Listening,
        AwaitingHello,
        Connected,
        Resyncing,
        Stopping
    }

    [InitializeOnLoad]
    public static class BridgeServer
    {
        private const int ReadTimeoutMilliseconds = 5000;
        private const int ShutdownJoinMilliseconds = 3000;
        private const int MaximumMessagesPerUpdate = 64;
        private const int MessageBatchSize = 8;
        private const double MaximumApplyTimePerUpdateSeconds = 0.004;
        private static readonly object Gate = new object();
        private static readonly ConcurrentQueue<ServerLog> PendingLogs = new ConcurrentQueue<ServerLog>();
        private static readonly int EditorMainThreadId = Thread.CurrentThread.ManagedThreadId;

        private static TcpListener listener;
        private static Thread acceptThread;
        private static ClientConnection activeConnection;
        private static BridgeServerStatus status = BridgeServerStatus.Stopped;
        private static string lastError;
        private static string unityProjectRoot;
        private static int listeningPort;
        private static volatile bool stopRequested;
        private static bool shutdownFailed;
        private static BridgeSnapshotTransaction snapshotTransaction;
        private static BridgeInboundMessage[] snapshotApplyMessages;
        private static int snapshotApplyIndex;
        private static uint snapshotApplyId;
        private static double snapshotApplyElapsedMilliseconds;
        private static long appliedPacketCount;

        private static double currentUpdateDrainMilliseconds;
        private static double lastUpdateMilliseconds;
        private static double peakUpdateMilliseconds;
        private static double totalUpdateMilliseconds;
        private static double lastWorkMilliseconds;
        private static double totalWorkMilliseconds;
        private static long updateCount;
        private static long updatesOverBudgetCount;
        private static double lastDrainMilliseconds;
        private static double lastLiveApplyMilliseconds;
        private static double lastSnapshotApplyMilliseconds;
        private static int lastSnapshotMessageCount;
        private static double totalLiveApplyMilliseconds;
        private static double totalSnapshotApplyMilliseconds;
        private static long liveApplyCount;
        private static long snapshotApplyCount;

        static BridgeServer()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            AssemblyReloadEvents.afterAssemblyReload += Start;
            EditorApplication.quitting += Stop;
            EditorApplication.update += Update;
            Start();
        }

        public static BridgeServerStatus Status
        {
            get
            {
                lock (Gate)
                    return status;
            }
        }

        public static int ListeningPort
        {
            get
            {
                lock (Gate)
                    return listeningPort;
            }
        }

        public static string LastError
        {
            get
            {
                lock (Gate)
                    return lastError;
            }
        }

        public static string PeerName
        {
            get
            {
                lock (Gate)
                    return activeConnection?.PeerName;
            }
        }

        public static string SessionId
        {
            get
            {
                lock (Gate)
                    return activeConnection?.SessionId;
            }
        }

        public static float PeerUnitScale
        {
            get
            {
                lock (Gate)
                    return activeConnection?.UnitScale ?? 0f;
            }
        }

        public static long ReceivedPacketCount
        {
            get
            {
                lock (Gate)
                    return activeConnection?.ReceivedPacketCount ?? 0;
            }
        }

        public static long ReceivedPayloadBytes
        {
            get
            {
                lock (Gate)
                    return activeConnection?.ReceivedPayloadBytes ?? 0;
            }
        }

        public static long AppliedPacketCount
        {
            get
            {
                lock (Gate)
                    return appliedPacketCount;
            }
        }

        public static double TargetUpdateMilliseconds =>
            MaximumApplyTimePerUpdateSeconds * 1000d;

        public static double LastUpdateMilliseconds => lastUpdateMilliseconds;

        public static double PeakUpdateMilliseconds => peakUpdateMilliseconds;

        public static double AverageUpdateMilliseconds =>
            updateCount == 0 ? 0d : totalUpdateMilliseconds / updateCount;

        public static long UpdateCount => updateCount;

        public static double LastWorkMilliseconds => lastWorkMilliseconds;

        public static double AverageWorkMilliseconds =>
            updateCount == 0 ? 0d : totalWorkMilliseconds / updateCount;

        public static long UpdatesOverBudgetCount => updatesOverBudgetCount;

        public static double LastDrainMilliseconds => lastDrainMilliseconds;

        public static double LastLiveApplyMilliseconds => lastLiveApplyMilliseconds;

        public static double LastSnapshotApplyMilliseconds => lastSnapshotApplyMilliseconds;

        public static int LastSnapshotMessageCount => lastSnapshotMessageCount;

        public static double AverageLiveApplyMilliseconds =>
            liveApplyCount == 0 ? 0d : totalLiveApplyMilliseconds / liveApplyCount;

        public static double AverageSnapshotApplyMilliseconds =>
            snapshotApplyCount == 0 ? 0d : totalSnapshotApplyMilliseconds / snapshotApplyCount;
        public static int SnapshotApplyMessageCount => snapshotApplyMessages?.Length ?? 0;
        public static int SnapshotAppliedMessageCount => snapshotApplyIndex;

        public static long PingEchoCount
        {
            get
            {
                lock (Gate)
                    return activeConnection?.PingEchoCount ?? 0;
            }
        }

        public static bool HasCommittedSnapshot
        {
            get
            {
                lock (Gate)
                    return activeConnection != null && activeConnection.SnapshotCommitted;
            }
        }

        public static int PendingMessageCount
        {
            get
            {
                lock (Gate)
                {
                    return activeConnection == null
                        ? 0
                        : activeConnection.Inbox.PendingStateCount + activeConnection.Inbox.PendingControlCount;
                }
            }
        }

        public static int PendingStateSlotCount
        {
            get
            {
                lock (Gate)
                    return activeConnection?.Inbox.PendingStateCount ?? 0;
            }
        }

        public static int PendingControlEventCount
        {
            get
            {
                lock (Gate)
                    return activeConnection?.Inbox.PendingControlCount ?? 0;
            }
        }

        public static long PendingInboxBytes
        {
            get
            {
                lock (Gate)
                    return activeConnection?.Inbox.PendingBytes ?? 0;
            }
        }

        public static BridgeInboundMessage[] DrainPendingMessages(int maximumMessages)
        {
            if (Thread.CurrentThread.ManagedThreadId != EditorMainThreadId)
                throw new InvalidOperationException("Bridge messages must be drained from the Unity Editor main thread.");
            if (maximumMessages < 1)
                throw new ArgumentOutOfRangeException(nameof(maximumMessages), "The drain limit must be positive.");

            BridgeInbox inbox;
            lock (Gate)
                inbox = activeConnection?.Inbox;
            return inbox == null ? Array.Empty<BridgeInboundMessage>() : inbox.Drain(maximumMessages);
        }

        public static void Start()
        {
            lock (Gate)
            {
                if (status != BridgeServerStatus.Stopped)
                    return;
                if (shutdownFailed ||
                    (acceptThread != null && acceptThread.IsAlive) ||
                    (activeConnection?.ReaderThread != null && activeConnection.ReaderThread.IsAlive))
                {
                    ReportLocked("The previous bridge server did not stop cleanly; refusing to start a second listener.");
                    return;
                }
            }

            if (!TryGetConfiguration(
                    out int port,
                    out int maximumPendingStateSlots,
                    out int maximumQueuedControlEvents,
                    out long maximumPendingInboxBytes,
                    out string settingsError))
            {
                Report(settingsError);
                return;
            }

            unityProjectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            TcpListener newListener = null;
            try
            {
                newListener = new TcpListener(IPAddress.Loopback, port);
                newListener.Server.ExclusiveAddressUse = true;
                newListener.Start(1);

                var thread = new Thread(() => AcceptConnections(
                    newListener,
                    maximumPendingStateSlots,
                    maximumQueuedControlEvents,
                    maximumPendingInboxBytes))
                {
                    IsBackground = true,
                    Name = "Blender Bridge accept"
                };

                lock (Gate)
                {
                    listener = newListener;
                    acceptThread = thread;
                    listeningPort = port;
                    lastError = null;
                    stopRequested = false;
                    status = BridgeServerStatus.Listening;
                    thread.Start();
                }
            }
            catch (SocketException exception)
            {
                newListener?.Stop();
                string error = exception.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? $"Port {port} is already in use on 127.0.0.1. Change the port in the Blender Bridge Settings asset and use the same port in Blender."
                    : exception.Message;
                Report($"Could not listen on 127.0.0.1:{port}: {error}");
            }
            catch (InvalidOperationException exception)
            {
                newListener?.Stop();
                Report($"Could not start the bridge listener on port {port}: {exception.Message}");
            }
        }

        public static void Stop()
        {
            TcpListener listenerToStop;
            Thread acceptThreadToJoin;
            ClientConnection connectionToClose;
            lock (Gate)
            {
                if (status == BridgeServerStatus.Stopped && listener == null && acceptThread == null)
                    return;

                status = BridgeServerStatus.Stopping;
                stopRequested = true;
                listenerToStop = listener;
                acceptThreadToJoin = acceptThread;
                connectionToClose = activeConnection;
            }

            connectionToClose?.Close();
            listenerToStop?.Stop();

            bool acceptStopped = JoinThread(acceptThreadToJoin, "accept");
            bool readerStopped = JoinThread(connectionToClose?.ReaderThread, "client reader");
            if (!acceptStopped)
                Debug.LogError($"[Blender Bridge] The bridge accept thread did not stop within {ShutdownJoinMilliseconds} ms.");
            if (!readerStopped)
                Debug.LogError($"[Blender Bridge] The bridge client reader thread did not stop within {ShutdownJoinMilliseconds} ms.");

            lock (Gate)
            {
                if (ReferenceEquals(activeConnection, connectionToClose))
                    activeConnection = null;
                if (ReferenceEquals(listener, listenerToStop))
                    listener = null;
                if (ReferenceEquals(acceptThread, acceptThreadToJoin))
                    acceptThread = null;

                shutdownFailed = !acceptStopped || !readerStopped;
                status = shutdownFailed ? BridgeServerStatus.Stopping : BridgeServerStatus.Stopped;
                snapshotTransaction = null;
                CancelSnapshotApplication();
                if (!shutdownFailed)
                    listeningPort = 0;
            }
            BridgeLiveScene.CleanupRetiredScenes(int.MaxValue);
        }

        private static void Update()
        {
            long updateStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            currentUpdateDrainMilliseconds = 0d;
            double workMilliseconds = 0d;
            try
            {
                while (PendingLogs.TryDequeue(out ServerLog message))
                {
                    if (message.IsError)
                        Debug.LogError("[Blender Bridge] " + message.Text);
                    else
                        Debug.LogWarning("[Blender Bridge] " + message.Text);
                }

                long workStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                double deadline = EditorApplication.timeSinceStartup + MaximumApplyTimePerUpdateSeconds;
                BridgeLiveScene.CleanupRetiredScenes(1024, deadline);
                bool connectionIsActive;
                lock (Gate)
                    connectionIsActive = activeConnection != null;
                BridgeLiveScene.UpdateConnectionState(connectionIsActive);
                if (!connectionIsActive)
                {
                    snapshotTransaction = null;
                    CancelSnapshotApplication();
                    workMilliseconds = ElapsedMilliseconds(workStarted);
                    lastWorkMilliseconds = workMilliseconds;
                    return;
                }

                int remainingMessages = MaximumMessagesPerUpdate;
                while (remainingMessages > 0 && EditorApplication.timeSinceStartup < deadline)
                {
                    if (snapshotApplyMessages != null)
                    {
                        if (!IsCurrentSnapshot(snapshotApplyId))
                        {
                            CancelSnapshotApplication();
                            continue;
                        }

                        int count = Math.Min(
                            MessageBatchSize,
                            Math.Min(remainingMessages, snapshotApplyMessages.Length - snapshotApplyIndex));
                        var batch = new ArraySegment<BridgeInboundMessage>(
                            snapshotApplyMessages,
                            snapshotApplyIndex,
                            count);
                        try
                        {
                            long snapshotStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                            BridgeLiveScene.ApplySnapshotMessages(batch);
                            snapshotApplyElapsedMilliseconds += ElapsedMilliseconds(snapshotStarted);
                            snapshotApplyIndex += count;
                            remainingMessages -= count;

                            if (snapshotApplyIndex == snapshotApplyMessages.Length)
                            {
                                long commitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                                if (!CompleteSnapshot(snapshotApplyId, snapshotApplyMessages.Length))
                                {
                                    CancelSnapshotApplication();
                                    continue;
                                }
                                snapshotApplyElapsedMilliseconds += ElapsedMilliseconds(commitStarted);

                                lastSnapshotApplyMilliseconds = snapshotApplyElapsedMilliseconds;
                                totalSnapshotApplyMilliseconds += snapshotApplyElapsedMilliseconds;
                                snapshotApplyCount++;
                                lastSnapshotMessageCount = snapshotApplyMessages.Length;
                                ClearSnapshotApplicationState();
                            }
                        }
                        catch (Exception exception)
                        {
                            CancelSnapshotApplication();
                            Report($"Could not apply Blender resync snapshot: {exception.Message}");
                            RequestResyncAfterSnapshotFailure("snapshot apply failure");
                        }

                        continue;
                    }

                    long drainStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    BridgeInboundMessage[] messages =
                        DrainPendingMessages(Math.Min(MessageBatchSize, remainingMessages));
                    currentUpdateDrainMilliseconds += ElapsedMilliseconds(drainStarted);
                    if (messages.Length == 0)
                        break;

                    lastDrainMilliseconds = currentUpdateDrainMilliseconds;
                    ProcessMessages(messages);
                    remainingMessages -= messages.Length;
                }

                workMilliseconds = ElapsedMilliseconds(workStarted);
                lastWorkMilliseconds = workMilliseconds;
            }
            finally
            {
                RecordUpdateTiming(updateStarted, workMilliseconds);
            }
        }

        private static void RecordUpdateTiming(long updateStarted, double workMilliseconds)
        {
            double elapsed = ElapsedMilliseconds(updateStarted);
            lastUpdateMilliseconds = elapsed;
            totalUpdateMilliseconds += elapsed;
            totalWorkMilliseconds += workMilliseconds;
            updateCount++;
            if (elapsed > peakUpdateMilliseconds)
                peakUpdateMilliseconds = elapsed;
            if (workMilliseconds > MaximumApplyTimePerUpdateSeconds * 1000d)
                updatesOverBudgetCount++;
        }

        private static double ElapsedMilliseconds(long startedTimestamp)
        {
            return (System.Diagnostics.Stopwatch.GetTimestamp() - startedTimestamp) * 1000d /
                   System.Diagnostics.Stopwatch.Frequency;
        }

        private static void ProcessMessages(IReadOnlyList<BridgeInboundMessage> messages)
        {
            var liveMessages = new List<BridgeInboundMessage>();
            for (int i = 0; i < messages.Count; i++)
            {
                BridgeInboundMessage message = messages[i];
                if (!message.IsSnapshot)
                {
                    if (message.Type != BridgePacketType.Hello)
                        liveMessages.Add(message);
                    continue;
                }

                if (!IsCurrentSnapshot(message.SnapshotId))
                    continue;

                ApplyLiveMessages(liveMessages);
                liveMessages.Clear();
                try
                {
                    if (message.Type == BridgePacketType.Hello)
                    {
                        if (snapshotTransaction != null &&
                            snapshotTransaction.SnapshotId == message.SnapshotId)
                        {
                            snapshotTransaction.TryAdd(message, out _);
                            throw new InvalidDataException("Snapshot contains more than one Hello packet.");
                        }

                        CancelSnapshotApplication();
                        snapshotTransaction = new BridgeSnapshotTransaction(
                            message,
                            GetMaximumStagingBytes());
                        continue;
                    }

                    if (snapshotTransaction == null)
                        throw new InvalidDataException("Received snapshot data before its Hello inventory.");

                    if (snapshotTransaction.TryAdd(message, out BridgeInboundMessage[] snapshotMessages))
                    {
                        try
                        {
                            BridgeLiveScene.BeginSnapshotReplacement();
                            snapshotApplyMessages = snapshotMessages;
                            snapshotApplyIndex = 0;
                            snapshotApplyId = message.SnapshotId;
                            snapshotApplyElapsedMilliseconds = 0d;
                        }
                        catch (Exception exception)
                        {
                            // Leaving SnapshotCommitted false silently drops every
                            // later live packet. Reset and ask Blender for a new
                            // snapshot instead.
                            snapshotTransaction = null;
                            CancelSnapshotApplication();
                            Report($"Could not apply Blender resync snapshot: {exception.Message}");
                            RequestResyncAfterSnapshotFailure("snapshot apply failure");
                            continue;
                        }
                        snapshotTransaction = null;
                    }
                }
                catch (InvalidDataException exception)
                {
                    snapshotTransaction = null;
                    CancelSnapshotApplication();
                    Report($"Rejected Blender resync snapshot: {exception.Message}");
                    RequestResyncAfterSnapshotFailure("snapshot validation failure");
                }
                catch (BridgeInboxCapacityException exception)
                {
                    snapshotTransaction = null;
                    CancelSnapshotApplication();
                    Report($"Could not stage Blender resync snapshot: {exception.Message}");
                    ClientConnection connection;
                    lock (Gate)
                        connection = activeConnection;
                    connection?.Close();
                }
            }

            ApplyLiveMessages(liveMessages);
        }

        private static bool IsCurrentSnapshot(uint snapshotId)
        {
            lock (Gate)
                return activeConnection != null &&
                       activeConnection.CurrentResyncId == snapshotId;
        }

        private static long GetMaximumStagingBytes()
        {
            lock (Gate)
                return activeConnection?.MaximumStagingBytes ??
                       (long)BridgeSettings.DefaultMaximumPendingInboxMegabytes * 1024 * 1024;
        }

        private static void ApplyLiveMessages(IReadOnlyList<BridgeInboundMessage> messages)
        {
            if (messages.Count == 0)
                return;

            lock (Gate)
            {
                if (activeConnection == null ||
                    status != BridgeServerStatus.Connected ||
                    !activeConnection.SnapshotCommitted)
                    return;
            }

            long applyStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            BridgeLiveScene.ApplyMessages(messages);
            double elapsed = ElapsedMilliseconds(applyStarted);
            lastLiveApplyMilliseconds = elapsed;
            totalLiveApplyMilliseconds += elapsed;
            liveApplyCount++;
            lock (Gate)
                appliedPacketCount += messages.Count;
        }

        private static bool CompleteSnapshot(uint snapshotId, int appliedMessages)
        {
            lock (Gate)
            {
                if (activeConnection == null || activeConnection.CurrentResyncId != snapshotId)
                    return false;

                BridgeLiveScene.CommitSnapshotReplacement();
                activeConnection.SnapshotCommitted = true;
                status = BridgeServerStatus.Connected;
                appliedPacketCount += appliedMessages;
                return true;
            }
        }

        private static void CancelSnapshotApplication()
        {
            if (snapshotApplyMessages != null)
                BridgeLiveScene.AbortSnapshotReplacement();
            ClearSnapshotApplicationState();
        }

        private static void ClearSnapshotApplicationState()
        {
            snapshotApplyMessages = null;
            snapshotApplyIndex = 0;
            snapshotApplyId = 0;
            snapshotApplyElapsedMilliseconds = 0d;
        }

        public static bool RequestResyncFromWindow()
        {
            ClientConnection connection;
            lock (Gate)
            {
                connection = activeConnection;
                if (status != BridgeServerStatus.Connected ||
                    connection == null ||
                    !connection.HasHello ||
                    connection.IsClosed ||
                    connection.HasOutstandingResync)
                    return false;

                status = BridgeServerStatus.Resyncing;
                connection.SnapshotCommitted = false;
            }

            try
            {
                RequestResync(connection, connection.Client.GetStream());
                return true;
            }
            catch (IOException exception)
            {
                Report($"Could not request a full resync: {exception.Message}");
                connection.Close();
            }
            catch (SocketException exception)
            {
                Report($"Could not request a full resync: {exception.Message}");
                connection.Close();
            }
            catch (ObjectDisposedException)
            {
                connection.Close();
            }
            catch (InvalidOperationException exception)
            {
                Report($"Could not request a full resync: {exception.Message}");
                connection.Close();
            }

            return false;
        }

        internal static void ReportApplicationError(string message)
        {
            Report(message);
        }

        internal static void ReportApplicationWarning(string message)
        {
            Warn(message);
        }

        internal static void RequestResyncForApplicationError(string reason)
        {
            ClientConnection connection;
            lock (Gate)
            {
                if ((status != BridgeServerStatus.Connected && status != BridgeServerStatus.Resyncing) ||
                    activeConnection == null)
                    return;

                connection = activeConnection;
                if (connection.HasOutstandingResync)
                    return;

                status = BridgeServerStatus.Resyncing;
                connection.SnapshotCommitted = false;
            }

            try
            {
                RequestResync(connection, connection.Client.GetStream());
                Report($"Requested a full resync after rejecting invalid live data ({reason}).");
            }
            catch (IOException exception)
            {
                Report($"Could not request a full resync: {exception.Message}");
                connection.Close();
            }
            catch (SocketException exception)
            {
                Report($"Could not request a full resync: {exception.Message}");
                connection.Close();
            }
            catch (ObjectDisposedException)
            {
                connection.Close();
            }
            catch (InvalidOperationException exception)
            {
                Report($"Could not request a full resync: {exception.Message}");
                connection.Close();
            }
        }

        private static void RequestResyncAfterSnapshotFailure(string reason)
        {
            ClientConnection connection;
            lock (Gate)
            {
                connection = activeConnection;
                if (connection == null || connection.IsClosed)
                    return;

                // This also runs during the very first snapshot, when status is
                // AwaitingHello, so RequestResyncForApplicationError would refuse
                // to act and leave the live-message gate closed forever.
                status = BridgeServerStatus.Resyncing;
                connection.SnapshotCommitted = false;
            }

            try
            {
                RequestResync(connection, connection.Client.GetStream());
                Report($"Requested a full resync after rejecting a snapshot ({reason}).");
            }
            catch (Exception exception)
            {
                Report($"Could not request a full resync: {exception.Message}");
                connection.Close();
            }
        }

        private static bool TryGetConfiguration(
            out int port,
            out int maximumPendingStateSlots,
            out int maximumQueuedControlEvents,
            out long maximumPendingInboxBytes,
            out string error)
        {
            port = BridgeSettings.DefaultPort;
            maximumPendingStateSlots = BridgeSettings.DefaultMaximumPendingStateSlots;
            maximumQueuedControlEvents = BridgeSettings.DefaultMaximumQueuedControlEvents;
            maximumPendingInboxBytes =
                (long)BridgeSettings.DefaultMaximumPendingInboxMegabytes * 1024 * 1024;
            error = null;

            string[] settingsGuids = AssetDatabase.FindAssets("t:BridgeSettings");
            if (settingsGuids.Length > 1)
            {
                error = "Multiple BridgeSettings assets were found. Keep exactly one settings asset to select a listening port.";
                return false;
            }

            if (settingsGuids.Length == 0)
                return true;

            string path = AssetDatabase.GUIDToAssetPath(settingsGuids[0]);
            BridgeSettings settings = AssetDatabase.LoadAssetAtPath<BridgeSettings>(path);
            if (settings == null)
            {
                error = $"Could not load BridgeSettings asset at '{path}'.";
                return false;
            }

            if (!settings.TryValidate(out error))
                return false;

            port = settings.Port;
            maximumPendingStateSlots = settings.MaximumPendingStateSlots;
            maximumQueuedControlEvents = settings.MaximumQueuedControlEvents;
            maximumPendingInboxBytes = settings.MaximumPendingInboxBytes;
            return true;
        }

        private static void AcceptConnections(
            TcpListener listeningSocket,
            int maximumPendingStateSlots,
            int maximumQueuedControlEvents,
            long maximumPendingInboxBytes)
        {
            while (!stopRequested)
            {
                TcpClient client;
                try
                {
                    client = listeningSocket.AcceptTcpClient();
                }
                catch (SocketException exception)
                {
                    if (!stopRequested)
                        FailListener(listeningSocket, exception.Message);
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (InvalidOperationException exception)
                {
                    if (!stopRequested)
                        FailListener(listeningSocket, exception.Message);
                    break;
                }

                try
                {
                    client.NoDelay = true;
                }
                catch (SocketException exception)
                {
                    client.Close();
                    if (!stopRequested)
                        Report($"Could not configure a bridge peer socket: {exception.Message}");
                    continue;
                }

                var connection = new ClientConnection(
                    client,
                    maximumPendingStateSlots,
                    maximumQueuedControlEvents,
                    maximumPendingInboxBytes);
                bool rejected;
                lock (Gate)
                {
                    rejected = stopRequested || activeConnection != null;
                    if (!rejected)
                    {
                        activeConnection = connection;
                        status = BridgeServerStatus.AwaitingHello;
                        connection.ReaderThread = new Thread(() => ReadClient(connection))
                        {
                            IsBackground = true,
                            Name = "Blender Bridge client"
                        };
                        connection.ReaderThread.Start();
                    }
                }

                if (rejected)
                {
                    client.Close();
                    if (!stopRequested)
                        Warn("Rejected an additional bridge peer; only one Blender connection is supported.");
                }
            }
        }

        private static void FailListener(TcpListener listeningSocket, string reason)
        {
            Report($"The bridge listener stopped accepting connections: {reason}");
            ClientConnection connectionToClose;
            lock (Gate)
            {
                stopRequested = true;
                connectionToClose = activeConnection;
                if (ReferenceEquals(listener, listeningSocket))
                {
                    listener = null;
                    listeningPort = 0;
                    status = BridgeServerStatus.Stopped;
                }
            }

            connectionToClose?.Close();
            listeningSocket.Stop();
        }

        private static void ReadClient(ClientConnection connection)
        {
            try
            {
                NetworkStream stream = connection.Client.GetStream();
                stream.ReadTimeout = ReadTimeoutMilliseconds;
                stream.WriteTimeout = ReadTimeoutMilliseconds;

                while (!stopRequested && !connection.IsClosed)
                {
                    BridgePacket packet = BridgeFrameCodec.ReadPacket(stream);
                    connection.RecordReceived(packet.Payload.LongLength);
                    ValidateSequence(connection, packet);
                    HandlePacket(connection, stream, packet);
                }
            }
            catch (IOException exception)
            {
                if (!stopRequested && !connection.IsClosed)
                    Report($"Bridge peer disconnected or sent an invalid frame: {exception.Message}");
            }
            catch (BridgeInboxCapacityException exception)
            {
                if (!stopRequested && !connection.IsClosed)
                    Report($"Bridge inbox overflow invalidated the current snapshot; disconnecting for a fresh resync: {exception.Message}");
            }
            catch (InvalidDataException exception)
            {
                if (!stopRequested && !connection.IsClosed)
                    Report($"Rejected malformed bridge traffic: {exception.Message}");
            }
            catch (SocketException exception)
            {
                if (!stopRequested && !connection.IsClosed)
                    Report($"Bridge socket failed: {exception.Message}");
            }
            catch (ArgumentException exception)
            {
                if (!stopRequested && !connection.IsClosed)
                    Report($"Rejected invalid bridge payload: {exception.Message}");
            }
            catch (OverflowException exception)
            {
                if (!stopRequested && !connection.IsClosed)
                    Report($"Rejected out-of-range bridge payload data: {exception.Message}");
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                connection.Close();
                connection.Inbox.Clear();
                lock (Gate)
                {
                    if (ReferenceEquals(activeConnection, connection))
                    {
                        activeConnection = null;
                        if (!stopRequested)
                            status = BridgeServerStatus.Listening;
                    }
                }
            }
        }

        private static void ValidateSequence(ClientConnection connection, BridgePacket packet)
        {
            if (!connection.HasReceivedSequence)
            {
                if (packet.Sequence != 0)
                    throw new InvalidDataException("The first Blender packet must use sequence zero.");
                connection.HasReceivedSequence = true;
                connection.LastReceivedSequence = packet.Sequence;
                return;
            }

            if (!BridgeFrameCodec.IsSequenceNewer(packet.Sequence, connection.LastReceivedSequence))
                throw new InvalidDataException("Blender packet sequence is not strictly increasing.");

            connection.LastReceivedSequence = packet.Sequence;
        }

        private static void HandlePacket(ClientConnection connection, NetworkStream stream, BridgePacket packet)
        {
            if (!connection.HasHello &&
                (!packet.IsKnownType || packet.Type != BridgePacketType.Hello))
            {
                throw new InvalidDataException("The first Blender packet must be Hello.");
            }

            if (!packet.IsKnownType)
            {
                Warn($"Skipped unknown Blender packet type 0x{packet.RawType:X2}.");
                return;
            }

            if (!connection.HasHello)
            {
                HelloPayload hello = (HelloPayload)BridgePayloadCodec.Decode(packet);
                ValidateHello(hello);
                connection.SetHello(hello);
                connection.Inbox.Enqueue(packet.Sequence, packet.Type, hello);
                lock (Gate)
                {
                    if (ReferenceEquals(activeConnection, connection))
                        status = BridgeServerStatus.Resyncing;
                }
                RequestResync(connection, stream);
                SendProjectRoot(connection, stream);
                return;
            }

            switch (packet.Type)
            {
                case BridgePacketType.Ping:
                {
                    var ping = (PingPayload)BridgePayloadCodec.Decode(packet);
                    SendPacket(connection, stream, BridgePacketType.Pong,
                        BridgePayloadCodec.EncodePong(ping.TimestampMilliseconds));
                    connection.RecordPingEcho();
                    break;
                }
                case BridgePacketType.ProjectRootRequest:
                    if (packet.Payload.Length != 0)
                        throw new InvalidDataException("Project root requests must have an empty payload.");
                    SendProjectRoot(connection, stream);
                    break;
                case BridgePacketType.Hello:
                {
                    HelloPayload hello = (HelloPayload)BridgePayloadCodec.Decode(packet);
                    ValidateHello(hello);
                    bool changed = connection.UpdateHello(hello);
                    if (connection.TryBeginSnapshot(out uint snapshotId))
                    {
                        connection.Inbox.Enqueue(
                            packet.Sequence,
                            packet.Type,
                            hello,
                            isSnapshot: true,
                            snapshotId: snapshotId);
                    }
                    else if (changed)
                    {
                        connection.Inbox.Enqueue(packet.Sequence, packet.Type, hello);
                        lock (Gate)
                        {
                            if (ReferenceEquals(activeConnection, connection))
                                status = BridgeServerStatus.Resyncing;
                        }
                        RequestResync(connection, stream);
                    }
                    break;
                }
                case BridgePacketType.ResyncComplete:
                {
                    var complete = (ResyncCompletePayload)BridgePayloadCodec.Decode(packet);
                    if (complete.ResyncId == connection.CurrentResyncId)
                    {
                        connection.EndSnapshot();
                        connection.Inbox.Enqueue(
                            packet.Sequence,
                            packet.Type,
                            complete,
                            isSnapshot: true,
                            snapshotId: complete.ResyncId);
                    }
                    break;
                }
                case BridgePacketType.Transform:
                case BridgePacketType.MeshFull:
                case BridgePacketType.ObjectDeleted:
                case BridgePacketType.Visibility:
                case BridgePacketType.Hierarchy:
                case BridgePacketType.Materials:
                    BridgePayload payload = BridgePayloadCodec.Decode(packet);
                    if (connection.IsSnapshotInProgress)
                    {
                        connection.Inbox.Enqueue(
                            packet.Sequence,
                            packet.Type,
                            payload,
                            isSnapshot: true,
                            snapshotId: connection.CurrentResyncId);
                    }
                    else
                    {
                        connection.Inbox.Enqueue(packet.Sequence, packet.Type, payload);
                    }
                    break;
                default:
                    throw new InvalidDataException(
                        $"Blender sent packet type {packet.Type}, which is not valid in the Blender-to-Unity direction.");
            }
        }

        private static void ValidateHello(HelloPayload hello)
        {
            if (hello == null || !Guid.TryParse(hello.SessionId, out Guid sessionId) || sessionId == Guid.Empty)
                throw new InvalidDataException("Hello must contain a valid non-empty session UUID.");
        }

        private static void RequestResync(ClientConnection connection, NetworkStream stream)
        {
            uint resyncId = connection.NextResyncId();
            connection.SnapshotCommitted = false;
            SendPacket(connection, stream, BridgePacketType.SceneResync,
                BridgePayloadCodec.EncodeSceneResync(resyncId));
        }

        private static void SendProjectRoot(ClientConnection connection, NetworkStream stream)
        {
            SendPacket(
                connection,
                stream,
                BridgePacketType.ProjectRoot,
                BridgePayloadCodec.EncodeProjectRoot(unityProjectRoot));
        }

        private static void SendPacket(
            ClientConnection connection,
            NetworkStream stream,
            BridgePacketType packetType,
            byte[] payload)
        {
            lock (connection.SendGate)
            {
                uint sequence = connection.NextOutgoingSequence();
                byte[] frame = BridgeFrameCodec.EncodeFrame((byte)packetType, payload, sequence);
                stream.Write(frame, 0, frame.Length);
                stream.Flush();
            }
        }

        private static bool JoinThread(Thread thread, string name)
        {
            if (thread == null || ReferenceEquals(Thread.CurrentThread, thread))
                return true;

            if (thread.Join(ShutdownJoinMilliseconds))
                return true;

            lock (Gate)
                lastError = $"The bridge {name} thread did not stop within {ShutdownJoinMilliseconds} ms.";
            return false;
        }

        private static void Report(string message)
        {
            lock (Gate)
                ReportLocked(message);
        }

        private static void Warn(string message)
        {
            PendingLogs.Enqueue(new ServerLog(message, false));
        }

        private static void ReportLocked(string message)
        {
            lastError = message;
            PendingLogs.Enqueue(new ServerLog(message, true));
        }

        private sealed class ClientConnection
        {
            private readonly object stateGate = new object();
            private uint outgoingSequence;
            private uint nextResyncId;
            private string sessionId;
            private string peerName;
            private float unitScale;
            private uint currentResyncId;
            private bool resyncOutstanding;
            private bool snapshotInProgress;
            private bool snapshotCommitted;
            private bool hasHello;
            private bool isClosed;
            private long receivedPacketCount;
            private long receivedPayloadBytes;
            private long pingEchoCount;

            public ClientConnection(
                TcpClient client,
                int maximumPendingStateSlots,
                int maximumQueuedControlEvents,
                long maximumPendingInboxBytes)
            {
                Client = client;
                MaximumStagingBytes = maximumPendingInboxBytes;
                Inbox = new BridgeInbox(
                    maximumPendingStateSlots,
                    maximumQueuedControlEvents,
                    maximumPendingInboxBytes);
            }

            public readonly object SendGate = new object();
            public readonly TcpClient Client;
            public readonly BridgeInbox Inbox;
            public long MaximumStagingBytes { get; }
            public Thread ReaderThread;
            public bool HasReceivedSequence;
            public uint LastReceivedSequence;
            public bool HasHello
            {
                get
                {
                    lock (stateGate)
                        return hasHello;
                }
            }
            public uint CurrentResyncId
            {
                get
                {
                    lock (stateGate)
                        return currentResyncId;
                }
            }
            public bool IsClosed
            {
                get
                {
                    lock (stateGate)
                        return isClosed;
                }
            }
            public bool SnapshotCommitted
            {
                get
                {
                    lock (stateGate)
                        return snapshotCommitted;
                }
                set
                {
                    lock (stateGate)
                        snapshotCommitted = value;
                }
            }
            public string PeerName
            {
                get
                {
                    lock (stateGate)
                        return peerName;
                }
            }

            public string SessionId
            {
                get
                {
                    lock (stateGate)
                        return sessionId;
                }
            }

            public float UnitScale
            {
                get
                {
                    lock (stateGate)
                        return unitScale;
                }
            }

            public bool IsSnapshotInProgress
            {
                get
                {
                    lock (stateGate)
                        return snapshotInProgress;
                }
            }

            public bool HasOutstandingResync
            {
                get
                {
                    lock (stateGate)
                        return resyncOutstanding;
                }
            }

            public long ReceivedPacketCount => Interlocked.Read(ref receivedPacketCount);
            public long ReceivedPayloadBytes => Interlocked.Read(ref receivedPayloadBytes);
            public long PingEchoCount => Interlocked.Read(ref pingEchoCount);

            public void SetHello(HelloPayload hello)
            {
                lock (stateGate)
                {
                    hasHello = true;
                    sessionId = hello.SessionId;
                    peerName = hello.SessionName;
                    unitScale = hello.UnitScale;
                }
            }

            public bool UpdateHello(HelloPayload hello)
            {
                lock (stateGate)
                {
                    bool changed = !string.Equals(sessionId, hello.SessionId, StringComparison.Ordinal) ||
                                   unitScale != hello.UnitScale;
                    sessionId = hello.SessionId;
                    peerName = hello.SessionName;
                    unitScale = hello.UnitScale;
                    return changed;
                }
            }

            public bool TryBeginSnapshot(out uint snapshotId)
            {
                lock (stateGate)
                {
                    snapshotId = CurrentResyncId;
                    if (!resyncOutstanding)
                        return false;

                    snapshotInProgress = true;
                    return true;
                }
            }

            public void EndSnapshot()
            {
                lock (stateGate)
                {
                    snapshotInProgress = false;
                    resyncOutstanding = false;
                }
            }

            public void RecordReceived(long payloadBytes)
            {
                Interlocked.Increment(ref receivedPacketCount);
                Interlocked.Add(ref receivedPayloadBytes, payloadBytes);
            }

            public void RecordPingEcho()
            {
                Interlocked.Increment(ref pingEchoCount);
            }

            public uint NextOutgoingSequence()
            {
                lock (stateGate)
                {
                    uint sequence = outgoingSequence;
                    outgoingSequence = unchecked(outgoingSequence + 1);
                    return sequence;
                }
            }

            public uint NextResyncId()
            {
                lock (stateGate)
                {
                    uint id = nextResyncId;
                    nextResyncId = unchecked(nextResyncId + 1);
                    currentResyncId = id;
                    resyncOutstanding = true;
                    snapshotInProgress = false;
                    return id;
                }
            }

            public void Close()
            {
                lock (stateGate)
                {
                    if (isClosed)
                        return;
                    isClosed = true;
                }

                try
                {
                    Client.Close();
                }
                catch (SocketException)
                {
                }
            }
        }

        private sealed class ServerLog
        {
            public ServerLog(string text, bool isError)
            {
                Text = text;
                IsError = isError;
            }

            public string Text { get; }
            public bool IsError { get; }
        }
    }
}
