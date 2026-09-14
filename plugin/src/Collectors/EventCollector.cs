using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimMetrics.Exposition;

namespace ValheimMetrics.Collectors
{
    // No dedicado so GetCurrentRandomEvent enxerga a raid; InEvent/GetActiveEvent sao sempre falsos.
    // O tempo do evento so anda com alguem dentro do raio, entao "running" = m_time avancou.
    sealed class EventCollector : ICollector
    {
        string _lastName;
        float _lastTime;
        readonly Dictionary<string, long> _started = new Dictionary<string, long>();

        public string Name => "events";

        public void Install(Harmony harmony)
        {
        }

        public void Write(PrometheusWriter w, double now)
        {
            var ev = RandEventSystem.instance?.GetCurrentRandomEvent();
            bool running = false;
            if (ev != null)
            {
                if (ev.m_name != _lastName || ev.m_time < _lastTime)
                    _started[ev.m_name] = (_started.TryGetValue(ev.m_name, out var n) ? n : 0) + 1;
                else
                    running = ev.m_time > _lastTime;
                _lastName = ev.m_name;
                _lastTime = ev.m_time;
            }
            else
            {
                _lastName = null;
                _lastTime = 0;
            }

            w.Family("valheim_event_active", "gauge", "1 enquanto ha raid setada, com o nome do evento.");
            if (ev != null)
                w.Sample("valheim_event_active", 1, "event", ev.m_name);

            w.Family("valheim_event_running", "gauge", "1 se o tempo da raid avancou no ultimo segundo (ha jogador no raio).");
            w.Sample("valheim_event_running", running ? 1 : 0);

            w.Family("valheim_event_elapsed_seconds", "gauge", "Tempo decorrido da raid.");
            w.Sample("valheim_event_elapsed_seconds", ev?.m_time ?? 0);

            w.Family("valheim_event_duration_seconds", "gauge", "Duracao total da raid.");
            w.Sample("valheim_event_duration_seconds", ev?.m_duration ?? 0);

            w.Family("valheim_events_started_total", "counter", "Raids iniciadas por evento.");
            foreach (var kv in _started)
                w.Sample("valheim_events_started_total", kv.Value, "event", kv.Key);

            int inRange = 0;
            w.Family("valheim_player_in_event_range", "gauge", "1 se o jogador esta no raio da raid.");
            foreach (var p in Players.Connected)
            {
                bool inside = ev != null && DistanceXZ(p.Peer.m_refPos, ev.m_pos) < ev.m_eventRange;
                if (inside)
                    inRange++;
                w.Sample("valheim_player_in_event_range", inside ? 1 : 0, p.Labels);
            }

            w.Family("valheim_event_players_in_range", "gauge", "Jogadores no raio da raid.");
            w.Sample("valheim_event_players_in_range", inRange);
        }

        static float DistanceXZ(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
