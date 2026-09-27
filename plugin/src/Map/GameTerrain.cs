using UnityEngine;

namespace ValheimMetrics.Map
{
    // Mesmas chamadas que o Minimap do cliente usa para gerar o mapa (GenerateWorldMap/GetMaskColor).
    // O WorldGenerator ja e lido de outras threads pelo proprio jogo (cache de rios com lock).
    sealed class GameTerrain : ITerrain
    {
        readonly WorldGenerator _gen;

        public GameTerrain(WorldGenerator gen)
        {
            _gen = gen;
        }

        public void Sample(double x, double z, out int biome, out float height, out bool forest)
        {
            float wx = (float)x, wz = (float)z;
            var b = _gen.GetBiome(wx, wz);
            height = _gen.GetBiomeHeight(b, wx, wz, out _);
            biome = (int)b;
            var p = new Vector3(wx, 0f, wz);
            switch (b)
            {
                case Heightmap.Biome.Meadows: forest = WorldGenerator.InForest(p); break;
                case Heightmap.Biome.Plains: forest = WorldGenerator.GetForestFactor(p) < 0.8f; break;
                case Heightmap.Biome.BlackForest: forest = true; break;
                case Heightmap.Biome.Mistlands: forest = WorldGenerator.GetForestFactor(p) < 1.2f; break;
                default: forest = false; break;
            }
        }
    }
}
