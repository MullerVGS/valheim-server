using System.Collections.Generic;

namespace ValheimMetrics.Tuning
{
    // Quem e dono de uma criatura simula a IA dela e manda cada atualizacao ao servidor. O cliente nao
    // envia mais que 153600 bytes/s (ZSteamSocket.RegisterGlobalCallbacks no DLL do cliente): o dono
    // de 35 criaturas na base fica no teto e tudo dele chega atrasado a todos. O jogo so troca o dono
    // quando ele sai da area; aqui a criatura passa, aos poucos, para quem esta perto e tem folga.
    public static class OwnerBalanceRules
    {
        // ~75% e ~40% do teto do cliente: a faixa entre os dois impede a criatura de voltar.
        public const float OverloadedBytes = 115000f;
        public const float SpareBytes = 60000f;

        // Herdar simulacao engasga o cliente que recebe: poucas por vez, e o Steam suaviza a taxa em
        // alguns segundos, entao so ve o efeito do lote anterior depois da espera.
        public const int MaxPerPass = 4;
        public const double ReceiverCooldownSeconds = 6;
        public const double CreatureCooldownSeconds = 120;

        // Criatura colada no dono (que pode estar lutando com ela) fica com ele.
        public const float CloserSlackMeters = 8f;

        public static bool Overloaded(float uploadBytes) => uploadBytes >= OverloadedBytes;

        public static bool Spare(float uploadBytes) => uploadBytes <= SpareBytes;

        public static bool Closer(float receiverMeters, float ownerMeters) =>
            receiverMeters <= ownerMeters + CloserSlackMeters;
    }

    public sealed class OwnerBalanceBook<TKey>
    {
        readonly Dictionary<TKey, double> _moved = new Dictionary<TKey, double>();
        readonly Dictionary<long, double> _received = new Dictionary<long, double>();
        readonly List<TKey> _expired = new List<TKey>();
        readonly List<long> _expiredReceivers = new List<long>();

        public int Tracked => _moved.Count;

        public bool CanReceive(long receiver, double now) =>
            !_received.TryGetValue(receiver, out var at) || now - at >= OwnerBalanceRules.ReceiverCooldownSeconds;

        public bool CanMove(TKey key, double now) =>
            !_moved.TryGetValue(key, out var at) || now - at >= OwnerBalanceRules.CreatureCooldownSeconds;

        public void Moved(TKey key, long receiver, double now)
        {
            _moved[key] = now;
            _received[receiver] = now;
        }

        public void Prune(double now)
        {
            _expired.Clear();
            foreach (var pair in _moved)
                if (now - pair.Value >= OwnerBalanceRules.CreatureCooldownSeconds)
                    _expired.Add(pair.Key);
            foreach (var key in _expired)
                _moved.Remove(key);

            _expiredReceivers.Clear();
            foreach (var pair in _received)
                if (now - pair.Value >= OwnerBalanceRules.ReceiverCooldownSeconds)
                    _expiredReceivers.Add(pair.Key);
            foreach (var receiver in _expiredReceivers)
                _received.Remove(receiver);
        }
    }
}
