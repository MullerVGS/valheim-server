using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimMetrics.Collectors;
using ValheimMetrics.Exposition;

namespace ValheimMetrics.Fire
{
    // Fogo (tocha, braseiro, fogueira, lareira) gastando `factor` vezes menos, sem mod no cliente.
    // Quem desconta o combustivel e o dono do ZDO; o servidor so ve o resultado chegar, anota o que
    // deve devolver (FuelRules) e soma no ZDO de tempos em tempos.
    //
    // O dono grava fuel/lastTime a cada 2 s, entao uma escrita nossa com revisao +1 perderia para a
    // dele quase sempre. A revisao vai adiante com folga: o dono aceita o ZDO inteiro do servidor
    // (fuel e lastTime do mesmo instante) e segue dali sem contar nada duas vezes.
    sealed class FireFuel : ICollector
    {
        const double WriteEverySeconds = 300;
        const double SweepSeconds = 5;
        const uint RevisionLead = 1000;

        static int _factor;
        static readonly Dictionary<int, Fireplace> _fires = new Dictionary<int, Fireplace>();
        static readonly Dictionary<ZDOID, Debt> _debts = new Dictionary<ZDOID, Debt>();
        static readonly List<ZDOID> _due = new List<ZDOID>();
        static double _nextSweep;
        static double _refunded;
        static long _writes;
        static long _relit;
        static long _lost;

        struct Debt
        {
            public float Owed;
            public double Due;
        }

        struct Before
        {
            public Fireplace Fire;
            public float Fuel;
            public long Ticks;
        }

        public string Name => "fire_fuel";

        public void Install(Harmony harmony)
        {
            var raw = Environment.GetEnvironmentVariable(FuelRules.Variable);
            var factor = FuelRules.ParseFactor(raw);
            if (factor == null)
            {
                if (!string.IsNullOrWhiteSpace(raw))
                    Plugin.Log.LogWarning($"{FuelRules.Variable}={raw} ignorado: esperado inteiro entre 2 e {FuelRules.MaxFactor}.");
                return;
            }
            if (!Patcher.Patch(harmony, typeof(ZDO), "Deserialize", new[] { typeof(ZPackage) }, typeof(FireFuel),
                    prefix: nameof(DeserializePrefix), postfix: nameof(DeserializePostfix), tag: "fire_fuel"))
                return;
            _factor = factor.Value;
            Plugin.Log.LogInfo($"Combustivel de fogo: {_factor}x mais lento.");
        }

        // Roda dentro do RPC que recebe ZDO de cliente: guarda o que o servidor tinha antes.
        static void DeserializePrefix(ZDO __instance, out Before __state)
        {
            __state = default;
            try
            {
                var fire = FireOf(__instance.GetPrefab());
                if (fire == null)
                    return;
                long ticks = __instance.GetLong(ZDOVars.s_lastTime);
                if (ticks <= 0)
                    return;
                __state = new Before { Fire = fire, Fuel = __instance.GetFloat(ZDOVars.s_fuel), Ticks = ticks };
            }
            catch
            {
                Patcher.Errors++;
            }
        }

        static void DeserializePostfix(ZDO __instance, Before __state)
        {
            if (__state.Fire == null)
                return;
            try
            {
                var id = __instance.m_uid;
                _debts.TryGetValue(id, out var debt);
                var step = FuelRules.Observe(__state.Fuel, __state.Ticks,
                    __instance.GetFloat(ZDOVars.s_fuel), __instance.GetLong(ZDOVars.s_lastTime),
                    __state.Fire.m_secPerFuel, _factor, debt.Owed);
                if (step.Owed <= 0)
                {
                    _debts.Remove(id);
                    return;
                }
                double now = Time.realtimeSinceStartupAsDouble;
                if (step.BurnedOut)
                    debt.Due = now;
                else if (debt.Owed <= 0)
                    debt.Due = now + WriteEverySeconds;
                debt.Owed = step.Owed;
                _debts[id] = debt;
            }
            catch
            {
                Patcher.Errors++;
            }
        }

        public static void OnFrame(double now)
        {
            if (_factor == 0 || now < _nextSweep || _debts.Count == 0 || ZDOMan.instance == null)
                return;
            _nextSweep = now + SweepSeconds;
            _due.Clear();
            foreach (var kv in _debts)
                if (now >= kv.Value.Due)
                    _due.Add(kv.Key);
            foreach (var id in _due)
            {
                var owed = _debts[id].Owed;
                _debts.Remove(id);
                try
                {
                    var zdo = ZDOMan.instance.GetZDO(id);
                    var fire = zdo != null && zdo.IsValid() ? FireOf(zdo.GetPrefab()) : null;
                    if (fire == null)
                    {
                        _lost++;
                        continue;
                    }
                    float fuel = zdo.GetFloat(ZDOVars.s_fuel);
                    float next = Mathf.Min(fire.m_maxFuel, fuel + owed);
                    if (next <= fuel)
                        continue;
                    zdo.Set(ZDOVars.s_fuel, next);
                    zdo.DataRevision += RevisionLead;
                    _refunded += next - fuel;
                    _writes++;
                    if (fuel <= 0)
                        _relit++;
                }
                catch (Exception e)
                {
                    Patcher.Errors++;
                    Plugin.Log.LogWarning($"Combustivel de fogo: {id} ignorado: {e.Message}");
                }
            }
        }

        static Fireplace FireOf(int prefabHash)
        {
            if (prefabHash == 0 || ZNetScene.instance == null)
                return null;
            if (_fires.TryGetValue(prefabHash, out var cached))
                return cached;
            var prefab = ZNetScene.instance.GetPrefab(prefabHash);
            var fire = prefab ? prefab.GetComponent<Fireplace>() : null;
            if (fire != null && (fire.m_infiniteFuel || fire.m_secPerFuel <= 0))
                fire = null;
            _fires[prefabHash] = fire;
            return fire;
        }

        public void Write(PrometheusWriter w, double now)
        {
            if (_factor == 0)
                return;
            w.Family("valheim_fire_fuel_factor", "gauge", "Quantas vezes o fogo gasta menos que no jogo.");
            w.Sample("valheim_fire_fuel_factor", _factor);
            w.Family("valheim_fire_fuel_refunded_total", "counter", "Unidades de combustivel devolvidas aos fogos.");
            w.Sample("valheim_fire_fuel_refunded_total", _refunded);
            w.Family("valheim_fire_fuel_writes_total", "counter", "Escritas do servidor em ZDO de fogo.");
            w.Sample("valheim_fire_fuel_writes_total", _writes);
            w.Family("valheim_fire_fuel_relit_total", "counter", "Fogos que tinham apagado por falta e voltaram a ter combustivel.");
            w.Sample("valheim_fire_fuel_relit_total", _relit);
            w.Family("valheim_fire_fuel_lost_total", "counter", "Devolucoes descartadas porque o fogo sumiu.");
            w.Sample("valheim_fire_fuel_lost_total", _lost);
            w.Family("valheim_fire_fuel_pending", "gauge", "Fogos com devolucao esperando a proxima escrita.");
            w.Sample("valheim_fire_fuel_pending", _debts.Count);
        }
    }
}
