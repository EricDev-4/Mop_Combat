using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RevStudio.Revvy.Editor
{
    /// <summary>
    /// Authenticates the private proxy-to-editor hop. The long-lived capability
    /// stays in the owner-only Revvy security file; only a one-use, body-bound
    /// HMAC crosses the loopback socket.
    /// </summary>
    internal static class RevvyBridgeAuth
    {
        internal const string InstanceHeader = "X-Revvy-Bridge-Instance";
        internal const string ScopeHeader = "X-Revvy-Access-Scope";
        internal const string TimestampHeader = "X-Revvy-Request-Timestamp";
        internal const string NonceHeader = "X-Revvy-Request-Nonce";
        internal const string RequestSignatureHeader = "X-Revvy-Request-Signature";
        internal const string ResponseSignatureHeader = "X-Revvy-Response-Signature";

        private const string SignatureVersion = "v2";
        private const long AllowedClockSkewSeconds = 30;
        private const long ReplayRetentionSeconds = 60;

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static readonly object ReplayGate = new object();
        private static readonly Dictionary<string, long> SeenNonces =
            new Dictionary<string, long>(StringComparer.Ordinal);

        internal sealed class SignedRequest
        {
            public string Scope;
            public string Instance;
            public string Timestamp;
            public string Nonce;
            public byte[] RequestBody;
            public string Token;
            public int Port;
        }

        /// <summary>
        /// Public callers may only use JSON-RPC ping. Any other POST must carry a
        /// valid proxy signature, even when it presents the public Safe bearer.
        /// </summary>
        internal static bool TryAuthorize(
            RevvyHttpRequest request,
            string expectedInstance,
            int expectedPort,
            out SignedRequest signedRequest,
            out string error)
        {
            signedRequest = null;
            error = null;

            bool isPing = IsPublicPing(request.Body);
            bool hasSignedHeader = HasAnySignedHeader(request);
            if (isPing && !hasSignedHeader)
            {
                return true;
            }

            string instance = Normalize(request.Header(InstanceHeader));
            string scope = Normalize(request.Header(ScopeHeader));
            string timestamp = Normalize(request.Header(TimestampHeader));
            string nonce = Normalize(request.Header(NonceHeader));
            string presentedSignature = Normalize(request.Header(RequestSignatureHeader));

            if (!string.Equals(scope, "read_only", StringComparison.Ordinal) &&
                !string.Equals(scope, "full", StringComparison.Ordinal))
            {
                error = "Invalid or missing proxy access scope";
                return false;
            }

            if (!string.Equals(instance, expectedInstance, StringComparison.Ordinal) ||
                string.IsNullOrEmpty(instance) || expectedPort < 1 || expectedPort > 65535)
            {
                error = "Proxy bridge identity does not match this listener";
                return false;
            }

            long timestampSeconds;
            if (string.IsNullOrEmpty(timestamp) ||
                !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out timestampSeconds))
            {
                error = "Invalid proxy request timestamp";
                return false;
            }

            long now = UnixTimeSeconds();
            if (timestampSeconds < now - AllowedClockSkewSeconds ||
                timestampSeconds > now + AllowedClockSkewSeconds)
            {
                error = "Expired proxy request timestamp";
                return false;
            }

            if (!IsSafeNonce(nonce))
            {
                error = "Invalid proxy request nonce";
                return false;
            }

            if (presentedSignature == null ||
                presentedSignature.Length != SignatureVersion.Length + 1 + 40 ||
                !presentedSignature.StartsWith(SignatureVersion + "=", StringComparison.Ordinal))
            {
                error = "Invalid or missing proxy request signature";
                return false;
            }

            string token = LoadBackendToken();
            if (string.IsNullOrEmpty(token))
            {
                error = "Protected editor backend capability is unavailable";
                return false;
            }

            byte[] rawBody = request.BodyBytes ?? new byte[0];
            byte[] canonicalPrefix = Utf8.GetBytes(
                "revvy-proxy-v2\n" + scope + "\n" + instance + "\n" +
                expectedPort.ToString(CultureInfo.InvariantCulture) + "\n" + timestamp + "\n" +
                nonce + "\n" + rawBody.Length.ToString(CultureInfo.InvariantCulture) + "\n");
            string expectedSignature = SignatureVersion + "=" +
                HmacSha1Hex(token, Join(canonicalPrefix, rawBody));
            if (!FixedTimeEquals(presentedSignature, expectedSignature))
            {
                error = "Invalid proxy request signature";
                return false;
            }

            // Consume only authenticated nonces. Invalid traffic therefore cannot
            // fill the replay cache or prevent a future legitimate request.
            string replayKey = instance + "\n" + nonce;
            lock (ReplayGate)
            {
                PurgeExpiredNonces(now);
                if (SeenNonces.ContainsKey(replayKey))
                {
                    error = "Proxy request nonce was already used";
                    return false;
                }

                SeenNonces[replayKey] = now;
            }

            signedRequest = new SignedRequest
            {
                Scope = scope,
                Instance = instance,
                Timestamp = timestamp,
                Nonce = nonce,
                RequestBody = rawBody,
                Token = token,
                Port = expectedPort
            };
            return true;
        }

        internal static string SignResponse(SignedRequest request, int httpStatus, byte[] responseBody)
        {
            if (request == null)
            {
                return null;
            }

            byte[] rawResponse = responseBody ?? new byte[0];
            byte[] rawRequest = request.RequestBody ?? new byte[0];
            byte[] prefix = Utf8.GetBytes(
                "revvy-bridge-response-v2\n" + request.Scope + "\n" + request.Instance + "\n" +
                request.Port.ToString(CultureInfo.InvariantCulture) + "\n" + request.Timestamp + "\n" +
                request.Nonce + "\n" + httpStatus.ToString(CultureInfo.InvariantCulture) + "\n" +
                rawRequest.Length.ToString(CultureInfo.InvariantCulture) + "\n");
            byte[] separator = Utf8.GetBytes(
                "\n" + rawResponse.Length.ToString(CultureInfo.InvariantCulture) + "\n");
            return SignatureVersion + "=" + HmacSha1Hex(
                request.Token,
                Join(prefix, rawRequest, separator, rawResponse));
        }

        internal static byte[] EncodeBody(string body)
        {
            return string.IsNullOrEmpty(body) ? new byte[0] : Utf8.GetBytes(body);
        }

        private static bool IsPublicPing(string body)
        {
            string parseError;
            RevvyJson request = RevvyJson.TryParse(body, out parseError);
            if (request == null || !request.IsObject || request.IsArray)
            {
                return false;
            }

            foreach (string key in request.Keys)
            {
                if (key != "jsonrpc" && key != "id" && key != "method" && key != "params")
                {
                    return false;
                }
            }

            RevvyJson version = request.Get("jsonrpc");
            RevvyJson method = request.Get("method");
            if (version == null || version.Kind != RevvyJsonKind.String ||
                !string.Equals(version.AsString(null), "2.0", StringComparison.Ordinal) ||
                method == null || method.Kind != RevvyJsonKind.String ||
                !string.Equals(method.AsString(null), "ping", StringComparison.Ordinal))
            {
                return false;
            }

            RevvyJson parameters = request.Get("params");
            return parameters == null || (parameters.IsObject && parameters.Count == 0);
        }

        private static bool HasAnySignedHeader(RevvyHttpRequest request)
        {
            return request.Header(InstanceHeader) != null ||
                request.Header(ScopeHeader) != null ||
                request.Header(TimestampHeader) != null ||
                request.Header(NonceHeader) != null ||
                request.Header(RequestSignatureHeader) != null;
        }

        private static bool IsSafeNonce(string nonce)
        {
            if (string.IsNullOrEmpty(nonce) || nonce.Length != 32)
            {
                return false;
            }

            for (int index = 0; index < nonce.Length; index++)
            {
                char value = nonce[index];
                if (!((value >= '0' && value <= '9') ||
                      (value >= 'a' && value <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }

        private static string LoadBackendToken()
        {
            try
            {
                string path = CapabilityPath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return null;
                }

                string parseError;
                RevvyJson document = RevvyJson.TryParse(File.ReadAllText(path, Encoding.UTF8), out parseError);
                if (document == null || !document.IsObject)
                {
                    return null;
                }

                RevvyJson versionNode = document.Get("version");
                RevvyJson algorithmNode = document.Get("algorithm");
                if (versionNode == null || versionNode.AsInt(0) != 2 ||
                    algorithmNode == null ||
                    !string.Equals(
                        algorithmNode.AsString(null),
                        "hmac-sha1-exact-v2",
                        StringComparison.Ordinal))
                {
                    return null;
                }

                RevvyJson tokenNode = document.Get("bridge_hmac_key");
                string token = tokenNode != null ? Normalize(tokenNode.AsString(null)) : null;
                return token != null && token.Length >= 32 ? token : null;
            }
            catch (Exception exception)
            {
                RevvyLog.Warn("Could not read protected editor backend capability: " + RevvyLog.Describe(exception));
                return null;
            }
        }

        private static string CapabilityPath()
        {
            string explicitFile = Normalize(Environment.GetEnvironmentVariable("REVVY_BRIDGE_AUTH_FILE"));
            if (explicitFile != null)
            {
                return ExpandUserPath(explicitFile);
            }

            string securityDirectory = Normalize(Environment.GetEnvironmentVariable("REVVY_SECURITY_DIR"));
            if (securityDirectory != null)
            {
                return Path.Combine(ExpandUserPath(securityDirectory), "bridge-auth.json");
            }

            bool windows = Path.DirectorySeparatorChar == '\\';
            if (windows)
            {
                string localAppData = Normalize(Environment.GetEnvironmentVariable("LOCALAPPDATA"));
                if (localAppData == null)
                {
                    localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                }

                return Path.Combine(localAppData, "Revvy", "Security", "bridge-auth.json");
            }

            string stateHome = Normalize(Environment.GetEnvironmentVariable("XDG_STATE_HOME"));
            if (stateHome != null)
            {
                return Path.Combine(ExpandUserPath(stateHome), "Revvy", "Security", "bridge-auth.json");
            }

            string home = Normalize(Environment.GetEnvironmentVariable("HOME"));
            if (home == null)
            {
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            return Path.Combine(home, ".local", "state", "Revvy", "Security", "bridge-auth.json");
        }

        private static string ExpandUserPath(string path)
        {
            if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) ||
                path.StartsWith("~\\", StringComparison.Ordinal))
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(home))
                {
                    home = Environment.GetEnvironmentVariable("HOME");
                }

                path = path.Length == 1 ? home : Path.Combine(home, path.Substring(2));
            }

            return Path.GetFullPath(path);
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.Trim();
        }

        private static long UnixTimeSeconds()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        private static void PurgeExpiredNonces(long now)
        {
            List<string> expired = null;
            foreach (KeyValuePair<string, long> entry in SeenNonces)
            {
                if (entry.Value < now - ReplayRetentionSeconds)
                {
                    if (expired == null)
                    {
                        expired = new List<string>();
                    }

                    expired.Add(entry.Key);
                }
            }

            if (expired == null)
            {
                return;
            }

            foreach (string key in expired)
            {
                SeenNonces.Remove(key);
            }
        }

        private static string HmacSha1Hex(string token, byte[] canonical)
        {
            using (HMACSHA1 hmac = new HMACSHA1(Utf8.GetBytes(token)))
            {
                return Hex(hmac.ComputeHash(canonical));
            }
        }

        private static byte[] Join(params byte[][] segments)
        {
            int length = 0;
            foreach (byte[] segment in segments)
            {
                checked
                {
                    length += segment != null ? segment.Length : 0;
                }
            }

            byte[] result = new byte[length];
            int offset = 0;
            foreach (byte[] segment in segments)
            {
                if (segment == null || segment.Length == 0)
                {
                    continue;
                }

                Buffer.BlockCopy(segment, 0, result, offset, segment.Length);
                offset += segment.Length;
            }

            return result;
        }

        private static string Hex(byte[] bytes)
        {
            char[] result = new char[bytes.Length * 2];
            const string alphabet = "0123456789abcdef";
            for (int index = 0; index < bytes.Length; index++)
            {
                result[index * 2] = alphabet[bytes[index] >> 4];
                result[index * 2 + 1] = alphabet[bytes[index] & 0x0f];
            }

            return new string(result);
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            int difference = 0;
            for (int index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }

            return difference == 0;
        }
    }
}
