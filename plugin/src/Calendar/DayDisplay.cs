using System;
using System.Collections.Generic;
using HarmonyLib;
using ValheimMetrics.Collectors;
using ValheimMetrics.Exposition;

namespace ValheimMetrics.Calendar
{
    // Replaces the "Day N" dawn message without a client mod. Each client writes it locally
    // (EnvMan.OnMorning) when its smoothed day fraction crosses 0.25; the server can only show
    // another center message over it, and the last one wins. The server smooths the fraction the
    // same way and, above 50 FPS, crosses with a client above 50 FPS, earlier by the network latency.
    // Below 50 FPS a client crosses up to a few seconds later, so the message is sent three times.
    sealed class DayDisplay : ICollector
    {
        const int CenterMessage = 2;
        static readonly double[] SendAfterSeconds = { 0.5, 2, 4 };

        static int _shownDay;
        static bool _enabled;
        static float _lastFraction = -1;
        static readonly List<(double at, string text)> _queue = new List<(double, string)>();
        static long _mornings;
        static long _sent;

        public string Name => "day_display";

        public void Install(Harmony harmony)
        {
            var raw = Environment.GetEnvironmentVariable(DayRules.Variable);
            if (string.IsNullOrWhiteSpace(raw))
                return;
            if (DayRules.Parse(raw) is not int day)
            {
                Plugin.Log.LogWarning($"{DayRules.Variable}={raw} ignored: expected a whole number of days.");
                return;
            }
            _shownDay = day;
            _enabled = true;
            Plugin.Log.LogInfo($"Day display: dawn shows day {day}, except {DayRules.CountdownDays} days before each {DayRules.YearDays}-day year.");
        }

        public static void OnFrame(double now)
        {
            if (!_enabled)
                return;
            var env = EnvMan.instance;
            var net = ZNet.instance;
            if (env == null || net == null || !net.IsServer())
                return;
            try
            {
                float fraction = env.GetDayFraction();
                // Same window as EnvMan.UpdateTriggers.
                if (_lastFraction > 0.2f && _lastFraction < 0.25f && fraction > 0.25f && fraction < 0.3f)
                    Morning(env.GetDay(), now);
                _lastFraction = fraction;
                Flush(net, now);
            }
            catch (Exception e)
            {
                Patcher.Errors++;
                Plugin.Log.LogWarning($"Day display: {e.Message}");
            }
        }

        static void Morning(int day, double now)
        {
            _mornings++;
            var text = DayRules.Text(day, _shownDay);
            foreach (var delay in SendAfterSeconds)
                _queue.Add((now + delay, text));
            Plugin.Log.LogInfo($"Day display: day {day} shown as \"{text.Replace('\n', ' ')}\".");
        }

        static void Flush(ZNet net, double now)
        {
            for (int i = _queue.Count - 1; i >= 0; i--)
            {
                if (_queue[i].at > now)
                    continue;
                var text = _queue[i].text;
                _queue.RemoveAt(i);
                foreach (var peer in net.GetPeers())
                {
                    if (!peer.IsReady())
                        continue;
                    ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "ShowMessage", CenterMessage, text);
                    _sent++;
                }
            }
        }

        public void Write(PrometheusWriter w, double now)
        {
            if (!_enabled)
                return;
            w.Family("valheim_day_display_shown_day", "gauge", "Day number dawn shows outside the new year countdown (VALHEIM_SHOWN_DAY).");
            w.Sample("valheim_day_display_shown_day", _shownDay);
            w.Family("valheim_day_display_mornings_total", "counter", "Dawns the server announced.");
            w.Sample("valheim_day_display_mornings_total", _mornings);
            w.Family("valheim_day_display_messages_total", "counter", "Dawn messages sent to players (three per player per dawn).");
            w.Sample("valheim_day_display_messages_total", _sent);
        }
    }
}
