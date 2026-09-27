using System;

namespace ValheimMetrics.Tuning
{
    public enum HandoffAction { None, Release, Claim }

    public readonly struct HandoffDecision
    {
        public readonly HandoffAction Action;
        // A histerese segurou o dono (On) ou seguraria (Measure, que age como o jogo).
        public readonly bool Kept;

        public HandoffDecision(HandoffAction action, bool kept)
        {
            Action = action;
            Kept = kept;
        }
    }

    // Regras de ZDOMan.ReleaseNearbyZDOS (1.0.14), que o servidor roda a cada 2 s para cada jogador.
    // O jogo tira a posse de quem sai da area ativa (1,5 zona do centro da zona dele, 3x3), mas o
    // cliente do dono segue instanciando o objeto ate a borda da distancia de simulacao dele (5x5 no
    // padrao). A histerese so troca o dono quando o objeto sai do que o dono ainda tem carregado.
    public static class OwnerHandoffRules
    {
        // Mesmo recorte de ZDOMan.FindSectorObjects: anel ate `near`; fora do modo classico, so as
        // zonas cujo centro fica a menos de near + 0,5 zona (ZoneSystem.ZonesWithinRadius).
        public static bool Loaded(int centerX, int centerY, int x, int y, int near, bool classic)
        {
            int dx = Math.Abs(x - centerX);
            int dy = Math.Abs(y - centerY);
            if (Math.Max(dx, dy) > near)
                return false;
            if (classic)
                return true;
            double radius = near + 0.5;
            return dx * dx + dy * dy < radius * radius;
        }

        // selfActive/ownerActive: a regra do jogo. selfLoaded/ownerLoaded: o objeto segue carregado no
        // cliente do dono. sticky: objeto que pode ficar com o dono antigo (tudo menos criatura selvagem
        // e jogador, porque criatura longe do dono luta com atraso para quem esta perto).
        public static HandoffDecision Decide(bool ownedBySelf, bool hasOwner, bool selfActive, bool selfLoaded,
            bool ownerActive, bool ownerLoaded, bool sticky, bool hysteresis)
        {
            if (ownedBySelf)
            {
                if (selfActive)
                    return new HandoffDecision(HandoffAction.None, false);
                if (sticky && selfLoaded)
                    return new HandoffDecision(hysteresis ? HandoffAction.None : HandoffAction.Release, true);
                return new HandoffDecision(HandoffAction.Release, false);
            }
            if (!selfActive || (hasOwner && ownerActive))
                return new HandoffDecision(HandoffAction.None, false);
            if (sticky && hasOwner && ownerLoaded)
                return new HandoffDecision(hysteresis ? HandoffAction.None : HandoffAction.Claim, true);
            return new HandoffDecision(HandoffAction.Claim, false);
        }
    }
}
