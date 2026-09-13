using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RevStudio.Revvy.Editor
{
    /// <summary>
    /// A parsed HTTP/1.1 request. Header names are matched case-insensitively.
    /// </summary>
    public sealed class RevvyHttpRequest
    {
        public string Method = string.Empty;
        public string Path = string.Empty;
        public string Version = "HTTP/1.1";
        public string Body = string.Empty;
        /// <summary>
        /// Exact bytes received after the HTTP header. Authentication hashes these
        /// bytes rather than a decode/re-encode of <see cref="Body"/>.
        /// </summary>
        public byte[] BodyBytes = new byte[0];

        public readonly Dictionary<string, string> Headers =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Header(string name)
        {
            string value;
            return Headers.TryGetValue(name, out value) ? value : null;
        }

        public bool HeaderContains(string name, string needle)
        {
            string value = Header(name);
            return value != null && value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Client asked for an SSE stream (Streamable HTTP transport, contract §3).</summary>
        public bool AcceptsEventStream
        {
            get { return HeaderContains("Accept", "text/event-stream"); }
        }

        public bool WantsKeepAlive
        {
            get
            {
                string connection = Header("Connection");
                if (connection == null)
                {
                    return !string.Equals(Version, "HTTP/1.0", StringComparison.OrdinalIgnoreCase);
                }

                return connection.IndexOf("close", StringComparison.OrdinalIgnoreCase) < 0;
            }
        }
    }

    /// <summary>
    /// Blocking HTTP/1.1 reader and writer over a raw stream.
    ///
    /// Why not <c>HttpListener</c>: on desktop CLR it is backed by http.sys and needs a
    /// <c>netsh http add urlacl</c> reservation (or elevation) to bind a prefix, which
    /// would make the bridge fail on a plain user account. A socket-level
    /// implementation also gives direct control over SSE framing and mirrors the UE
    /// MCP server, which is likewise socket-based.
    /// </summary>
    public static class RevvyHttp
    {
        /// <summary>Refuse absurd bodies outright; execute_script payloads are the largest legitimate case.</summary>
        public const int MaxBodyBytes = 8 * 1024 * 1024;

        private const int MaxHeaderBytes = 64 * 1024;

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        /// <summary>
        /// Reads one request. Returns null on a clean client disconnect.
        /// </summary>
        /// <exception cref="IOException">Malformed request or oversized headers/body.</exception>
        public static RevvyHttpRequest ReadRequest(Stream stream)
        {
            string headerBlock = ReadHeaderBlock(stream);
            if (headerBlock == null)
            {
                return null;
            }

            string[] lines = headerBlock.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || string.IsNullOrEmpty(lines[0]))
            {
                throw new IOException("Empty HTTP request line");
            }

            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2)
            {
                throw new IOException("Malformed HTTP request line: " + lines[0]);
            }

            RevvyHttpRequest request = new RevvyHttpRequest
            {
                Method = requestLine[0],
                Path = requestLine[1],
                Version = requestLine.Length > 2 ? requestLine[2] : "HTTP/1.1"
            };

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                // Duplicate headers are comma-folded per RFC 7230 §3.2.2.
                string existing;
                request.Headers[name] = request.Headers.TryGetValue(name, out existing)
                    ? existing + ", " + value
                    : value;
            }

            int contentLength = 0;
            string rawLength = request.Header("Content-Length");
            if (!string.IsNullOrEmpty(rawLength) &&
                !int.TryParse(rawLength, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength))
            {
                throw new IOException("Malformed Content-Length: " + rawLength);
            }

            if (contentLength < 0 || contentLength > MaxBodyBytes)
            {
                throw new IOException("Request body of " + contentLength + " bytes exceeds the limit");
            }

            if (contentLength > 0)
            {
                byte[] body = new byte[contentLength];
                int read = 0;
                while (read < contentLength)
                {
                    int chunk = stream.Read(body, read, contentLength - read);
                    if (chunk <= 0)
                    {
                        throw new IOException("Client closed the connection mid-body");
                    }

                    read += chunk;
                }

                request.BodyBytes = body;
                request.Body = Utf8.GetString(body);
            }

            return request;
        }

        private static string ReadHeaderBlock(Stream stream)
        {
            MemoryStream buffer = new MemoryStream(1024);
            int matched = 0; // position within the "\r\n\r\n" terminator
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0)
                {
                    // Clean disconnect before any bytes arrived: not an error.
                    if (buffer.Length == 0)
                    {
                        return null;
                    }

                    throw new IOException("Truncated HTTP headers");
                }

                buffer.WriteByte((byte)b);
                if (buffer.Length > MaxHeaderBytes)
                {
                    throw new IOException("HTTP headers exceed " + MaxHeaderBytes + " bytes");
                }

                char c = (char)b;
                if ((matched == 0 || matched == 2) && c == '\r')
                {
                    matched++;
                }
                else if ((matched == 1 || matched == 3) && c == '\n')
                {
                    matched++;
                    if (matched == 4)
                    {
                        byte[] bytes = buffer.ToArray();
                        return Utf8.GetString(bytes, 0, bytes.Length - 4);
                    }
                }
                else
                {
                    matched = 0;
                }
            }
        }

        /// <summary>Writes a complete buffered response (Content-Length framed).</summary>
        public static void WriteResponse(
            Stream stream,
            int status,
            string reason,
            string contentType,
            string body,
            IDictionary<string, string> extraHeaders = null,
            bool keepAlive = false)
        {
            byte[] payload = string.IsNullOrEmpty(body) ? new byte[0] : Utf8.GetBytes(body);

            StringBuilder head = new StringBuilder(256);
            head.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture))
                .Append(' ').Append(reason).Append("\r\n");
            if (!string.IsNullOrEmpty(contentType))
            {
                head.Append("Content-Type: ").Append(contentType).Append("\r\n");
            }

            head.Append("Content-Length: ")
                .Append(payload.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            head.Append("Cache-Control: no-store\r\n");
            head.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n");
            AppendHeaders(head, extraHeaders);
            head.Append("\r\n");

            byte[] headBytes = Utf8.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            if (payload.Length > 0)
            {
                stream.Write(payload, 0, payload.Length);
            }

            stream.Flush();
        }

        /// <summary>Writes the SSE response head; the caller then emits events until it closes.</summary>
        public static void WriteEventStreamHead(Stream stream, IDictionary<string, string> extraHeaders = null)
        {
            StringBuilder head = new StringBuilder(256);
            head.Append("HTTP/1.1 200 OK\r\n");
            head.Append("Content-Type: text/event-stream\r\n");
            head.Append("Cache-Control: no-store\r\n");
            head.Append("Connection: keep-alive\r\n");
            head.Append("X-Accel-Buffering: no\r\n");
            AppendHeaders(head, extraHeaders);
            head.Append("\r\n");

            byte[] bytes = Utf8.GetBytes(head.ToString());
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        /// <summary>Emits a single <c>message</c> SSE event.</summary>
        public static void WriteEvent(Stream stream, string data, long eventId)
        {
            StringBuilder frame = new StringBuilder(data.Length + 64);
            frame.Append("id: ").Append(eventId.ToString(CultureInfo.InvariantCulture)).Append('\n');
            frame.Append("event: message\n");
            // A payload containing newlines must be split across multiple data: lines.
            foreach (string line in data.Split('\n'))
            {
                frame.Append("data: ").Append(line.TrimEnd('\r')).Append('\n');
            }

            frame.Append('\n');

            byte[] bytes = Utf8.GetBytes(frame.ToString());
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        /// <summary>SSE comment frame; keeps idle proxies and NAT tables from dropping the stream.</summary>
        public static void WriteKeepAlive(Stream stream)
        {
            byte[] bytes = Utf8.GetBytes(": keep-alive\n\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private static void AppendHeaders(StringBuilder head, IDictionary<string, string> extraHeaders)
        {
            if (extraHeaders == null)
            {
                return;
            }

            foreach (KeyValuePair<string, string> header in extraHeaders)
            {
                if (string.IsNullOrEmpty(header.Key) || header.Value == null)
                {
                    continue;
                }

                // Header injection guard: values reaching here can echo request data.
                if (header.Value.IndexOf('\r') >= 0 || header.Value.IndexOf('\n') >= 0)
                {
                    continue;
                }

                head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }
        }
    }
}
