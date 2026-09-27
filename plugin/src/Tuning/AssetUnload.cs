using HarmonyLib;

namespace ValheimMetrics.Tuning
{
    // Game.CollectResourcesCheckPeriodic chama Resources.UnloadUnusedAssets de hora em hora: no dedicado
    // a varredura trava a thread 0,7-0,9 s para liberar quase nada (0 assets na maioria das horas).
    // Com gente online a varredura espera o servidor esvaziar; vazio, roda como o jogo.
    static class AssetUnload
    {
        public static bool Enabled { get; private set; }
        public static bool Pending { get; private set; }
        public static long Deferred;
        public static long IdleRuns;

        public static void Install(Harmony harmony, bool enabled)
        {
            if (!enabled)
                return;
            if (Patcher.Patch(harmony, typeof(Game), "CollectResourcesCheckPeriodic", new System.Type[0],
                    typeof(AssetUnload), prefix: nameof(CheckPeriodicPrefix)))
            {
                Enabled = true;
                Plugin.Log.LogInfo("Limpeza horaria de assets adiada enquanto houver jogador online.");
            }
        }

        static bool CheckPeriodicPrefix()
        {
            int players = ZNet.instance == null ? 0 : ZNet.instance.GetPeers().Count;
            if (players == 0)
                return true;
            Deferred++;
            if (!Pending)
                Plugin.Log.LogInfo($"Limpeza de assets adiada: {players} jogador(es) online.");
            Pending = true;
            return false;
        }

        public static void OnFrame()
        {
            if (!Pending || ZNet.instance == null || Game.instance == null || ZNet.instance.GetPeers().Count > 0)
                return;
            Pending = false;
            IdleRuns++;
            Plugin.Log.LogInfo("Servidor vazio: limpeza de assets adiada rodando agora.");
            Game.instance.CollectResources();
        }
    }
}
