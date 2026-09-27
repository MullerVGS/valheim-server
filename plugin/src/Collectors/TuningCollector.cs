using HarmonyLib;
using ValheimMetrics.Exposition;
using ValheimMetrics.Tuning;

namespace ValheimMetrics.Collectors
{
    // O que os ajustes opt-in fizeram: limpeza de assets adiada e trocas de dono feitas ou seguradas.
    sealed class TuningCollector : ICollector
    {
        public string Name => "tuning";

        public void Install(Harmony harmony)
        {
        }

        public void Write(PrometheusWriter w, double now)
        {
            w.Family("valheim_asset_unload_deferred_total", "counter", "Limpezas horarias de assets adiadas porque havia jogador online.");
            w.Sample("valheim_asset_unload_deferred_total", AssetUnload.Deferred);
            w.Family("valheim_asset_unload_idle_runs_total", "counter", "Limpezas adiadas que rodaram quando o servidor esvaziou.");
            w.Sample("valheim_asset_unload_idle_runs_total", AssetUnload.IdleRuns);
            w.Family("valheim_asset_unload_pending", "gauge", "1 = ha limpeza adiada esperando o servidor esvaziar.");
            w.Sample("valheim_asset_unload_pending", AssetUnload.Pending ? 1 : 0);

            w.Family("valheim_owner_hysteresis", "gauge", "Modo da histerese de dono (VALHEIM_OWNER_HYSTERESIS).");
            w.Sample("valheim_owner_hysteresis", 1, "mode", OwnerHandoff.Mode.ToString().ToLowerInvariant());

            if (OwnerHandoff.Mode != OwnerHysteresisMode.Off)
            {
                w.Family("valheim_zdo_owner_changes_total", "counter", "Trocas de dono feitas pela varredura de 2 s do servidor. action=release: dono saiu; claim: jogador assumiu.");
                w.Family("valheim_zdo_owner_kept_total", "counter", "Trocas que a histerese segurou (mode=on) ou seguraria (mode=measure).");
                for (int k = 0; k < OwnerHandoff.KindNames.Length; k++)
                {
                    var kind = OwnerHandoff.KindNames[k];
                    w.Sample("valheim_zdo_owner_changes_total", OwnerHandoff.Releases[k], "action", "release", "kind", kind);
                    w.Sample("valheim_zdo_owner_changes_total", OwnerHandoff.Claims[k], "action", "claim", "kind", kind);
                    w.Sample("valheim_zdo_owner_kept_total", OwnerHandoff.Kept[k], "kind", kind);
                }
            }

            if (ZNet.instance == null)
                return;
            var synced = ZNet.instance.GetSyncedSimulationDistance();
            w.Family("valheim_simulation_distance_zones", "gauge", "Distancia de simulacao do servidor, em zonas de 64 m. near define a area de posse.");
            w.Sample("valheim_simulation_distance_zones", synced.NearSimulationDistance, "bound", "near", "classic", synced.IsClassic ? "1" : "0");
            w.Sample("valheim_simulation_distance_zones", synced.FarSimulationDistance, "bound", "far", "classic", synced.IsClassic ? "1" : "0");

            w.Family("valheim_player_simulation_distance_zones", "gauge", "Distancia validada de cada jogador (menor entre o grafico dele e o servidor): ate onde o cliente carrega objetos.");
            foreach (var p in Players.Connected)
            {
                if (p.Peer == null)
                    continue;
                var d = p.Peer.m_simulationDistance;
                w.Sample("valheim_player_simulation_distance_zones", d.NearSimulationDistance, p.Labels[0], p.Labels[1], p.Labels[2], p.Labels[3], "bound", "near", "classic", d.IsClassic ? "1" : "0");
            }
        }
    }
}
