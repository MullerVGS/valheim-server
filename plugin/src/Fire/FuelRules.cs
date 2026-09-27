using System;

namespace ValheimMetrics.Fire
{
    public readonly struct FuelStep
    {
        // Combustivel a somar no ZDO. Se BurnedOut, e o total que o fogo deveria ter (ele esta em zero).
        public readonly float Owed;
        public readonly bool BurnedOut;

        public FuelStep(float owed, bool burnedOut)
        {
            Owed = owed;
            BurnedOut = burnedOut;
        }
    }

    // O dono do fogo desconta tempo/m_secPerFuel a cada 2 s (Fireplace.UpdateFireplace) e grava
    // fuel e lastTime no ZDO. O servidor ve cada mudanca e devolve a parte que um fogo `factor`
    // vezes mais economico nao teria gasto.
    public static class FuelRules
    {
        public const string Variable = "VALHEIM_FIRE_FUEL_FACTOR";
        public const int MaxFactor = 100;

        const double TicksPerSecond = 10_000_000;

        public static FuelStep Observe(float fuelBefore, long ticksBefore, float fuelAfter, long ticksAfter,
            float secPerFuel, int factor, float owed)
        {
            double seconds = (ticksAfter - ticksBefore) / TicksPerSecond;
            if (factor <= 1 || secPerFuel <= 0 || seconds <= 0)
                return new FuelStep(owed, false);

            double gameBurn = seconds / secPerFuel;

            // Acabou dentro da janela (tipico de quem volta depois de horas longe): o jogo parou de
            // contar no zero, entao o que resta sai do relogio, nao da diferenca.
            if (fuelAfter <= 0 && fuelBefore > 0)
                return new FuelStep((float)Math.Max(0, fuelBefore + owed - gameBurn / factor), true);

            // Queda maior que o relogio (escrita nossa perdida, retirada) so devolve o que o relogio
            // justifica; subida ou nada (reabastecido, molhado, desligado) nao devolve nada.
            double seen = fuelBefore - fuelAfter;
            if (seen <= 0)
                return new FuelStep(owed, false);
            return new FuelStep(owed + (float)(Math.Min(seen, gameBurn) * (1 - 1.0 / factor)), false);
        }

        // 1 = jogo; ausente ou invalido = desligado.
        public static int? ParseFactor(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            return int.TryParse(raw.Trim(), out var factor) && factor > 1 && factor <= MaxFactor ? factor : (int?)null;
        }
    }
}
