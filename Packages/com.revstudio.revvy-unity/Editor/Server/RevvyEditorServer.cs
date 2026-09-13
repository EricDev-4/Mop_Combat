using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace RevStudio.Revvy.Editor
{
    /// <summary>
    /// Loopback Streamable-HTTP (+SSE) MCP endpoint at <c>http://127.0.0.1:8088/mcp</c>
    /// (contract §1, §3).
    ///
    /// Threading model: one accept thread, one worker thread per connection. No Unity
    /// API is ever touched here — tool dispatch hops to the main thread inside
    /// <see cref="RevvyToolRegistry"/> (contract §8.2).
    ///
    /// Security posture matches the UE MCP server: bound to the loopback interface
    /// only, non-loopback peers are dropped, and a non-loopback <c>Origin</c> header is
    /// rejected (DNS-rebinding guard). There is no auth on this hop — the proxy owns
    /// bearer-token enforcement.
    /// </summary>
    public sealed class RevvyEditorServer : IDisposable
    {
        public const string EndpointPath = "/mcp";

        private const int ConnectionReadTimeoutMs = 120000;
        private const int ConnectionWriteTimeoutMs = 30000;
        private const int SseKeepAliveMs = 15000;
        private const int SseTickMs = 250;
        private const int MaxConcurrentConnections = 32;

        private static readonly object InstanceGate = new object();
        private static RevvyEditorServer _instance;

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private long _nextEventId;
        private int _openConnections;
        private readonly object _connectionsGate = new object();
        private readonly HashSet<TcpClient> _connections = new HashSet<TcpClient>();

        public int Port { get; private set; }

        public string LastError { get; private set; }

        public bool IsRunning
        {
            get { return _running; }
        }

        public long RequestCount;

        /// <summary>The process-wide bridge instance, or null when stopped.</summary>
        public static RevvyEditorServer Instance
        {
            get
            {
                lock (InstanceGate)
                {
                    return _instance;
                }
            }
        }

        /// <summary>Starts (or restarts) the singleton listener. Returns the running instance, or null on failure.</summary>
        public static RevvyEditorServer StartShared(int port)
        {
            lock (InstanceGate)
            {
                if (_instance != null && _instance._running && _instance.Port == port)
                {
                    return _instance;
                }

                StopShared();

                RevvyEditorServer server = new RevvyEditorServer();
                if (!server.Start(port))
                {
                    _instance = server; // keep it so the status window can show LastError
                    return null;
                }

                _instance = server;
                return server;
            }
        }

        public static void StopShared()
        {
            lock (InstanceGate)
            {
                if (_instance != null)
                {
                    _instance.Stop();
                    _instance = null;
                }
            }
        }

        public bool Start(int port)
        {
            if (_running)
            {
                return true;
            }

            Port = port;
            LastError = null;

            try
            {
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
                _listener.Start(16);
            }
            catch (SocketException exception)
            {
                LastError = exception.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? "Port " + port + " is already in use. Another editor bridge (or the Unreal MCP server) " +
                      "is holding the single 8088 slot; contract §1 allows only one active bridge."
                    : "Failed to bind 127.0.0.1:" + port + " — " + RevvyLog.Describe(exception);
                RevvyLog.Error(LastError);
                SafeStopListener();
                return false;
            }
            catch (Exception exception)
            {
                LastError = "Failed to start listener — " + RevvyLog.Describe(exception);
                RevvyLog.Error(LastError);
                SafeStopListener();
                return false;
            }

            _running = true;
            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "Revvy-MCP-Accept"
            };
            _acceptThread.Start();

            RevvyLog.Info("MCP bridge listening on http://127.0.0.1:" + port + EndpointPath);
            return true;
        }

        public void Stop()
        {
            if (!_running)
            {
                SafeStopListener();
                return;
            }

            _running = false;

            // Close accepted sockets too: idle keep-alive reads can otherwise survive
            // shutdown and keep an exclusive Windows port bound during a restart.
            lock (_connectionsGate)
            {
                foreach (TcpClient client in _connections)
                {
                    CloseQuietly(client);
                }
                _connections.Clear();
            }

            // The accept thread is a background thread; a short join keeps shutdown tidy
            // without risking a stall on domain reload.
            if (_acceptThread != null && _acceptThread.IsAlive)
            {
                _acceptThread.Join(1000);
            }

            SafeStopListener();
            _acceptThread = null;
            RevvyMcpHandler.ClearSessions();
            RevvyLog.Info("MCP bridge stopped.");
        }

        public void Dispose()
        {
            Stop();
        }

        private void SafeStopListener()
        {
            try
            {
                if (_listener != null)
                {
                    _listener.Stop();
                }
            }
            catch (Exception)
            {
                // Stop() during shutdown races is expected; nothing actionable.
            }
            finally
            {
                _listener = null;
            }
        }

        // ------------------------------------------------------------------ accept

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    TcpListener listener = _listener;
                    if (listener == null)
                    {
                        break;
                    }

                    // Never block in native accept across a Unity domain reload.
                    // Poll bounds shutdown latency and lets Stop join before closing
                    // the listener, avoiding an orphaned accept/socket on Windows.
                    if (!listener.Server.Poll(100000, SelectMode.SelectRead))
                    {
                        continue;
                    }
                    if (!_running)
                    {
                        break;
                    }
                    client = listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    // Listener stopped underneath us (normal shutdown path).
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }

                if (client == null)
                {
                    continue;
                }

                if (!IsLoopbackPeer(client))
                {
                    RevvyLog.Warn("Rejected non-loopback connection.");
                    CloseQuietly(client);
                    continue;
                }

                if (Interlocked.Increment(ref _openConnections) > MaxConcurrentConnections)
                {
                    Interlocked.Decrement(ref _openConnections);
                    RevvyLog.Warn("Connection limit (" + MaxConcurrentConnections + ") reached; dropping client.");
                    CloseQuietly(client);
                    continue;
                }

                TcpClient captured = client;
                lock (_connectionsGate)
                {
                    if (!_running)
                    {
                        Interlocked.Decrement(ref _openConnections);
                        CloseQuietly(captured);
                        break;
                    }
                    _connections.Add(captured);
                }
                Thread worker = new Thread(() =>
                {
                    try
                    {
                        ServeConnection(captured);
                    }
                    finally
                    {
                        lock (_connectionsGate)
                        {
                            _connections.Remove(captured);
                        }
                        Interlocked.Decrement(ref _openConnections);
                    }
                })
                {
                    IsBackground = true,
                    Name = "Revvy-MCP-Conn"
                };
                worker.Start();
            }
        }

        private static bool IsLoopbackPeer(TcpClient client)
        {
            try
            {
                IPEndPoint endpoint = client.Client.RemoteEndPoint as IPEndPoint;
                return endpoint != null && IPAddress.IsLoopback(endpoint.Address);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void CloseQuietly(TcpClient client)
        {
            try
            {
                client.Close();
            }
            catch (Exception)
            {
                // Already gone.
            }
        }

        // ------------------------------------------------------------------ serve

        private void ServeConnection(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                using (NetworkStream stream = client.GetStream())
                {
                    stream.ReadTimeout = ConnectionReadTimeoutMs;
                    stream.WriteTimeout = ConnectionWriteTimeoutMs;

                    while (_running)
                    {
                        RevvyHttpRequest request;
                        try
                        {
                            request = RevvyHttp.ReadRequest(stream);
                        }
                        catch (IOException exception)
                        {
                            RevvyLog.Verbose("Connection read ended: " + RevvyLog.Describe(exception));
                            return;
                        }

                        if (request == null)
                        {
                            return; // client disconnected cleanly
                        }

                        Interlocked.Increment(ref RequestCount);

                        bool keepAlive = HandleRequest(stream, request);
                        if (!keepAlive || !request.WantsKeepAlive)
                        {
                            return;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                RevvyLog.Verbose("Connection aborted: " + RevvyLog.Describe(exception));
            }
            finally
            {
                CloseQuietly(client);
            }
        }

        /// <summary>Returns true when the connection may be reused for another request.</summary>
        private bool HandleRequest(Stream stream, RevvyHttpRequest request)
        {
            Dictionary<string, string> headers = BuildCorsHeaders(request);

            if (!IsOriginAcceptable(request))
            {
                RevvyHttp.WriteResponse(stream, 403, "Forbidden", "text/plain",
                    "Origin not permitted for a loopback bridge", headers);
                return false;
            }

            string path = StripQuery(request.Path);

            if (string.Equals(request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
                headers["Access-Control-Max-Age"] = "86400";
                RevvyHttp.WriteResponse(stream, 204, "No Content", null, null, headers, true);
                return true;
            }

            // Tiny convenience endpoint: lets a human (or a shell probe) confirm the
            // bridge is alive without speaking JSON-RPC.
            if (string.Equals(path, "/health", StringComparison.Ordinal) &&
                string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                RevvyJson health = RevvyJson.Object()
                    .Set("service", "revvy-unity-bridge")
                    .Set("engine", RevvyEnv.EngineId)
                    .Set("engine_version", RevvyEditorFacts.CachedUnityVersion)
                    .Set("project_path", RevvyEditorFacts.CachedProjectPath)
                    .Set("bridge_instance_id", RevvyEditorFacts.BridgeInstanceId)
                    .Set("editor_port", Port)
                    .Set("port", Port)
                    .Set("tools", RevvyToolRegistry.Count);
                RevvyHttp.WriteResponse(stream, 200, "OK", "application/json", health.ToJson(false), headers, true);
                return true;
            }

            if (!string.Equals(path, EndpointPath, StringComparison.Ordinal))
            {
                RevvyHttp.WriteResponse(stream, 404, "Not Found", "text/plain", "Not found", headers);
                return false;
            }

            if (string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                return HandlePost(stream, request, headers);
            }

            if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                if (!request.AcceptsEventStream)
                {
                    RevvyHttp.WriteResponse(stream, 406, "Not Acceptable", "text/plain",
                        "GET /mcp requires Accept: text/event-stream", headers);
                    return false;
                }

                ServeEventStream(stream, request, headers);
                return false;
            }

            if (string.Equals(request.Method, "DELETE", StringComparison.OrdinalIgnoreCase))
            {
                RevvyMcpHandler.ForgetSession(request.Header("Mcp-Session-Id"));
                RevvyHttp.WriteResponse(stream, 200, "OK", "text/plain", "Session terminated", headers);
                return false;
            }

            headers["Allow"] = "GET, POST, DELETE, OPTIONS";
            RevvyHttp.WriteResponse(stream, 405, "Method Not Allowed", "text/plain", "Method not allowed", headers);
            return false;
        }

        private bool HandlePost(Stream stream, RevvyHttpRequest request, Dictionary<string, string> headers)
        {
            RevvyBridgeAuth.SignedRequest signedRequest;
            string authError;
            if (!RevvyBridgeAuth.TryAuthorize(
                    request,
                    RevvyEditorFacts.BridgeInstanceId,
                    Port,
                    out signedRequest,
                    out authError))
            {
                RevvyLog.Warn("Rejected unauthenticated editor bridge POST: " + authError);
                RevvyHttp.WriteResponse(stream, 401, "Unauthorized", "text/plain",
                    "Unauthorized editor bridge request", headers);
                return false;
            }

            RevvyRpcOutcome outcome = RevvyMcpHandler.Handle(
                request.Body,
                signedRequest != null ? signedRequest.Scope : "read_only");

            if (!string.IsNullOrEmpty(outcome.SessionId))
            {
                headers["Mcp-Session-Id"] = outcome.SessionId;
            }

            if (outcome.IsNotification)
            {
                AddResponseSignature(headers, signedRequest, 202, string.Empty);
                RevvyHttp.WriteResponse(stream, 202, "Accepted", null, null, headers, true);
                return true;
            }

            // Streamable HTTP: when the client accepts SSE the response is delivered as a
            // single event on a short-lived stream, matching the UE server's behaviour.
            if (request.AcceptsEventStream && signedRequest == null)
            {
                RevvyHttp.WriteEventStreamHead(stream, headers);
                RevvyHttp.WriteEvent(stream, outcome.Response, Interlocked.Increment(ref _nextEventId));
                return false;
            }

            AddResponseSignature(headers, signedRequest, 200, outcome.Response);
            RevvyHttp.WriteResponse(stream, 200, "OK", "application/json", outcome.Response, headers, true);
            return true;
        }

        private static void AddResponseSignature(
            Dictionary<string, string> headers,
            RevvyBridgeAuth.SignedRequest signedRequest,
            int httpStatus,
            string responseBody)
        {
            if (signedRequest == null)
            {
                return;
            }

            headers[RevvyBridgeAuth.ResponseSignatureHeader] = RevvyBridgeAuth.SignResponse(
                signedRequest,
                httpStatus,
                RevvyBridgeAuth.EncodeBody(responseBody));
        }

        /// <summary>
        /// Server-to-client SSE channel (GET /mcp). The bridge currently pushes no
        /// unsolicited messages, so the stream only carries keep-alives and stays open
        /// until the client disconnects or the bridge stops.
        /// TODO(tier-2): emit progress notifications for long-running tool calls here.
        /// </summary>
        private void ServeEventStream(Stream stream, RevvyHttpRequest request, Dictionary<string, string> headers)
        {
            string sessionId = request.Header("Mcp-Session-Id");
            if (!string.IsNullOrEmpty(sessionId))
            {
                headers["Mcp-Session-Id"] = sessionId;
            }

            try
            {
                RevvyHttp.WriteEventStreamHead(stream, headers);
                while (_running)
                {
                    // Sleep in short slices rather than one 15s block: Stop() must be
                    // able to release this thread (and its socket) promptly, or an
                    // orphaned SSE connection outlives the domain it was created in.
                    for (int waited = 0; waited < SseKeepAliveMs && _running; waited += SseTickMs)
                    {
                        Thread.Sleep(SseTickMs);
                    }

                    if (!_running)
                    {
                        break;
                    }

                    RevvyHttp.WriteKeepAlive(stream);
                }
            }
            catch (Exception exception)
            {
                RevvyLog.Verbose("SSE stream closed: " + RevvyLog.Describe(exception));
            }
        }

        // ------------------------------------------------------------------ helpers

        private static string StripQuery(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "/";
            }

            int mark = path.IndexOf('?');
            return mark >= 0 ? path.Substring(0, mark) : path;
        }

        /// <summary>
        /// DNS-rebinding guard: a browser page on some remote origin must not be able to
        /// drive the editor even though the socket itself is loopback.
        /// </summary>
        private static bool IsOriginAcceptable(RevvyHttpRequest request)
        {
            string origin = request.Header("Origin");
            if (string.IsNullOrEmpty(origin))
            {
                return true; // non-browser client (the proxy)
            }

            Uri parsed;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out parsed))
            {
                return false;
            }

            string host = parsed.Host;
            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
                || string.Equals(host, "::1", StringComparison.Ordinal)
                || string.Equals(host, "[::1]", StringComparison.Ordinal);
        }

        private static Dictionary<string, string> BuildCorsHeaders(RevvyHttpRequest request)
        {
            Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string origin = request.Header("Origin");
            if (!string.IsNullOrEmpty(origin) && IsOriginAcceptable(request))
            {
                // Echo the validated origin; never "*" (matches the UE server).
                headers["Access-Control-Allow-Origin"] = origin;
                headers["Vary"] = "Origin";
            }

            headers["Access-Control-Allow-Headers"] =
                "Content-Type, Authorization, Mcp-Session-Id, MCP-Protocol-Version, Accept, Last-Event-ID, " +
                "X-Revvy-Bridge-Instance, X-Revvy-Access-Scope, X-Revvy-Request-Timestamp, " +
                "X-Revvy-Request-Nonce, X-Revvy-Request-Signature";
            headers["Access-Control-Expose-Headers"] =
                "Mcp-Session-Id, X-Revvy-Response-Signature";
            headers["Server"] = "revvy-unity/" + RevvyMcpHandler.ServerVersion;
            return headers;
        }

        public string DescribeState()
        {
            if (_running)
            {
                return "listening on 127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture) + EndpointPath;
            }

            return string.IsNullOrEmpty(LastError) ? "stopped" : "stopped — " + LastError;
        }
    }
}
