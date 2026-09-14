using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace ValheimMetrics
{
    // HTTP minimo numa thread propria. So serve o ultimo snapshot; nunca toca objeto do jogo.
    sealed class MetricsServer
    {
        static readonly byte[] NotFound = Encoding.ASCII.GetBytes(
            "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        readonly int _port;
        readonly ManualLogSource _log;
        TcpListener _listener;
        Thread _thread;
        volatile byte[] _body = new byte[0];
        volatile bool _running;

        public MetricsServer(int port, ManualLogSource log)
        {
            _port = port;
            _log = log;
        }

        public void Publish(string text) => _body = Encoding.UTF8.GetBytes(text);

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "ValheimMetrics.Http" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
        }

        void Loop()
        {
            var request = new byte[4096];
            while (_running)
            {
                try
                {
                    using (var client = _listener.AcceptTcpClient())
                    {
                        client.ReceiveTimeout = 2000;
                        client.SendTimeout = 2000;
                        var stream = client.GetStream();
                        int read = 0;
                        while (read < request.Length)
                        {
                            int n = stream.Read(request, read, request.Length - read);
                            if (n <= 0)
                                break;
                            read += n;
                            if (EndsHeaders(request, read))
                                break;
                        }
                        var line = Encoding.ASCII.GetString(request, 0, Math.Min(read, 64));
                        if (!line.StartsWith("GET /metrics", StringComparison.Ordinal))
                        {
                            stream.Write(NotFound, 0, NotFound.Length);
                            continue;
                        }
                        var body = _body;
                        var head = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Type: text/plain; version=0.0.4; charset=utf-8\r\n" +
                            $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                        stream.Write(head, 0, head.Length);
                        stream.Write(body, 0, body.Length);
                    }
                }
                catch (Exception e)
                {
                    if (_running)
                        _log.LogDebug($"HTTP: {e.Message}");
                }
            }
        }

        static bool EndsHeaders(byte[] buf, int len)
        {
            for (int i = 3; i < len; i++)
            {
                if (buf[i - 3] == '\r' && buf[i - 2] == '\n' && buf[i - 1] == '\r' && buf[i] == '\n')
                    return true;
            }
            return false;
        }
    }
}
