using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValheimMetrics.Tuning
{
    // O dedicado manda ZDO a um jogador por frame, com ate 10240 bytes por pacote: com N jogadores
    // cada um recebe no maximo 10240 * FPS / (N+1) bytes/s, e o Steam nunca envia mais que 153600
    // bytes/s por conexao. As alavancas sao o FPS (fixo em 30 por
    // GraphicsSettingsManager.RequestTargetFrameRateFromPreset), a constante de SendZDOs e a taxa
    // do Steam (ZSteamSocket.RegisterGlobalCallbacks).
    static class ServerTuning
    {
        // Limite efetivo, lido pelo ThrottleCollector para saber quando o ciclo foi pulado.
        public static int SendLimitBytes = TuningSettings.GameSendLimitBytes;

        static int? _fps;
        static bool _fpsCorrected;
        static int _pendingLimit;
        static int _pendingSendRate;

        public static void Install(Harmony harmony, TuningSettings settings)
        {
            foreach (var warning in settings.Warnings)
                Plugin.Log.LogWarning(warning);

            var self = typeof(ServerTuning);
            if (settings.ServerFps is int fps)
            {
                _fps = fps;
                Patcher.Patch(harmony, typeof(PresentManager), "RequestTargetFrameRate", new[] { typeof(int) }, self,
                    prefix: nameof(RequestTargetFrameRatePrefix));
                Plugin.Log.LogInfo($"FPS do servidor: {fps}");
            }

            if (settings.ZdoSendLimitBytes is int limit)
            {
                _pendingLimit = limit;
                var zdoPeer = AccessTools.Inner(typeof(ZDOMan), "ZDOPeer");
                if (Patcher.Patch(harmony, typeof(ZDOMan), "SendZDOs", new[] { zdoPeer, typeof(bool) }, self,
                        transpiler: nameof(SendZDOsTranspiler)))
                {
                    SendLimitBytes = limit;
                    Plugin.Log.LogInfo($"Limite de ZDO por ciclo: {limit} bytes");
                }
            }

            // Global do Steam, aplicado antes do socket de escuta existir: toda conexao aceita herda.
            if (settings.SteamSendRateBytes is int rate)
            {
                _pendingSendRate = rate;
                if (Patcher.Patch(harmony, typeof(ZSteamSocket), "RegisterGlobalCallbacks", Type.EmptyTypes, self,
                        transpiler: nameof(RegisterGlobalCallbacksTranspiler)))
                    Plugin.Log.LogInfo($"Taxa de envio do Steam: {rate} bytes/s");
            }
        }

        // O PresentManager so escreve Application.targetFrameRate quando o limite calculado muda:
        // se o jogo pediu os 30 antes do patch existir, ninguem mais pediria. O log sai uma vez.
        public static void OnFrame()
        {
            if (_fps is int fps && Application.targetFrameRate != fps)
            {
                Application.targetFrameRate = fps;
                if (!_fpsCorrected)
                {
                    _fpsCorrected = true;
                    Plugin.Log.LogInfo($"targetFrameRate corrigido para {fps} fora do PresentManager.");
                }
            }
        }

        static void RequestTargetFrameRatePrefix(ref int __0)
        {
            __0 = _fps.Value;
        }

        // SendZDOs usa a constante duas vezes (fila maxima e espaco disponivel).
        static IEnumerable<CodeInstruction> SendZDOsTranspiler(IEnumerable<CodeInstruction> instructions) =>
            ReplaceConstant(instructions, "SendZDOs", TuningSettings.GameSendLimitBytes, _pendingLimit, 2);

        // Um unico valor fixado vai para SendRateMin e SendRateMax: taxa constante, sem recuo por perda.
        static IEnumerable<CodeInstruction> RegisterGlobalCallbacksTranspiler(IEnumerable<CodeInstruction> instructions) =>
            ReplaceConstant(instructions, "RegisterGlobalCallbacks", TuningSettings.GameSteamSendRateBytes, _pendingSendRate, 1);

        // Outro numero de ocorrencias = o jogo mudou; recusa o patch em vez de trocar so parte.
        static List<CodeInstruction> ReplaceConstant(IEnumerable<CodeInstruction> instructions, string method,
            int from, int to, int expected)
        {
            var codes = new List<CodeInstruction>(instructions);
            int replaced = 0;
            foreach (var code in codes)
            {
                if (code.opcode == OpCodes.Ldc_I4 && code.operand is int value && value == from)
                {
                    code.operand = to;
                    replaced++;
                }
            }
            if (replaced != expected)
                throw new InvalidOperationException($"{method} tem {replaced} ocorrencias de {from}, esperado {expected}.");
            return codes;
        }
    }
}
