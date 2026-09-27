using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace ValheimMetrics.Access
{
    sealed class AccessCommand
    {
        public string Action;
        public string SteamId;
        public bool Ok;
        public string Message = "";
        public string State = "";
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
    }

    // Pagina de pedidos numa thread propria. Nao tem login: fica atras do SSO da borda, numa porta que
    // o host nao publica. Leitura serve o ultimo estado publicado; escrita vira comando na fila da
    // thread principal (a whitelist e objeto do jogo) e espera o resultado.
    sealed class AccessServer
    {
        // POST sem este header nao passa: o navegador so manda header proprio de outra origem depois de
        // um preflight, que ninguem responde aqui. E o que impede outra aba de liberar alguem.
        public const string ActionHeader = "x-valheim-access";
        const int WaitMilliseconds = 5000;

        readonly int _port;
        readonly byte[] _page;
        readonly ManualLogSource _log;
        TcpListener _listener;
        Thread _thread;
        volatile bool _running;
        volatile string _state = "{}";

        public readonly ConcurrentQueue<AccessCommand> Commands = new ConcurrentQueue<AccessCommand>();

        public AccessServer(int port, byte[] page, ManualLogSource log)
        {
            _port = port;
            _page = page;
            _log = log;
        }

        public void Publish(string state) => _state = state;

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "ValheimMetrics.Access" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
        }

        void Loop()
        {
            var buffer = new byte[8192];
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
                        while (read < buffer.Length)
                        {
                            int n = stream.Read(buffer, read, buffer.Length - read);
                            if (n <= 0)
                                break;
                            read += n;
                            if (HeaderEnd(buffer, read) > 0)
                                break;
                        }
                        Respond(stream, Encoding.UTF8.GetString(buffer, 0, read));
                    }
                }
                catch (Exception e)
                {
                    if (_running)
                        _log.LogDebug($"Access HTTP: {e.Message}");
                }
            }
        }

        void Respond(System.IO.Stream stream, string request)
        {
            var lines = request.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length < 2)
            {
                Write(stream, 400, "text/plain", "bad request");
                return;
            }
            var method = first[0];
            var target = first[1];
            var query = "";
            int q = target.IndexOf('?');
            if (q >= 0)
            {
                query = target.Substring(q + 1);
                target = target.Substring(0, q);
            }
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length && lines[i].Length > 0; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c > 0)
                    headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
            }

            if (method == "GET" && (target == "/" || target == "/index.html"))
            {
                Write(stream, 200, "text/html; charset=utf-8", _page);
                return;
            }
            if (method == "GET" && target == "/api/state")
            {
                Write(stream, 200, "application/json; charset=utf-8", _state);
                return;
            }
            if (method == "POST" && (target == "/api/permit" || target == "/api/dismiss"))
            {
                if (!headers.ContainsKey(ActionHeader))
                {
                    Write(stream, 403, "text/plain", "forbidden");
                    return;
                }
                var command = new AccessCommand { Action = target.Substring(5), SteamId = Param(query, "id") };
                Commands.Enqueue(command);
                if (!command.Done.Wait(WaitMilliseconds))
                {
                    Write(stream, 504, "application/json; charset=utf-8",
                        "{\"ok\":false,\"message\":\"o servidor não respondeu a tempo\"}");
                    return;
                }
                Write(stream, command.Ok ? 200 : 409, "application/json; charset=utf-8",
                    "{\"ok\":" + (command.Ok ? "true" : "false") + ",\"message\":" + AccessState.Str(command.Message)
                    + ",\"state\":" + command.State + "}");
                return;
            }
            Write(stream, 404, "text/plain", "not found");
        }

        static string Param(string query, string name)
        {
            foreach (var pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0 && pair.Substring(0, eq) == name)
                    return Uri.UnescapeDataString(pair.Substring(eq + 1));
            }
            return "";
        }

        static void Write(System.IO.Stream stream, int status, string type, string body) =>
            Write(stream, status, type, Encoding.UTF8.GetBytes(body));

        static void Write(System.IO.Stream stream, int status, string type, byte[] body)
        {
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n" +
                "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\n" +
                "Connection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            stream.Write(body, 0, body.Length);
        }

        static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 409: return "Conflict";
                default: return "Gateway Timeout";
            }
        }

        static int HeaderEnd(byte[] buf, int len)
        {
            for (int i = 3; i < len; i++)
            {
                if (buf[i - 3] == '\r' && buf[i - 2] == '\n' && buf[i - 1] == '\r' && buf[i] == '\n')
                    return i + 1;
            }
            return 0;
        }
    }
}
