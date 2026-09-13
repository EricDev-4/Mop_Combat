using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using RevStudio.Revvy.Editor;

public static class VerifyBridgeShutdown
{
    public static string Main()
    {
        long maxShutdownMs = 0;
        for (int iteration = 0; iteration < 10; iteration++)
        {
            using (var server = new RevvyEditorServer())
            {
                if (!server.Start(0)) throw new Exception(server.LastError);
                var listener = (TcpListener)typeof(RevvyEditorServer)
                    .GetField("_listener", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(server);
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                using (var client = new TcpClient())
                {
                    client.Connect(IPAddress.Loopback, port);
                    var stream = client.GetStream();
                    // An incomplete HTTP request leaves a worker blocked in Read.
                    var bytes = System.Text.Encoding.ASCII.GetBytes("GET /health HTTP/1.1\r\n");
                    stream.Write(bytes, 0, bytes.Length);
                    var clients = typeof(RevvyEditorServer).GetField("_openConnections", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (!SpinWait.SpinUntil(() => (int)clients.GetValue(server) > 0, 2000))
                        throw new Exception("Connection was not accepted");
                    var timer = Stopwatch.StartNew();
                    server.Stop();
                    maxShutdownMs = Math.Max(maxShutdownMs, timer.ElapsedMilliseconds);
                    using (var replacement = new RevvyEditorServer())
                    {
                        if (!replacement.Start(port)) throw new Exception("Restart failed: " + replacement.LastError);
                        var competitor = new TcpListener(IPAddress.Loopback, port);
                        bool rejected = false;
                        try { competitor.Start(); }
                        catch (SocketException) { rejected = true; }
                        finally { competitor.Stop(); }
                        if (!rejected) throw new Exception("Exclusive listener allowed a competing bind");
                    }
                }
            }
        }
        return "PASS: 10 restarts with blocked client reads; competing binds rejected; maximum shutdown " + maxShutdownMs + "ms";
    }
}
