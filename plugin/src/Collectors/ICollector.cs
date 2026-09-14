using HarmonyLib;
using ValheimMetrics.Exposition;

namespace ValheimMetrics.Collectors
{
    interface ICollector
    {
        string Name { get; }

        // Aplica patches e resolve membros privados. Excecao aqui desliga so este coletor.
        void Install(Harmony harmony);

        // Roda na thread principal, uma vez por segundo.
        void Write(PrometheusWriter w, double now);
    }
}
