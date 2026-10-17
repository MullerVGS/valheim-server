using System;
using System.Collections.Generic;
using HarmonyLib;
using ValheimMetrics.Collectors;
using ValheimMetrics.Exposition;

namespace ValheimMetrics.Training
{
    // Training dummies with `factor` times the health and regen, without a client mod (DummyRules).
    // The server sees a dummy when its owner sends the ZDO (built, hit, loaded) and, on the next
    // frame, writes level, max health and health ahead in revision; the owner adopts them.
    sealed class TrainingDummy : ICollector
    {
        const uint RevisionLead = 1000;
        const float GameHealth = 2500;

        static readonly int DummyPrefab = "piece_TrainingDummy".GetStableHashCode();

        static int _factor;
        static float _baseHealth;
        static readonly HashSet<ZDOID> _pending = new HashSet<ZDOID>();
        static readonly HashSet<ZDOID> _seen = new HashSet<ZDOID>();
        static readonly List<ZDOID> _due = new List<ZDOID>();
        static long _writes;

        public string Name => "training_dummy";

        public void Install(Harmony harmony)
        {
            var raw = Environment.GetEnvironmentVariable(DummyRules.Variable);
            var factor = DummyRules.ParseFactor(raw);
            if (factor == null)
            {
                if (!string.IsNullOrWhiteSpace(raw))
                    Plugin.Log.LogWarning($"{DummyRules.Variable}={raw} ignored: expected an integer between 1 and {DummyRules.MaxFactor}.");
                return;
            }
            if (!Patcher.Patch(harmony, typeof(ZDO), "Deserialize", new[] { typeof(ZPackage) }, typeof(TrainingDummy),
                    postfix: nameof(DeserializePostfix), tag: "training_dummy"))
                return;
            _factor = factor.Value;
            Plugin.Log.LogInfo($"Training dummies: {_factor}x health and regen.");
        }

        static void DeserializePostfix(ZDO __instance)
        {
            try
            {
                if (__instance.GetPrefab() == DummyPrefab)
                    _pending.Add(__instance.m_uid);
            }
            catch
            {
                Patcher.Errors++;
            }
        }

        public static void OnFrame(double now)
        {
            if (_factor == 0 || _pending.Count == 0 || ZDOMan.instance == null)
                return;
            _due.Clear();
            _due.AddRange(_pending);
            _pending.Clear();
            foreach (var id in _due)
            {
                try
                {
                    var zdo = ZDOMan.instance.GetZDO(id);
                    if (zdo == null || !zdo.IsValid())
                    {
                        _seen.Remove(id);
                        continue;
                    }
                    _seen.Add(id);
                    float baseHealth = BaseHealth();
                    int level = zdo.GetInt(ZDOVars.s_level, 1);
                    float max = zdo.GetFloat(ZDOVars.s_maxHealth, baseHealth * level);
                    float health = zdo.GetFloat(ZDOVars.s_health, max);
                    var fix = DummyRules.Fix(level, max, health, baseHealth, _factor);
                    if (fix == null)
                        continue;
                    zdo.Set(ZDOVars.s_level, fix.Value.Level);
                    zdo.Set(ZDOVars.s_maxHealth, fix.Value.MaxHealth);
                    zdo.Set(ZDOVars.s_health, fix.Value.Health);
                    zdo.DataRevision += RevisionLead;
                    _writes++;
                }
                catch (Exception e)
                {
                    Patcher.Errors++;
                    Plugin.Log.LogWarning($"Training dummies: {id} skipped: {e.Message}");
                }
            }
        }

        static float BaseHealth()
        {
            if (_baseHealth > 0)
                return _baseHealth;
            var prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(DummyPrefab) : null;
            var character = prefab ? prefab.GetComponent<Character>() : null;
            _baseHealth = character != null && character.m_health > 0 ? character.m_health : GameHealth;
            return _baseHealth;
        }

        public void Write(PrometheusWriter w, double now)
        {
            if (_factor == 0)
                return;
            w.Family("valheim_training_dummy_factor", "gauge", "How many times the game's health and regen training dummies get.");
            w.Sample("valheim_training_dummy_factor", _factor);
            w.Family("valheim_training_dummy_seen", "gauge", "Training dummies whose ZDO reached the server since boot.");
            w.Sample("valheim_training_dummy_seen", _seen.Count);
            w.Family("valheim_training_dummy_writes_total", "counter", "Server writes of level and health into training dummies.");
            w.Sample("valheim_training_dummy_writes_total", _writes);
        }
    }
}
