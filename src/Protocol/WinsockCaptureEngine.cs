using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using EasyHook;

namespace TianshuQitanLauncher.Protocol
{
    public sealed class WinsockCaptureEngine : ITransportCaptureEngine
    {
        private const int SocketError = -1;
        private const int WsaWouldBlock = 10035;
        private const int WsaInProgress = 10036;
        private const int WsaIoPending = 997;
        private const int SioGetExtensionFunctionPointer = unchecked((int)0xC8000006);
        private static readonly Guid WsaIdConnectEx = new Guid("25a207b9-ddf3-4660-8ee9-76e58c74063e");
        private readonly object syncRoot = new object();
        private readonly ProtocolWorkbenchService service;
        private readonly WorkbenchProfile profile;
        private readonly int httpRedirectPort;
        private readonly Dictionary<IntPtr, SocketContext> sockets = new Dictionary<IntPtr, SocketContext>();
        private readonly Dictionary<long, SocketContext> socketsById = new Dictionary<long, SocketContext>();
        private readonly Dictionary<IntPtr, ConnectExDelegate> connectExOriginals = new Dictionary<IntPtr, ConnectExDelegate>();
        private readonly HashSet<IntPtr> connectExHookAddresses = new HashSet<IntPtr>();
        private readonly List<LocalHook> hooks = new List<LocalHook>();
        private long nextConnectionId;
        private bool running;
        private bool disposed;

        private SocketDelegate socketHandler;
        private WsaSocketDelegate wsaSocketHandler;
        private ConnectDelegate connectHandler;
        private WsaConnectDelegate wsaConnectHandler;
        private SendDelegate sendHandler;
        private ReceiveDelegate receiveHandler;
        private SendToDelegate sendToHandler;
        private ReceiveFromDelegate receiveFromHandler;
        private CloseSocketDelegate closeHandler;
        private GetNameDelegate getPeerNameHandler;
        private GetNameDelegate getSockNameHandler;
        private WsaEventSelectDelegate eventSelectHandler;
        private WsaIoctlDelegate wsaIoctlHandler;
        private ConnectExDelegate connectExHandler;
        private ConnectExDelegate connectExOriginalFallback;

        [ThreadStatic]
        private static bool bypassRules;

        public WinsockCaptureEngine(ProtocolWorkbenchService service, WorkbenchProfile profile)
            : this(service, profile, 0)
        {
        }

        public WinsockCaptureEngine(ProtocolWorkbenchService service, WorkbenchProfile profile, int httpRedirectPort)
        {
            this.service = service;
            this.profile = profile;
            this.httpRedirectPort = Math.Max(0, httpRedirectPort);
        }

        internal static IDisposable BypassCurrentThreadInterception()
        {
            return new InterceptionBypassScope();
        }

        public bool IsRunning
        {
            get { lock (syncRoot) { return running; } }
        }

        public void Start()
        {
            lock (syncRoot)
            {
                if (running)
                {
                    return;
                }

                socketHandler = OnSocket;
                wsaSocketHandler = OnWsaSocket;
                connectHandler = OnConnect;
                wsaConnectHandler = OnWsaConnect;
                sendHandler = OnSend;
                receiveHandler = OnReceive;
                sendToHandler = OnSendTo;
                receiveFromHandler = OnReceiveFrom;
                closeHandler = OnCloseSocket;
                getPeerNameHandler = OnGetPeerName;
                getSockNameHandler = OnGetSockName;
                eventSelectHandler = OnWsaEventSelect;
                wsaIoctlHandler = OnWsaIoctl;
                connectExHandler = OnConnectEx;

                AddHook("socket", socketHandler);
                AddHook("WSASocketW", wsaSocketHandler);
                AddHook("connect", connectHandler);
                AddHook("WSAConnect", wsaConnectHandler);
                AddHook("send", sendHandler);
                AddHook("recv", receiveHandler);
                AddHook("sendto", sendToHandler);
                AddHook("recvfrom", receiveFromHandler);
                AddHook("closesocket", closeHandler);
                AddHook("getpeername", getPeerNameHandler);
                AddHook("getsockname", getSockNameHandler);
                AddHook("WSAEventSelect", eventSelectHandler);
                AddHook("WSAIoctl", wsaIoctlHandler);
                running = true;
            }
        }

        public void Stop()
        {
            lock (syncRoot)
            {
                if (!running)
                {
                    return;
                }
                running = false;
                for (int i = hooks.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        hooks[i].Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Cannot dispose Winsock hook", ex);
                    }
                }
                hooks.Clear();
                connectExHookAddresses.Clear();
                connectExOriginals.Clear();
                connectExOriginalFallback = null;
                LocalHook.Release();
            }
        }

        public bool Send(long connectionId, byte[] bytes)
        {
            SocketContext context;
            lock (syncRoot)
            {
                if (!socketsById.TryGetValue(connectionId, out context))
                {
                    return false;
                }
            }
            bytes = bytes ?? new byte[0];
            int result = CallSend(context.Connection.SocketHandle, bytes, 0);
            TransportChunk chunk = CreateChunk(context, TrafficDirection.ClientToServer, TransportOperation.Inject,
                bytes, bytes, result, result == SocketError ? NativeWinsock.WSAGetLastError() : 0,
                RuleAction.Inject, "lua:send", "Active client injection");
            service.RecordChunk(chunk);
            return result >= 0;
        }

        public bool InjectReceive(long connectionId, byte[] bytes)
        {
            SocketContext context;
            lock (syncRoot)
            {
                if (!socketsById.TryGetValue(connectionId, out context))
                {
                    return false;
                }
            }
            lock (context.SyncRoot)
            {
                context.PendingReceive.Enqueue(new PendingReceive
                {
                    Bytes = HexCodec.Copy(bytes),
                    DueUtc = DateTime.UtcNow,
                    RecordOnDelivery = true,
                    Action = RuleAction.Inject,
                    RuleId = "lua:inject_receive",
                    Note = "Active local server injection"
                });
                if (context.EventHandle != IntPtr.Zero)
                {
                    NativeWinsock.WSASetEvent(context.EventHandle);
                }
            }
            return true;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            Stop();
        }

        private IntPtr OnSocket(int addressFamily, int socketType, int protocol)
        {
            if (bypassRules)
            {
                return NativeWinsock.socket(addressFamily, socketType, protocol);
            }
            IntPtr socket = NativeWinsock.socket(addressFamily, socketType, protocol);
            if (!IsInvalidSocket(socket))
            {
                SocketContext context = EnsureSocket(socket);
                service.RecordConnection(context.Connection, "Created");
            }
            return socket;
        }

        private IntPtr OnWsaSocket(int addressFamily, int socketType, int protocol, IntPtr protocolInfo, uint group, uint flags)
        {
            if (bypassRules)
            {
                return NativeWinsock.WSASocketW(addressFamily, socketType, protocol, protocolInfo, group, flags);
            }
            IntPtr socket = NativeWinsock.WSASocketW(addressFamily, socketType, protocol, protocolInfo, group, flags);
            if (!IsInvalidSocket(socket))
            {
                SocketContext context = EnsureSocket(socket);
                service.RecordConnection(context.Connection, "Created");
            }
            return socket;
        }

        private int OnConnect(IntPtr socket, IntPtr address, int addressLength)
        {
            if (bypassRules)
            {
                return NativeWinsock.connect(socket, address, addressLength);
            }
            SocketContext context = EnsureSocket(socket);
            ApplyRemoteAddress(context, address, addressLength);
            context.Connection.State = "Connecting";
            PersistConnection(context, "Connecting", true);
            IntPtr redirectAddress;
            int redirectAddressLength;
            bool redirected = TryCreateHttpRedirect(context, address, addressLength, out redirectAddress, out redirectAddressLength);
            int result;
            try
            {
                result = NativeWinsock.connect(socket, redirected ? redirectAddress : address, redirected ? redirectAddressLength : addressLength);
            }
            finally
            {
                if (redirectAddress != IntPtr.Zero) Marshal.FreeHGlobal(redirectAddress);
            }
            int error = result == SocketError ? NativeWinsock.WSAGetLastError() : 0;
            CompleteConnect(context, result, error);
            RecordLifecycleChunk(context, TransportOperation.Connect, result, error);
            return result;
        }

        private int OnWsaConnect(
            IntPtr socket,
            IntPtr address,
            int addressLength,
            IntPtr callerData,
            IntPtr calleeData,
            IntPtr sqos,
            IntPtr gqos)
        {
            if (bypassRules)
            {
                return NativeWinsock.WSAConnect(socket, address, addressLength, callerData, calleeData, sqos, gqos);
            }
            SocketContext context = EnsureSocket(socket);
            ApplyRemoteAddress(context, address, addressLength);
            context.Connection.State = "Connecting";
            PersistConnection(context, "Connecting", true);
            IntPtr redirectAddress;
            int redirectAddressLength;
            bool redirected = TryCreateHttpRedirect(context, address, addressLength, out redirectAddress, out redirectAddressLength);
            int result;
            try
            {
                result = NativeWinsock.WSAConnect(socket, redirected ? redirectAddress : address, redirected ? redirectAddressLength : addressLength, callerData, calleeData, sqos, gqos);
            }
            finally
            {
                if (redirectAddress != IntPtr.Zero) Marshal.FreeHGlobal(redirectAddress);
            }
            int error = result == SocketError ? NativeWinsock.WSAGetLastError() : 0;
            CompleteConnect(context, result, error);
            RecordLifecycleChunk(context, TransportOperation.Connect, result, error);
            return result;
        }

        private int OnWsaIoctl(
            IntPtr socket,
            int controlCode,
            IntPtr inputBuffer,
            int inputLength,
            IntPtr outputBuffer,
            int outputLength,
            IntPtr bytesReturned,
            IntPtr overlapped,
            IntPtr completionRoutine)
        {
            int result = NativeWinsock.WSAIoctl(
                socket, controlCode, inputBuffer, inputLength, outputBuffer, outputLength,
                bytesReturned, overlapped, completionRoutine);
            int error = result == SocketError ? NativeWinsock.WSAGetLastError() : 0;
            if (!bypassRules && result == 0 && controlCode == SioGetExtensionFunctionPointer &&
                inputBuffer != IntPtr.Zero && inputLength >= 16 && outputBuffer != IntPtr.Zero && outputLength >= IntPtr.Size)
            {
                Guid requested = (Guid)Marshal.PtrToStructure(inputBuffer, typeof(Guid));
                if (requested == WsaIdConnectEx)
                {
                    IntPtr originalPointer = Marshal.ReadIntPtr(outputBuffer);
                    if (originalPointer != IntPtr.Zero)
                    {
                        ConnectExDelegate original = (ConnectExDelegate)Marshal.GetDelegateForFunctionPointer(
                            originalPointer, typeof(ConnectExDelegate));
                        lock (syncRoot)
                        {
                            connectExOriginals[socket] = original;
                            connectExOriginalFallback = original;
                        }
                        InstallConnectExHook(originalPointer);
                    }
                }
            }
            if (result == SocketError) NativeWinsock.WSASetLastError(error);
            return result;
        }

        private void InstallConnectExHook(IntPtr address)
        {
            lock (syncRoot)
            {
                if (address == IntPtr.Zero || connectExHookAddresses.Contains(address)) return;
                try
                {
                    LocalHook hook = LocalHook.Create(address, connectExHandler, this);
                    hook.ThreadACL.SetExclusiveACL(new int[0]);
                    hooks.Add(hook);
                    connectExHookAddresses.Add(address);
                    Logger.Info("Installed ConnectEx hook at 0x" + address.ToString("X"));
                }
                catch (Exception ex)
                {
                    Logger.Error("Cannot install ConnectEx hook", ex);
                    service.ReportEngineEvent("Hook", "ERROR", "Cannot hook ConnectEx: " + ex.Message, null);
                }
            }
        }

        private bool OnConnectEx(
            IntPtr socket,
            IntPtr address,
            int addressLength,
            IntPtr sendBuffer,
            int sendDataLength,
            IntPtr bytesSent,
            IntPtr overlapped)
        {
            ConnectExDelegate original;
            lock (syncRoot)
            {
                if (!connectExOriginals.TryGetValue(socket, out original)) original = connectExOriginalFallback;
                if (original == null)
                {
                    NativeWinsock.WSASetLastError(10045);
                    return false;
                }
            }
            if (bypassRules)
            {
                return original(socket, address, addressLength, sendBuffer, sendDataLength, bytesSent, overlapped);
            }

            SocketContext context = EnsureSocket(socket);
            ApplyRemoteAddress(context, address, addressLength);
            context.Connection.State = "Connecting";
            PersistConnection(context, "Connecting", true);
            IntPtr redirectAddress;
            int redirectAddressLength;
            bool redirected = TryCreateHttpRedirect(context, address, addressLength, out redirectAddress, out redirectAddressLength);
            bool result;
            try
            {
                result = original(
                    socket,
                    redirected ? redirectAddress : address,
                    redirected ? redirectAddressLength : addressLength,
                    sendBuffer,
                    sendDataLength,
                    bytesSent,
                    overlapped);
            }
            finally
            {
                if (redirectAddress != IntPtr.Zero) Marshal.FreeHGlobal(redirectAddress);
            }
            int error = result ? 0 : NativeWinsock.WSAGetLastError();
            CompleteConnect(context, result ? 0 : SocketError, error);
            RecordLifecycleChunk(context, TransportOperation.Connect, result ? 0 : SocketError, error);
            return result;
        }

        private int OnSend(IntPtr socket, IntPtr buffer, int length, int flags)
        {
            if (bypassRules)
            {
                return NativeWinsock.send(socket, buffer, length, flags);
            }
            byte[] original = CopyFromNative(buffer, length);
            return ProcessSend(EnsureSocket(socket), original, flags, false, IntPtr.Zero, 0);
        }

        private int OnSendTo(IntPtr socket, IntPtr buffer, int length, int flags, IntPtr to, int toLength)
        {
            if (bypassRules)
            {
                return NativeWinsock.sendto(socket, buffer, length, flags, to, toLength);
            }
            SocketContext context = EnsureSocket(socket);
            ApplyRemoteAddress(context, to, toLength);
            byte[] original = CopyFromNative(buffer, length);
            return ProcessSend(context, original, flags, true, to, toLength);
        }

        private int ProcessSend(SocketContext context, byte[] original, int flags, bool useSendTo, IntPtr to, int toLength)
        {
            MarkConnected(context);
            ConnectionKind previousKind = context.Connection.Kind;
            Classify(context, original, TrafficDirection.ClientToServer);
            if (previousKind != context.Connection.Kind)
            {
                PersistConnection(context, "Classified", true);
            }
            RuleDecision decision = service.EvaluatePayload(context.Connection.Clone(), TrafficDirection.ClientToServer, original);
            byte[] effective = decision.Bytes ?? original;
            int result;
            int error = 0;

            switch (decision.Action)
            {
                case RuleAction.Drop:
                    result = original.Length;
                    effective = new byte[0];
                    break;
                case RuleAction.Delay:
                    ScheduleSend(context, effective, flags, useSendTo, to, toLength, decision.DelayMs);
                    result = original.Length;
                    break;
                case RuleAction.Duplicate:
                    result = CallSend(context.Connection.SocketHandle, effective, flags, useSendTo, to, toLength);
                    if (result >= 0)
                    {
                        CallSend(context.Connection.SocketHandle, effective, flags, useSendTo, to, toLength);
                        result = original.Length;
                    }
                    break;
                case RuleAction.Inject:
                    result = CallSend(context.Connection.SocketHandle, original, flags, useSendTo, to, toLength);
                    if (result >= 0 && effective.Length > 0)
                    {
                        CallSend(context.Connection.SocketHandle, effective, flags, useSendTo, to, toLength);
                        effective = Concat(original, effective);
                        result = original.Length;
                    }
                    break;
                default:
                    result = CallSend(context.Connection.SocketHandle, effective, flags, useSendTo, to, toLength);
                    if (decision.Action == RuleAction.Replace && result >= 0 && effective.Length != original.Length)
                    {
                        result = original.Length;
                    }
                    break;
            }
            if (result == SocketError)
            {
                error = NativeWinsock.WSAGetLastError();
            }

            TransportChunk chunk = CreateChunk(context, TrafficDirection.ClientToServer,
                useSendTo ? TransportOperation.SendTo : TransportOperation.Send,
                original, effective, result, error, decision.Action, decision.RuleId, decision.Reason);
            service.RecordChunk(chunk);
            PersistConnection(context, "Updated", false);
            if (result == SocketError)
            {
                NativeWinsock.WSASetLastError(error);
            }
            return result;
        }

        private int OnReceive(IntPtr socket, IntPtr buffer, int length, int flags)
        {
            if (bypassRules)
            {
                return NativeWinsock.recv(socket, buffer, length, flags);
            }
            SocketContext context = EnsureSocket(socket);
            int pendingResult = TryDeliverPending(context, buffer, length);
            if (pendingResult != int.MinValue)
            {
                return pendingResult;
            }

            int result = NativeWinsock.recv(socket, buffer, length, flags);
            return ProcessReceiveResult(context, buffer, length, result, false);
        }

        private int OnReceiveFrom(IntPtr socket, IntPtr buffer, int length, int flags, IntPtr from, IntPtr fromLength)
        {
            if (bypassRules)
            {
                return NativeWinsock.recvfrom(socket, buffer, length, flags, from, fromLength);
            }
            SocketContext context = EnsureSocket(socket);
            int pendingResult = TryDeliverPending(context, buffer, length);
            if (pendingResult != int.MinValue)
            {
                return pendingResult;
            }

            int result = NativeWinsock.recvfrom(socket, buffer, length, flags, from, fromLength);
            if (result > 0 && from != IntPtr.Zero && fromLength != IntPtr.Zero)
            {
                ApplyRemoteAddress(context, from, Marshal.ReadInt32(fromLength));
            }
            return ProcessReceiveResult(context, buffer, length, result, true);
        }

        private int ProcessReceiveResult(SocketContext context, IntPtr buffer, int bufferLength, int result, bool receiveFrom)
        {
            int error = result == SocketError ? NativeWinsock.WSAGetLastError() : 0;
            if (result <= 0)
            {
                if (result == 0)
                {
                    context.Connection.State = "ClosedByPeer";
                    PersistConnection(context, "Closed", true);
                }
                else if (error != WsaWouldBlock)
                {
                    context.Connection.LastError = error;
                    PersistConnection(context, "Error", true);
                }
                if (result == SocketError)
                {
                    NativeWinsock.WSASetLastError(error);
                }
                return result;
            }

            MarkConnected(context);
            byte[] original = CopyFromNative(buffer, result);
            ConnectionKind previousKind = context.Connection.Kind;
            Classify(context, original, TrafficDirection.ServerToClient);
            if (previousKind != context.Connection.Kind)
            {
                PersistConnection(context, "Classified", true);
            }
            RuleDecision decision = service.EvaluatePayload(context.Connection.Clone(), TrafficDirection.ServerToClient, original);
            byte[] effective = decision.Bytes ?? original;
            int delivered;

            switch (decision.Action)
            {
                case RuleAction.Drop:
                    delivered = SocketError;
                    effective = new byte[0];
                    error = WsaWouldBlock;
                    break;
                case RuleAction.Delay:
                    EnqueuePending(context, effective, decision.DelayMs, false, decision);
                    delivered = SocketError;
                    effective = new byte[0];
                    error = WsaWouldBlock;
                    break;
                case RuleAction.Duplicate:
                    WriteWithRemainder(context, buffer, bufferLength, effective, false, decision);
                    EnqueuePending(context, effective, 0, false, decision);
                    delivered = Math.Min(bufferLength, effective.Length);
                    break;
                case RuleAction.Inject:
                    EnqueuePending(context, original, 0, false, RuleDecision.Pass(original));
                    WriteWithRemainder(context, buffer, bufferLength, effective, false, decision);
                    delivered = Math.Min(bufferLength, effective.Length);
                    effective = Concat(effective, original);
                    break;
                default:
                    WriteWithRemainder(context, buffer, bufferLength, effective, false, decision);
                    delivered = Math.Min(bufferLength, effective.Length);
                    break;
            }

            TransportChunk chunk = CreateChunk(context, TrafficDirection.ServerToClient,
                receiveFrom ? TransportOperation.ReceiveFrom : TransportOperation.Receive,
                original, effective, delivered, error, decision.Action, decision.RuleId, decision.Reason);
            service.RecordChunk(chunk);
            PersistConnection(context, "Updated", false);
            if (delivered == SocketError)
            {
                NativeWinsock.WSASetLastError(error);
            }
            return delivered;
        }

        private int OnCloseSocket(IntPtr socket)
        {
            if (bypassRules)
            {
                return NativeWinsock.closesocket(socket);
            }
            SocketContext context = FindSocket(socket);
            int result = NativeWinsock.closesocket(socket);
            int error = result == SocketError ? NativeWinsock.WSAGetLastError() : 0;
            if (context != null)
            {
                context.Connection.ClosedUtc = DateTime.UtcNow;
                context.Connection.State = "Closed";
                context.Connection.LastError = error;
                RecordLifecycleChunk(context, TransportOperation.Close, result, error);
                PersistConnection(context, "Closed", true);
                lock (syncRoot)
                {
                    sockets.Remove(socket);
                    socketsById.Remove(context.Connection.Id);
                    connectExOriginals.Remove(socket);
                }
            }
            else
            {
                lock (syncRoot) connectExOriginals.Remove(socket);
            }
            if (result == SocketError)
            {
                NativeWinsock.WSASetLastError(error);
            }
            return result;
        }

        private int OnGetPeerName(IntPtr socket, IntPtr name, ref int nameLength)
        {
            if (bypassRules)
            {
                return NativeWinsock.getpeername(socket, name, ref nameLength);
            }
            int result = NativeWinsock.getpeername(socket, name, ref nameLength);
            if (result == 0)
            {
                SocketContext context = EnsureSocket(socket);
                ApplyRemoteAddress(context, name, nameLength);
                RestoreTransparentHttpTarget(context);
                PersistConnection(context, "Updated", false);
            }
            return result;
        }

        private int OnGetSockName(IntPtr socket, IntPtr name, ref int nameLength)
        {
            if (bypassRules)
            {
                return NativeWinsock.getsockname(socket, name, ref nameLength);
            }
            int result = NativeWinsock.getsockname(socket, name, ref nameLength);
            if (result == 0)
            {
                SocketContext context = EnsureSocket(socket);
                int ignoredPort;
                context.Connection.LocalEndPoint = ParseEndPoint(name, nameLength, out ignoredPort);
                PersistConnection(context, "Updated", false);
            }
            return result;
        }

        private int OnWsaEventSelect(IntPtr socket, IntPtr eventHandle, int networkEvents)
        {
            if (bypassRules)
            {
                return NativeWinsock.WSAEventSelect(socket, eventHandle, networkEvents);
            }
            SocketContext context = EnsureSocket(socket);
            lock (context.SyncRoot)
            {
                context.EventHandle = eventHandle;
            }
            int result = NativeWinsock.WSAEventSelect(socket, eventHandle, networkEvents);
            RecordLifecycleChunk(context, TransportOperation.EventSelect, result,
                result == SocketError ? NativeWinsock.WSAGetLastError() : 0);
            return result;
        }

        private void AddHook(string functionName, Delegate callback)
        {
            try
            {
                IntPtr address = LocalHook.GetProcAddress("ws2_32.dll", functionName);
                LocalHook hook = LocalHook.Create(address, callback, this);
                hook.ThreadACL.SetExclusiveACL(new int[0]);
                hooks.Add(hook);
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot install Winsock hook for " + functionName, ex);
                service.ReportEngineEvent("Hook", "ERROR", "Cannot hook " + functionName + ": " + ex.Message, null);
            }
        }

        private SocketContext EnsureSocket(IntPtr socket)
        {
            lock (syncRoot)
            {
                SocketContext context;
                if (sockets.TryGetValue(socket, out context))
                {
                    return context;
                }
                context = new SocketContext
                {
                    Connection = new ConnectionSession
                    {
                        Id = Interlocked.Increment(ref nextConnectionId),
                        SessionId = 0,
                        SocketHandle = socket,
                        OpenedUtc = DateTime.UtcNow,
                        Kind = ConnectionKind.Unknown,
                        State = "Created"
                    },
                    LastPersistedUtc = DateTime.MinValue
                };
                sockets.Add(socket, context);
                socketsById.Add(context.Connection.Id, context);
                return context;
            }
        }

        private SocketContext FindSocket(IntPtr socket)
        {
            lock (syncRoot)
            {
                SocketContext context;
                return sockets.TryGetValue(socket, out context) ? context : null;
            }
        }

        private void CompleteConnect(SocketContext context, int result, int error)
        {
            context.Connection.LastError = error;
            if (result == 0)
            {
                context.Connection.State = "Connected";
                UpdateLocalAndRemoteNames(context);
                PersistConnection(context, "Connected", true);
            }
            else if (error == WsaWouldBlock || error == WsaInProgress || error == WsaIoPending)
            {
                context.Connection.State = "Connecting";
                PersistConnection(context, "Connecting", true);
            }
            else
            {
                context.Connection.State = "Error";
                PersistConnection(context, "Error", true);
            }
            if (result == SocketError)
            {
                NativeWinsock.WSASetLastError(error);
            }
        }

        private void MarkConnected(SocketContext context)
        {
            if (context.Connection.State == "Connected")
            {
                return;
            }
            context.Connection.State = "Connected";
            context.Connection.LastError = 0;
            UpdateLocalAndRemoteNames(context);
            PersistConnection(context, "Connected", true);
        }

        private void UpdateLocalAndRemoteNames(SocketContext context)
        {
            IntPtr memory = Marshal.AllocHGlobal(128);
            try
            {
                int length = 128;
                if (NativeWinsock.getsockname(context.Connection.SocketHandle, memory, ref length) == 0)
                {
                    int ignoredPort;
                    context.Connection.LocalEndPoint = ParseEndPoint(memory, length, out ignoredPort);
                }
                length = 128;
                if (NativeWinsock.getpeername(context.Connection.SocketHandle, memory, ref length) == 0)
                {
                    int port;
                    context.Connection.RemoteEndPoint = ParseEndPoint(memory, length, out port);
                    context.Connection.RemotePort = port;
                    ClassifyByPort(context);
                }
                RestoreTransparentHttpTarget(context);
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }

        private void ApplyRemoteAddress(SocketContext context, IntPtr address, int addressLength)
        {
            int port;
            string endpoint = ParseEndPoint(address, addressLength, out port);
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                context.Connection.RemoteEndPoint = endpoint;
                context.Connection.RemotePort = port;
                ClassifyByPort(context);
            }
        }

        private bool TryCreateHttpRedirect(SocketContext context, IntPtr address, int addressLength, out IntPtr redirectAddress, out int redirectAddressLength)
        {
            redirectAddress = IntPtr.Zero;
            redirectAddressLength = 0;
            if (httpRedirectPort <= 0 || address == IntPtr.Zero || addressLength < 8)
            {
                return false;
            }

            int port;
            string endpoint = ParseEndPoint(address, addressLength, out port);
            int family = (ushort)Marshal.ReadInt16(address, 0);
            bool directHttp = port == 80 && !IsLoopbackEndpoint(endpoint);
            bool configuredHttpProxy = IsLoopbackEndpoint(endpoint) && profile.IsProxyPort(port) && port != httpRedirectPort;
            if (family != 2 || (!directHttp && !configuredHttpProxy))
            {
                return false;
            }

            context.TransparentRemoteEndPoint = endpoint;
            context.TransparentRemotePort = port;
            context.Connection.Kind = ConnectionKind.HttpProxy;
            Logger.Info("HTTP redirect: " + endpoint + " -> 127.0.0.1:" + httpRedirectPort +
                (configuredHttpProxy ? " (configured proxy)" : " (direct port 80)"));

            redirectAddressLength = 16;
            redirectAddress = Marshal.AllocHGlobal(redirectAddressLength);
            for (int i = 0; i < redirectAddressLength; i++) Marshal.WriteByte(redirectAddress, i, 0);
            Marshal.WriteInt16(redirectAddress, 0, 2);
            Marshal.WriteByte(redirectAddress, 2, (byte)((httpRedirectPort >> 8) & 0xff));
            Marshal.WriteByte(redirectAddress, 3, (byte)(httpRedirectPort & 0xff));
            byte[] loopback = IPAddress.Loopback.GetAddressBytes();
            Marshal.Copy(loopback, 0, IntPtr.Add(redirectAddress, 4), loopback.Length);
            return true;
        }

        private static void RestoreTransparentHttpTarget(SocketContext context)
        {
            if (string.IsNullOrWhiteSpace(context.TransparentRemoteEndPoint))
            {
                return;
            }
            context.Connection.RemoteEndPoint = context.TransparentRemoteEndPoint;
            context.Connection.RemotePort = context.TransparentRemotePort;
            context.Connection.Kind = ConnectionKind.HttpProxy;
        }

        private void ClassifyByPort(SocketContext context)
        {
            if (profile.IsPolicyPort(context.Connection.RemotePort))
            {
                context.Connection.Kind = ConnectionKind.FlashPolicy;
            }
            else if (profile.IsProxyPort(context.Connection.RemotePort) && IsLoopbackEndpoint(context.Connection.RemoteEndPoint))
            {
                context.Connection.Kind = ConnectionKind.HttpProxy;
            }
        }

        private void Classify(SocketContext context, byte[] bytes, TrafficDirection direction)
        {
            if (context.Connection.Kind == ConnectionKind.FlashPolicy || context.Connection.Kind == ConnectionKind.HttpProxy)
            {
                return;
            }
            string prefix = GetAsciiPrefix(bytes, 32);
            if (prefix.IndexOf("policy-file-request", StringComparison.OrdinalIgnoreCase) >= 0 ||
                prefix.IndexOf("cross-domain-policy", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                context.Connection.Kind = ConnectionKind.FlashPolicy;
            }
            else if (prefix.StartsWith("GET ", StringComparison.Ordinal) ||
                     prefix.StartsWith("POST ", StringComparison.Ordinal) ||
                     prefix.StartsWith("CONNECT ", StringComparison.Ordinal) ||
                     prefix.StartsWith("HTTP/", StringComparison.Ordinal))
            {
                context.Connection.Kind = ConnectionKind.HttpProxy;
            }
            else if (!string.IsNullOrEmpty(context.Connection.RemoteEndPoint))
            {
                context.Connection.Kind = ConnectionKind.Game;
            }
        }

        private TransportChunk CreateChunk(
            SocketContext context,
            TrafficDirection direction,
            TransportOperation operation,
            byte[] original,
            byte[] effective,
            int nativeResult,
            int nativeError,
            RuleAction action,
            string ruleId,
            string note)
        {
            long offset;
            lock (context.SyncRoot)
            {
                if (direction == TrafficDirection.ClientToServer)
                {
                    offset = context.Connection.ClientStreamOffset;
                    context.Connection.ClientStreamOffset += effective == null ? 0 : effective.Length;
                }
                else
                {
                    offset = context.Connection.ServerStreamOffset;
                    context.Connection.ServerStreamOffset += effective == null ? 0 : effective.Length;
                }
            }
            return new TransportChunk
            {
                ConnectionId = context.Connection.Id,
                TimestampUtc = DateTime.UtcNow,
                Direction = direction,
                Operation = operation,
                StreamOffset = offset,
                ThreadId = RemoteHooking.GetCurrentThreadId(),
                NativeResult = nativeResult,
                NativeError = nativeError,
                OriginalBytes = HexCodec.Copy(original),
                EffectiveBytes = HexCodec.Copy(effective),
                RuleAction = action,
                RuleId = ruleId,
                Note = note
            };
        }

        private void RecordLifecycleChunk(SocketContext context, TransportOperation operation, int result, int error)
        {
            service.RecordChunk(new TransportChunk
            {
                ConnectionId = context.Connection.Id,
                TimestampUtc = DateTime.UtcNow,
                Direction = TrafficDirection.ClientToServer,
                Operation = operation,
                ThreadId = RemoteHooking.GetCurrentThreadId(),
                NativeResult = result,
                NativeError = error,
                OriginalBytes = new byte[0],
                EffectiveBytes = new byte[0],
                RuleAction = RuleAction.Pass
            });
        }

        private void PersistConnection(SocketContext context, string eventName, bool force)
        {
            DateTime now = DateTime.UtcNow;
            lock (context.SyncRoot)
            {
                if (!force && (now - context.LastPersistedUtc).TotalMilliseconds < 250)
                {
                    return;
                }
                context.LastPersistedUtc = now;
            }
            service.RecordConnection(context.Connection.Clone(), eventName);
        }

        private void ScheduleSend(SocketContext context, byte[] bytes, int flags, bool sendTo, IntPtr to, int toLength, int delayMs)
        {
            byte[] copy = HexCodec.Copy(bytes);
            byte[] addressCopy = null;
            if (sendTo && to != IntPtr.Zero && toLength > 0)
            {
                addressCopy = new byte[toLength];
                Marshal.Copy(to, addressCopy, 0, toLength);
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(Math.Max(0, delayMs));
                if (!service.ActiveMode)
                {
                    return;
                }
                IntPtr address = IntPtr.Zero;
                try
                {
                    if (addressCopy != null)
                    {
                        address = Marshal.AllocHGlobal(addressCopy.Length);
                        Marshal.Copy(addressCopy, 0, address, addressCopy.Length);
                    }
                    CallSend(context.Connection.SocketHandle, copy, flags, sendTo, address, addressCopy == null ? 0 : addressCopy.Length);
                }
                finally
                {
                    if (address != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(address);
                    }
                }
            });
        }

        private int CallSend(IntPtr socket, byte[] bytes, int flags)
        {
            return CallSend(socket, bytes, flags, false, IntPtr.Zero, 0);
        }

        private int CallSend(IntPtr socket, byte[] bytes, int flags, bool sendTo, IntPtr to, int toLength)
        {
            IntPtr memory = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
            bool previousBypass = bypassRules;
            try
            {
                if (bytes.Length > 0)
                {
                    Marshal.Copy(bytes, 0, memory, bytes.Length);
                }
                bypassRules = true;
                return sendTo
                    ? NativeWinsock.sendto(socket, memory, bytes.Length, flags, to, toLength)
                    : NativeWinsock.send(socket, memory, bytes.Length, flags);
            }
            finally
            {
                bypassRules = previousBypass;
                Marshal.FreeHGlobal(memory);
            }
        }

        private int TryDeliverPending(SocketContext context, IntPtr buffer, int bufferLength)
        {
            PendingReceive pending;
            lock (context.SyncRoot)
            {
                if (context.PendingReceive.Count == 0)
                {
                    return int.MinValue;
                }
                pending = context.PendingReceive.Peek();
                if (pending.DueUtc > DateTime.UtcNow)
                {
                    NativeWinsock.WSASetLastError(WsaWouldBlock);
                    return SocketError;
                }
                context.PendingReceive.Dequeue();
            }

            byte[] pendingBytes = pending.Bytes;
            int delivered = Math.Min(bufferLength, pendingBytes.Length);
            if (delivered > 0)
            {
                Marshal.Copy(pendingBytes, 0, buffer, delivered);
            }
            if (delivered < pendingBytes.Length)
            {
                byte[] remaining = new byte[pendingBytes.Length - delivered];
                Buffer.BlockCopy(pendingBytes, delivered, remaining, 0, remaining.Length);
                pending.Bytes = remaining;
                lock (context.SyncRoot)
                {
                    PrependPending(context.PendingReceive, pending);
                }
            }
            if (pending.RecordOnDelivery)
            {
                byte[] deliveredBytes = new byte[delivered];
                Buffer.BlockCopy(pendingBytes, 0, deliveredBytes, 0, delivered);
                service.RecordChunk(CreateChunk(context, TrafficDirection.ServerToClient, TransportOperation.Inject,
                    new byte[0], deliveredBytes, delivered, 0, pending.Action, pending.RuleId, pending.Note));
            }
            return delivered;
        }

        private void WriteWithRemainder(SocketContext context, IntPtr buffer, int bufferLength, byte[] bytes, bool recordRemainder, RuleDecision decision)
        {
            int delivered = Math.Min(bufferLength, bytes.Length);
            if (delivered > 0)
            {
                Marshal.Copy(bytes, 0, buffer, delivered);
            }
            if (delivered < bytes.Length)
            {
                byte[] remaining = new byte[bytes.Length - delivered];
                Buffer.BlockCopy(bytes, delivered, remaining, 0, remaining.Length);
                EnqueuePending(context, remaining, 0, recordRemainder, decision);
            }
        }

        private void EnqueuePending(SocketContext context, byte[] bytes, int delayMs, bool recordOnDelivery, RuleDecision decision)
        {
            lock (context.SyncRoot)
            {
                context.PendingReceive.Enqueue(new PendingReceive
                {
                    Bytes = HexCodec.Copy(bytes),
                    DueUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(0, delayMs)),
                    RecordOnDelivery = recordOnDelivery,
                    Action = decision.Action,
                    RuleId = decision.RuleId,
                    Note = decision.Reason
                });
                if (context.EventHandle != IntPtr.Zero)
                {
                    NativeWinsock.WSASetEvent(context.EventHandle);
                }
            }
        }

        private static void PrependPending(Queue<PendingReceive> queue, PendingReceive item)
        {
            PendingReceive[] existing = queue.ToArray();
            queue.Clear();
            queue.Enqueue(item);
            for (int i = 0; i < existing.Length; i++)
            {
                queue.Enqueue(existing[i]);
            }
        }

        private static byte[] CopyFromNative(IntPtr buffer, int length)
        {
            if (buffer == IntPtr.Zero || length <= 0)
            {
                return new byte[0];
            }
            byte[] bytes = new byte[length];
            Marshal.Copy(buffer, bytes, 0, length);
            return bytes;
        }

        private static string ParseEndPoint(IntPtr address, int addressLength, out int port)
        {
            port = 0;
            if (address == IntPtr.Zero || addressLength < 4)
            {
                return string.Empty;
            }
            int family = (ushort)Marshal.ReadInt16(address, 0);
            port = (Marshal.ReadByte(address, 2) << 8) | Marshal.ReadByte(address, 3);
            if (family == 2 && addressLength >= 8)
            {
                byte[] bytes = new byte[4];
                Marshal.Copy(IntPtr.Add(address, 4), bytes, 0, 4);
                return new IPAddress(bytes) + ":" + port;
            }
            if (family == 23 && addressLength >= 24)
            {
                byte[] bytes = new byte[16];
                Marshal.Copy(IntPtr.Add(address, 8), bytes, 0, 16);
                return "[" + new IPAddress(bytes) + "]:" + port;
            }
            return "af" + family + ":" + port;
        }

        private static string GetAsciiPrefix(byte[] bytes, int maximumLength)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }
            return Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, maximumLength));
        }

        private static bool IsLoopbackEndpoint(string endpoint)
        {
            return endpoint != null &&
                (endpoint.StartsWith("127.", StringComparison.Ordinal) || endpoint.StartsWith("[::1]", StringComparison.Ordinal));
        }

        private static bool IsInvalidSocket(IntPtr socket)
        {
            return socket == new IntPtr(-1);
        }

        private static byte[] Concat(byte[] left, byte[] right)
        {
            left = left ?? new byte[0];
            right = right ?? new byte[0];
            byte[] result = new byte[left.Length + right.Length];
            Buffer.BlockCopy(left, 0, result, 0, left.Length);
            Buffer.BlockCopy(right, 0, result, left.Length, right.Length);
            return result;
        }

        private sealed class SocketContext
        {
            public readonly object SyncRoot = new object();
            public readonly Queue<PendingReceive> PendingReceive = new Queue<PendingReceive>();
            public ConnectionSession Connection;
            public IntPtr EventHandle;
            public DateTime LastPersistedUtc;
            public string TransparentRemoteEndPoint;
            public int TransparentRemotePort;
        }

        private sealed class InterceptionBypassScope : IDisposable
        {
            private readonly bool previous;
            private bool disposed;

            public InterceptionBypassScope()
            {
                previous = bypassRules;
                bypassRules = true;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                bypassRules = previous;
            }
        }

        private sealed class PendingReceive
        {
            public byte[] Bytes;
            public DateTime DueUtc;
            public bool RecordOnDelivery;
            public RuleAction Action;
            public string RuleId;
            public string Note;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate IntPtr SocketDelegate(int addressFamily, int socketType, int protocol);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate IntPtr WsaSocketDelegate(int addressFamily, int socketType, int protocol, IntPtr protocolInfo, uint group, uint flags);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate int ConnectDelegate(IntPtr socket, IntPtr address, int addressLength);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate int WsaConnectDelegate(IntPtr socket, IntPtr address, int addressLength, IntPtr callerData, IntPtr calleeData, IntPtr sqos, IntPtr gqos);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate int SendDelegate(IntPtr socket, IntPtr buffer, int length, int flags);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate int ReceiveDelegate(IntPtr socket, IntPtr buffer, int length, int flags);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate int SendToDelegate(IntPtr socket, IntPtr buffer, int length, int flags, IntPtr to, int toLength);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate int ReceiveFromDelegate(IntPtr socket, IntPtr buffer, int length, int flags, IntPtr from, IntPtr fromLength);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate int CloseSocketDelegate(IntPtr socket);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate int GetNameDelegate(IntPtr socket, IntPtr name, ref int nameLength);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate int WsaEventSelectDelegate(IntPtr socket, IntPtr eventHandle, int networkEvents);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate int WsaIoctlDelegate(
            IntPtr socket, int controlCode, IntPtr inputBuffer, int inputLength,
            IntPtr outputBuffer, int outputLength, IntPtr bytesReturned,
            IntPtr overlapped, IntPtr completionRoutine);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool ConnectExDelegate(
            IntPtr socket, IntPtr address, int addressLength, IntPtr sendBuffer,
            int sendDataLength, IntPtr bytesSent, IntPtr overlapped);

        private static class NativeWinsock
        {
            [DllImport("ws2_32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
            public static extern IntPtr socket(int addressFamily, int socketType, int protocol);

            [DllImport("ws2_32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
            public static extern IntPtr WSASocketW(int addressFamily, int socketType, int protocol, IntPtr protocolInfo, uint group, uint flags);

            [DllImport("ws2_32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
            public static extern int connect(IntPtr socket, IntPtr address, int addressLength);

            [DllImport("ws2_32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
            public static extern int WSAConnect(IntPtr socket, IntPtr address, int addressLength, IntPtr callerData, IntPtr calleeData, IntPtr sqos, IntPtr gqos);

            [DllImport("ws2_32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
            public static extern int send(IntPtr socket, IntPtr buffer, int length, int flags);

            [DllImport("ws2_32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
            public static extern int recv(IntPtr socket, IntPtr buffer, int length, int flags);

            [DllImport("ws2_32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
            public static extern int sendto(IntPtr socket, IntPtr buffer, int length, int flags, IntPtr to, int toLength);

            [DllImport("ws2_32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
            public static extern int recvfrom(IntPtr socket, IntPtr buffer, int length, int flags, IntPtr from, IntPtr fromLength);

            [DllImport("ws2_32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
            public static extern int closesocket(IntPtr socket);

            [DllImport("ws2_32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
            public static extern int getpeername(IntPtr socket, IntPtr name, ref int nameLength);

            [DllImport("ws2_32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
            public static extern int getsockname(IntPtr socket, IntPtr name, ref int nameLength);

            [DllImport("ws2_32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
            public static extern int WSAEventSelect(IntPtr socket, IntPtr eventHandle, int networkEvents);

            [DllImport("ws2_32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
            public static extern int WSAIoctl(
                IntPtr socket, int controlCode, IntPtr inputBuffer, int inputLength,
                IntPtr outputBuffer, int outputLength, IntPtr bytesReturned,
                IntPtr overlapped, IntPtr completionRoutine);

            [DllImport("ws2_32.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern int WSAGetLastError();

            [DllImport("ws2_32.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern void WSASetLastError(int error);

            [DllImport("ws2_32.dll", CallingConvention = CallingConvention.StdCall)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool WSASetEvent(IntPtr eventHandle);
        }
    }
}
