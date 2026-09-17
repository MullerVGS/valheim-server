using System;
using System.Collections.Generic;

namespace ValheimMetrics.Tuning
{
    // Ajustes opt-in do motor de rede, lidos do ambiente. Variavel ausente = comportamento do jogo.
    public sealed class TuningSettings
    {
        public const string FpsVariable = "VALHEIM_SERVER_FPS";
        public const string SendLimitVariable = "VALHEIM_ZDO_SEND_LIMIT_BYTES";

        // PresentManager.RequestTargetFrameRate troca valor fora de 30..360 por -1, que e FPS sem teto.
        public const int MinFps = 30;
        public const int MaxFps = 360;

        // Abaixo do original o servidor so piora; um pacote de 64 KB ja leva quase meio segundo
        // para escoar no teto de 150 KiB/s do Steam.
        public const int GameSendLimitBytes = 10240;
        public const int MaxSendLimitBytes = 65536;

        public int? ServerFps { get; private set; }
        public int? ZdoSendLimitBytes { get; private set; }
        public IReadOnlyList<string> Warnings => _warnings;

        readonly List<string> _warnings = new List<string>();

        public static TuningSettings Parse(Func<string, string> env)
        {
            var s = new TuningSettings();
            s.ServerFps = s.ReadInt(env, FpsVariable, MinFps, MaxFps);
            s.ZdoSendLimitBytes = s.ReadInt(env, SendLimitVariable, GameSendLimitBytes, MaxSendLimitBytes);
            return s;
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
