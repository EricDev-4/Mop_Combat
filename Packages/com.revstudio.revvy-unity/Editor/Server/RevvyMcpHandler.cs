using System;
using System.Collections.Generic;
using System.Threading;

namespace RevStudio.Revvy.Editor
{
    /// <summary>Outcome of handling one JSON-RPC message.</summary>
    public sealed class RevvyRpcOutcome
    {
        /// <summary>Serialized JSON-RPC response, or empty for a notification.</summary>
        public string Response = string.Empty;

        /// <summary>True when the client sent a notification (no id) — answer 202 with no body.</summary>
        public bool IsNotification;

        /// <summary>Session id minted by <c>initialize</c>; echoed back in <c>Mcp-Session-Id</c>.</summary>
        public string SessionId;
    }

    /// <summary>
    /// JSON-RPC 2.0 / MCP method dispatch (contract §3).
    ///
    /// Implements the required minimum set — <c>initialize</c>, <c>ping</c>,
    /// <c>tools/list</c>, <c>tools/call</c> — plus notification acknowledgement.
    /// Runs on a socket worker thread; anything touching Unity goes through
    /// <see cref="RevvyToolRegistry"/>, which marshals to the main thread.
    /// </summary>
    public static class RevvyMcpHandler
    {
        /// <summary>Newest first. Contract §3 pins 2025-11-25 and tolerates the two older revisions.</summary>
        public static readonly string[] SupportedProtocolVersions =
        {
            "2025-11-25",
            "2025-06-18",
            "2024-11-05"
        };

        public const string ServerName = "revvy-unity";
        public const string ServerVersion = "0.2.0";

        private static readonly object SessionGate = new object();
        private static readonly HashSet<string> Sessions = new HashSet<string>(StringComparer.Ordinal);

        public static int SessionCount
        {
            get
            {
                lock (SessionGate)
                {
                    return Sessions.Count;
                }
            }
        }

        public static void ForgetSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            lock (SessionGate)
            {
                Sessions.Remove(sessionId);
            }
        }

        public static void ClearSessions()
        {
            lock (SessionGate)
            {
                Sessions.Clear();
            }
        }

        /// <summary>Parses and dispatches one JSON-RPC request document.</summary>
        public static RevvyRpcOutcome Handle(string requestBody, string accessScope = "full")
        {
            RevvyRpcOutcome outcome = new RevvyRpcOutcome();
            bool readOnly = string.Equals(accessScope, "read_only", StringComparison.Ordinal);

            string parseError;
            RevvyJson request = RevvyJson.TryParse(requestBody, out parseError);
            if (request == null)
            {
                outcome.Response = BuildError(null, -32700, "Parse error: " + parseError);
                return outcome;
            }

            // Batches are legal JSON-RPC but the Revvy proxy never sends them; reject
            // explicitly rather than silently answering only the first element.
            if (request.IsArray)
            {
                outcome.Response = BuildError(null, -32600, "Batch requests are not supported by the Unity bridge");
                return outcome;
            }

            if (!request.IsObject)
            {
                outcome.Response = BuildError(null, -32600, "Invalid Request: expected a JSON object");
                return outcome;
            }

            RevvyJson id = request.Get("id");
            string method = request.Get("method") != null ? request.Get("method").AsString(string.Empty) : null;
            RevvyJson parameters = request.Get("params");

            if (string.IsNullOrEmpty(method))
            {
                outcome.Response = BuildError(id, -32600, "Invalid Request: missing 'method'");
                return outcome;
            }

            // No id => notification. Acknowledge with 202 and no body.
            if (id == null || id.IsNull)
            {
                outcome.IsNotification = true;
                RevvyLog.Verbose("Notification: " + method);
                return outcome;
            }

            try
            {
                switch (method)
                {
                    case "initialize":
                        outcome.SessionId = Guid.NewGuid().ToString("N");
                        lock (SessionGate)
                        {
                            Sessions.Add(outcome.SessionId);
                        }

                        outcome.Response = BuildResult(id, HandleInitialize(parameters));
                        break;

                    case "ping":
                        // Proxy liveness probe: must answer in well under a second, so it
                        // deliberately does NOT touch the (possibly stalled) main thread.
                        RevvyEditorServer activeServer = RevvyEditorServer.Instance;
                        outcome.Response = BuildResult(
                            id,
                            RevvyJson.Object()
                                .Set("engine", RevvyEnv.EngineId)
                                .Set("project_path", RevvyEditorFacts.CachedProjectPath)
                                .Set("bridge_instance_id", RevvyEditorFacts.BridgeInstanceId)
                                .Set("editor_port", activeServer != null ? activeServer.Port : 0));
                        break;

                    case "tools/list":
                        outcome.Response = BuildResult(
                            id,
                            RevvyJson.Object().Set(
                                "tools",
                                RevvyToolRegistry.BuildToolListJson(readOnly)));
                        break;

                    case "tools/call":
                        outcome.Response = BuildResult(id, HandleToolsCall(parameters, readOnly));
                        break;

                    default:
                        outcome.Response = BuildError(id, -32601, "Method not found: " + method);
                        break;
                }
            }
            catch (ThreadAbortException)
            {
                // Unity aborts socket workers while tearing down the AppDomain for
                // play-mode reload. Let that lifecycle cancellation propagate
                // without recording it as an MCP dispatch failure.
                throw;
            }
            catch (Exception exception)
            {
                RevvyLog.Exception("MCP dispatch failed for " + method, exception);
                outcome.Response = BuildError(id, -32603, "Internal error: " + RevvyLog.Describe(exception));
            }

            return outcome;
        }

        private static RevvyJson HandleInitialize(RevvyJson parameters)
        {
            string requested = null;
            if (parameters != null && parameters.IsObject && parameters.Get("protocolVersion") != null)
            {
                requested = parameters.Get("protocolVersion").AsString(null);
            }

            string negotiated = SupportedProtocolVersions[0];
            if (!string.IsNullOrEmpty(requested))
            {
                foreach (string supported in SupportedProtocolVersions)
                {
                    if (string.Equals(supported, requested, StringComparison.Ordinal))
                    {
                        negotiated = requested;
                        break;
                    }
                }
            }

            // §3 requires serverInfo {name: "revvy-<engine>", version}. The version
            // reported is the bridge package version; engine_version travels in
            // revvy-proxy.json (§2) and editor_status.
            RevvyJson serverInfo = RevvyJson.Object()
                .Set("name", ServerName)
                .Set("version", ServerVersion);

            return RevvyJson.Object()
                .Set("protocolVersion", negotiated)
                .Set("capabilities", RevvyJson.Object().Set("tools", RevvyJson.Object()))
                .Set("serverInfo", serverInfo);
        }

        private static RevvyJson HandleToolsCall(RevvyJson parameters, bool readOnly)
        {
            string name = null;
            RevvyJson arguments = null;
            if (parameters != null && parameters.IsObject)
            {
                RevvyJson nameNode = parameters.Get("name");
                name = nameNode != null ? nameNode.AsString(null) : null;
                arguments = parameters.Get("arguments");
            }

            if (string.IsNullOrEmpty(name))
            {
                return ToolError("tools/call requires a 'name' parameter");
            }

            if (readOnly)
            {
                string denial;
                if (!RevvyToolRegistry.IsReadOnlyCallAllowed(name, arguments, out denial))
                {
                    RevvyLog.Warn("Read-only bridge scope blocked tool call: " + name);
                    return ToolError(denial);
                }
            }

            try
            {
                RevvyJson payload = RevvyToolRegistry.Invoke(name, arguments);
                if (payload == null)
                {
                    payload = RevvyJson.Object().Set("success", true);
                }

                // Tools may signal a soft failure by setting success=false themselves.
                bool isError = payload.Has("success") && !payload.Get("success").AsBool(true);

                // Lift any attached content blocks out before serializing, so a large
                // attachment is carried once as its own block rather than also being
                // inlined into the text block's JSON.
                RevvyJson attachments = payload.Get(AttachmentsKey);
                payload.Remove(AttachmentsKey);
                return ToolContent(payload, isError, attachments);
            }
            catch (RevvyToolException toolFailure)
            {
                return ToolError(
                    toolFailure.Message,
                    ResolveErrorCode(name, toolFailure.ErrorCode),
                    toolFailure.Details);
            }
            catch (TimeoutException timeout)
            {
                return ToolError(timeout.Message, ResolveErrorCode(name));
            }
            catch (ThreadAbortException)
            {
                // A play-mode domain reload can abort this worker while it waits
                // for the Unity main thread. It is not a tool execution failure.
                throw;
            }
            catch (Exception exception)
            {
                RevvyLog.Exception("Tool '" + name + "' threw", exception);
                return ToolError(RevvyLog.Describe(exception), ResolveErrorCode(name));
            }
        }

        private static string ResolveErrorCode(string toolName, string explicitCode = null)
        {
            if (!string.IsNullOrEmpty(explicitCode))
            {
                return explicitCode;
            }

            RevvyToolDescriptor descriptor = RevvyToolRegistry.Find(toolName);
            return descriptor != null ? descriptor.Attribute.DefaultErrorCode : null;
        }

        /// <summary>
        /// Reserved payload key a tool uses to attach non-text MCP content blocks
        /// (docs/design/tier10-inline-image-contract.md §4).
        ///
        /// It is lifted out and removed before the payload is serialized, so a large
        /// attachment never gets duplicated into the text block as well.
        /// </summary>
        public const string AttachmentsKey = "_content_blocks";

        /// <summary>Wraps a payload as an MCP result: JSON inside a single text content block (§3).</summary>
        public static RevvyJson ToolContent(RevvyJson payload, bool isError)
        {
            return ToolContent(payload, isError, null);
        }

        /// <summary>
        /// As above, plus additional content blocks appended after the text block.
        ///
        /// The text block stays first and stays the complete JSON result, so a client
        /// that only reads text keeps working exactly as before.
        /// </summary>
        public static RevvyJson ToolContent(RevvyJson payload, bool isError, RevvyJson attachments)
        {
            RevvyJson block = RevvyJson.Object()
                .Set("type", "text")
                .Set("text", payload.ToJson(false));

            RevvyJson content = RevvyJson.Array().Add(block);
            if (attachments != null && attachments.IsArray)
            {
                foreach (RevvyJson attachment in attachments.Items)
                {
                    if (attachment != null && attachment.IsObject)
                    {
                        content.Add(attachment);
                    }
                }
            }

            return RevvyJson.Object()
                .Set("content", content)
                .Set("isError", isError);
        }

        public static RevvyJson ToolError(string message, string errorCode = null, RevvyJson details = null)
        {
            RevvyJson payload = RevvyJson.Object().Set("success", false);
            if (!string.IsNullOrEmpty(errorCode))
            {
                payload.Set("engine", RevvyEnv.EngineId).Set("error_code", errorCode);
            }

            payload.Set("error", message ?? "unknown error");

            // Optional machine-readable diagnosis, merged as sibling fields so a caller
            // can act on why the call failed. Reserved envelope keys are never
            // overwritten, so a careless tool cannot forge success or a different code.
            if (details != null && details.IsObject)
            {
                foreach (string key in details.Keys)
                {
                    if (key == "success" || key == "error" || key == "error_code" || key == "engine")
                    {
                        continue;
                    }

                    payload.Set(key, details.Get(key));
                }
            }

            return ToolContent(payload, true);
        }

        // ------------------------------------------------------------------ envelopes

        private static string BuildResult(RevvyJson id, RevvyJson result)
        {
            return RevvyJson.Object()
                .Set("jsonrpc", "2.0")
                .Set("id", id ?? RevvyJson.Null())
                .Set("result", result ?? RevvyJson.Object())
                .ToJson(false);
        }

        private static string BuildError(RevvyJson id, int code, string message)
        {
            RevvyJson error = RevvyJson.Object()
                .Set("code", code)
                .Set("message", message ?? string.Empty);

            return RevvyJson.Object()
                .Set("jsonrpc", "2.0")
                .Set("id", id ?? RevvyJson.Null())
                .Set("error", error)
                .ToJson(false);
        }
    }
}
