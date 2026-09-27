using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using ValheimMetrics.Collectors;
using ValheimMetrics.Exposition;

namespace ValheimMetrics.Access
{
    // Quem a whitelist barrou, para o admin liberar sem cacar SteamID em log. O servidor ja sabe nome e
    // SteamID de quem tenta (ZNet.IsAllowed recebe os dois); aqui isso vira pedido numerado, aviso aos
    // admins online e duas formas de liberar:
    // - no jogo, sem mod no cliente: `/unban <numero|nome|steamid>` no chat ou no F5. unban e comando de
    //   rede que todo cliente manda ao servidor (chat comum nao chega com o admin sozinho: o cliente so
    //   envia fala para os outros jogadores). Argumento que nao e pedido segue para o unban de verdade.
    //   `/banned` lista os pedidos junto dos banidos.
    // - na pagina (VALHEIM_ACCESS_PORT), que o host nao publica e a borda protege com SSO.
    // Whitelist vazia deixa todo mundo entrar: ai nada e barrado e nao ha pedido.
    sealed class AccessControl : ICollector
    {
        public const string PortVariable = "VALHEIM_ACCESS_PORT";
        const string FileName = "access-requests.tsv";
        const int TopLeft = 1;

        static AccessTools.FieldRef<ZNet, SyncedList> _permittedList;
        static AccessTools.FieldRef<ZNet, SyncedList> _bannedList;
        static Func<ZNet, SyncedList, string, bool> _listContainsId;

        static AccessBook _book;
        static string _path;
        static AccessServer _server;
        static long _deniedTotal;
        static long _permittedTotal;

        public string Name => "access";

        public void Install(Harmony harmony)
        {
            _permittedList = AccessTools.FieldRefAccess<ZNet, SyncedList>("m_permittedList");
            _bannedList = AccessTools.FieldRefAccess<ZNet, SyncedList>("m_bannedList");
            var contains = AccessTools.Method(typeof(ZNet), "ListContainsId", new[] { typeof(SyncedList), typeof(string) })
                ?? throw new MissingMethodException("ZNet", "ListContainsId");
            _listContainsId = AccessTools.MethodDelegate<Func<ZNet, SyncedList, string, bool>>(contains);

            var self = typeof(AccessControl);
            if (!Patcher.Patch(harmony, typeof(ZNet), "IsAllowed", new[] { typeof(string), typeof(string) }, self,
                    postfix: nameof(IsAllowedPostfix)))
                return;
            Patcher.Patch(harmony, typeof(ZNet), "RPC_Unban", new[] { typeof(ZRpc), typeof(string) }, self,
                prefix: nameof(UnbanPrefix));
            Patcher.Patch(harmony, typeof(ZNet), "RPC_PrintBanned", new[] { typeof(ZRpc) }, self,
                postfix: nameof(PrintBannedPostfix));

            var raw = Environment.GetEnvironmentVariable(PortVariable);
            if (string.IsNullOrWhiteSpace(raw))
                return;
            if (!int.TryParse(raw.Trim(), out var port) || port <= 0)
                throw new FormatException($"{PortVariable}={raw} nao e porta.");
            _server = new AccessServer(port, LoadPage(), Plugin.Log);
            _server.Start();
            Plugin.Log.LogInfo($"Pagina de pedidos de entrada em :{port}");
        }

        public static void Stop() => _server?.Stop();

        static byte[] LoadPage()
        {
            using (var stream = typeof(AccessControl).Assembly.GetManifestResourceStream("ValheimMetrics.Access.page.html")
                ?? throw new FileNotFoundException("page.html embutida"))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                return memory.ToArray();
            }
        }

        static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // O caminho do save so existe depois que o jogo le -savedir; o livro abre no primeiro uso.
        static bool Ready()
        {
            var net = ZNet.instance;
            if (net == null || !net.IsServer())
                return false;
            if (_book != null)
                return true;
            _path = Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), FileName);
            _book = File.Exists(_path) ? AccessBook.Parse(File.ReadAllText(_path)) : new AccessBook();
            return true;
        }

        static void Save()
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, _book.Serialize());
            if (File.Exists(_path))
                File.Replace(tmp, _path, null);
            else
                File.Move(tmp, _path);
        }

        static bool IsPermitted(string steamId) =>
            _listContainsId(ZNet.instance, _permittedList(ZNet.instance), steamId);

        static List<AccessEntry> Pending() => _book.Pending(IsPermitted);

        static string StateJson()
        {
            var permitted = _permittedList(ZNet.instance).GetList().ToList();
            return AccessState.ToJson(Pending(), permitted, _book, Now);
        }

        // --- entrada ---------------------------------------------------------------------------

        static void IsAllowedPostfix(ZNet __instance, string __0, string __1, bool __result)
        {
            try
            {
                if (!Ready())
                    return;
                var banned = _bannedList(__instance);
                if (!__result && (_listContainsId(__instance, banned, __0) || banned.Contains(__1)))
                    return;
                bool notify = _book.Record(__0, __1, __result, Now);
                Save();
                if (__result)
                    return;
                _deniedTotal++;
                var entry = _book.Get(__0);
                Plugin.Log.LogInfo($"Barrado pela whitelist: {entry.Name} ({entry.SteamId}), pedido #{entry.Ticket}");
                if (notify)
                    TellAdmins($"<color=orange>{entry.Name}</color> tentou entrar e não está na whitelist. " +
                        $"Para liberar: /unban {entry.Ticket}");
            }
            catch (Exception e)
            {
                Patcher.Errors++;
                Plugin.Log.LogWarning($"Access IsAllowed: {e.Message}");
            }
        }

        // --- comandos no jogo ------------------------------------------------------------------

        static bool UnbanPrefix(ZRpc __0, string __1)
        {
            try
            {
                if (!Ready() || !IsAdmin(__0))
                    return true;
                var entry = AccessBook.Resolve(Pending(), __1);
                if (entry == null)
                    return true;
                Permit(entry, "no jogo");
                return false;
            }
            catch (Exception e)
            {
                Patcher.Errors++;
                Plugin.Log.LogWarning($"Access unban: {e.Message}");
                return true;
            }
        }

        static void PrintBannedPostfix(ZRpc __0)
        {
            try
            {
                if (!Ready() || !IsAdmin(__0))
                    return;
                var pending = Pending();
                Tell(__0, pending.Count == 0
                    ? "Ninguém esperando na whitelist."
                    : "Esperando na whitelist: " + string.Join(", ", pending.Select(e => $"#{e.Ticket} {e.Name}"))
                      + ". Para liberar: /unban <numero>");
            }
            catch (Exception e)
            {
                Patcher.Errors++;
                Plugin.Log.LogWarning($"Access banned: {e.Message}");
            }
        }

        static void Permit(AccessEntry entry, string where)
        {
            _permittedList(ZNet.instance).Add(entry.SteamId);
            _book.Close(entry.SteamId);
            Save();
            _permittedTotal++;
            Plugin.Log.LogInfo($"Liberado na whitelist ({where}): {entry.Name} ({entry.SteamId})");
            TellAdmins($"<color=orange>{entry.Name}</color> liberado na whitelist ({where}).");
        }

        static bool IsAdmin(ZRpc rpc)
        {
            var host = rpc?.GetSocket()?.GetHostName();
            return !string.IsNullOrEmpty(host) && ZNet.instance.IsAdmin(host);
        }

        // Canto superior esquerdo (fica no registro de mensagens do jogo) e o console F5.
        static void Tell(ZRpc rpc, string text)
        {
            rpc.Invoke("RemotePrint", StripTags(text));
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer.m_rpc == rpc && peer.IsReady())
                    ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "ShowMessage", TopLeft, text);
            }
        }

        static void TellAdmins(string text)
        {
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer.IsReady() && ZNet.instance.IsAdmin(peer.m_socket.GetHostName()))
                    Tell(peer.m_rpc, text);
            }
        }

        static string StripTags(string text) =>
            text.Replace("<color=orange>", "").Replace("</color>", "");

        // --- pagina ----------------------------------------------------------------------------

        public static void OnFrame()
        {
            if (_server == null || !Ready())
                return;
            while (_server.Commands.TryDequeue(out var command))
            {
                try
                {
                    Run(command);
                    command.State = StateJson();
                }
                catch (Exception e)
                {
                    command.Ok = false;
                    command.Message = e.Message;
                    command.State = "{}";
                }
                command.Done.Set();
            }
        }

        static void Run(AccessCommand command)
        {
            var entry = Pending().FirstOrDefault(e => e.SteamId == command.SteamId);
            if (entry == null)
            {
                command.Message = "esse pedido não está mais aberto";
                return;
            }
            if (command.Action == "permit")
                Permit(entry, "pela pagina");
            else
            {
                _book.Close(entry.SteamId);
                Save();
                Plugin.Log.LogInfo($"Pedido ignorado pela pagina: {entry.Name} ({entry.SteamId})");
            }
            command.Ok = true;
            command.Message = command.Action == "permit" ? $"{entry.Name} liberado" : $"pedido de {entry.Name} ignorado";
        }

        public void Write(PrometheusWriter w, double now)
        {
            if (!Ready())
                return;
            var pending = Pending();
            _server?.Publish(StateJson());

            w.Family("valheim_access_pending", "gauge", "Pedidos de entrada abertos: barrados pela whitelist e ainda nao liberados.");
            w.Sample("valheim_access_pending", pending.Count);
            w.Family("valheim_access_denied_total", "counter", "Entradas barradas pela whitelist desde o boot.");
            w.Sample("valheim_access_denied_total", _deniedTotal);
            w.Family("valheim_access_permitted_total", "counter", "SteamIDs liberados pelo plugin desde o boot.");
            w.Sample("valheim_access_permitted_total", _permittedTotal);
        }
    }
}
