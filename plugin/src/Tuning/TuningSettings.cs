using System;
using System.Collections.Generic;

namespace ValheimMetrics.Tuning
{
    // Off = metodo do jogo intocado. Measure = replica do jogo que so conta o que a histerese seguraria.
    public enum OwnerHysteresisMode { Off, Measure, On }

    // Ajustes opt-in do motor de rede, lidos do ambiente. Variavel ausente = comportamento do jogo.
    public sealed class TuningSettings
    {
        public const string FpsVariable = "VALHEIM_SERVER_FPS";
        public const string SendLimitVariable = "VALHEIM_ZDO_SEND_LIMIT_BYTES";
        public const string SteamSendRateVariable = "VALHEIM_STEAM_SEND_RATE_BYTES";
        public const string DeferAssetUnloadVariable = "VALHEIM_DEFER_ASSET_UNLOAD";
        public const string OwnerHysteresisVariable = "VALHEIM_OWNER_HYSTERESIS";

        // PresentManager.RequestTargetFrameRate troca valor fora de 30..360 por -1, que e FPS sem teto.
        public const int MinFps = 30;
        public const int MaxFps = 360;

        // Abaixo do original o servidor so piora; um pacote de 64 KB ja leva quase meio segundo
        // para escoar no teto de 150 KiB/s do Steam.
        public const int GameSendLimitBytes = 10240;
        public const int MaxSendLimitBytes = 65536;

        // SendRateMin = SendRateMax fixados pelo jogo. Acima de 1 MiB/s (~8 Mbps) por jogador a
        // conta de banda da VPS pesa mais que qualquer ganho.
        public const int GameSteamSendRateBytes = 153600;
        public const int MaxSteamSendRateBytes = 1048576;

        public int? ServerFps { get; private set; }
        public int? ZdoSendLimitBytes { get; private set; }
        public int? SteamSendRateBytes { get; private set; }
        public bool DeferAssetUnload { get; private set; }
        public OwnerHysteresisMode OwnerHysteresis { get; private set; }
        public IReadOnlyList<string> Warnings => _warnings;

        readonly List<string> _warnings = new List<string>();

        public static TuningSettings Parse(Func<string, string> env)
        {
            var s = new TuningSettings();
            s.ServerFps = s.ReadInt(env, FpsVariable, MinFps, MaxFps);
            s.ZdoSendLimitBytes = s.ReadInt(env, SendLimitVariable, GameSendLimitBytes, MaxSendLimitBytes);
            s.SteamSendRateBytes = s.ReadInt(env, SteamSendRateVariable, GameSteamSendRateBytes, MaxSteamSendRateBytes);
            s.DeferAssetUnload = s.ReadWord(env, DeferAssetUnloadVariable, "1", "0") == "1";
            s.OwnerHysteresis = s.ReadWord(env, OwnerHysteresisVariable, "on", "measure", "off") switch
            {
                "on" => OwnerHysteresisMode.On,
                "measure" => OwnerHysteresisMode.Measure,
                _ => OwnerHysteresisMode.Off,
            };
            return s;
        }

        string ReadWord(Func<string, string> env, string name, params string[] allowed)
        {
            var raw = env(name);
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            var value = raw.Trim().ToLowerInvariant();
            if (Array.IndexOf(allowed, value) >= 0)
                return value;
            _warnings.Add($"{name}={raw} ignorado: esperado {string.Join(", ", allowed)}.");
            return null;
        }

        int? ReadInt(Func<string, string> env, string name, int min, int max)
        {
            var raw = env(name);
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            if (!int.TryParse(raw.Trim(), out var value) || value < min || value > max)
            {
                _warnings.Add($"{name}={raw} ignorado: esperado inteiro entre {min} e {max}.");
                return null;
            }
            return value;
        }
    }
}
