using System;

namespace ValheimMetrics.Training
{
    // What the server writes into a training dummy's ZDO.
    public readonly struct DummyHealth
    {
        public readonly int Level;
        public readonly float MaxHealth;
        public readonly float Health;

        public DummyHealth(int level, float maxHealth, float health)
        {
            Level = level;
            MaxHealth = maxHealth;
            Health = health;
        }
    }

    // The T.W.I.G. (piece_TrainingDummy) is a Character: max health = m_health x level, and its
    // BaseAI heals max / m_regenAllHPTime per second. Both live on the owning client, but the level
    // is read from the ZDO in Awake, so a dummy at level N has N times the health and N times the
    // regen, and keeps it when reloaded. Level 4+ shows no stars on the hud.
    public static class DummyRules
    {
        public const string Variable = "VALHEIM_DUMMY_HEALTH_FACTOR";
        public const int MaxFactor = 100;

        // null = already right (or dead). Health keeps its fraction of the max.
        public static DummyHealth? Fix(int level, float maxHealth, float health, float baseHealth, int factor)
        {
            if (baseHealth <= 0 || factor < 1 || health <= 0 || maxHealth <= 0)
                return null;
            float target = baseHealth * factor;
            if (level == factor && Math.Abs(maxHealth - target) < 0.5f)
                return null;
            float fraction = Math.Min(1f, health / maxHealth);
            return new DummyHealth(factor, target, target * fraction);
        }

        // 1 = back to the game's health; missing or invalid = off.
        public static int? ParseFactor(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            return int.TryParse(raw.Trim(), out var factor) && factor >= 1 && factor <= MaxFactor ? factor : (int?)null;
        }
    }
}
