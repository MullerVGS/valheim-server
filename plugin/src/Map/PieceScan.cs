using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using ValheimMetrics.Traffic;

namespace ValheimMetrics.Map
{
    // Forma de cada prefab construivel vista de cima, tirada dos colliders do proprio prefab: parede
    // vira linha fina, piso vira quadrado. Montado uma vez, na thread principal.
    sealed class PieceCatalog
    {
        public struct Shape
        {
            public PieceKind Kind;
            public float CenterX;
            public float CenterZ;
            public float HalfX;
            public float HalfZ;
        }

        const float Fallback = 0.5f;
        readonly Dictionary<int, Shape> _shapes = new Dictionary<int, Shape>();
        readonly HashSet<int> _crops = new HashSet<int>();

        public int Count => _shapes.Count;

        public static PieceCatalog Build(ZNetScene scene)
        {
            var catalog = new PieceCatalog();
            foreach (var prefab in scene.m_prefabs)
            {
                var plant = prefab != null ? prefab.GetComponent<Plant>() : null;
                if (plant == null)
                    continue;
                // Muda de arvore cresce no mesmo prefab das arvores nativas: sem criador, nao da para separar.
                foreach (var grown in plant.m_grownPrefabs)
                    if (grown != null && grown.GetComponent<TreeBase>() == null)
                        catalog._crops.Add(grown.name.GetStableHashCode());
            }
            foreach (var prefab in scene.m_prefabs)
            {
                if (prefab == null)
                    continue;
                int hash = prefab.name.GetStableHashCode();
                var piece = prefab.GetComponent<Piece>();
                if (piece == null && !catalog._crops.Contains(hash))
                    continue;
                Footprint(prefab, out var cx, out var cz, out var hx, out var hz);
                catalog._shapes[hash] = new Shape { Kind = KindOf(prefab, piece, catalog._crops.Contains(hash)), CenterX = cx, CenterZ = cz, HalfX = hx, HalfZ = hz };
            }
            return catalog;
        }

        public bool TryGet(int prefab, out Shape shape) => _shapes.TryGetValue(prefab, out shape);

        // Os maiores, para o log: collider grande demais denuncia area que nao e corpo da peca.
        public string Largest(int n)
        {
            var names = new Dictionary<int, string>();
            foreach (var prefab in ZNetScene.instance.m_prefabs)
                if (prefab != null)
                    names[prefab.name.GetStableHashCode()] = prefab.name;
            var list = new List<KeyValuePair<int, Shape>>(_shapes);
            list.Sort((a, b) => (b.Value.HalfX * b.Value.HalfZ).CompareTo(a.Value.HalfX * a.Value.HalfZ));
            var parts = new List<string>();
            for (int i = 0; i < list.Count && i < n; i++)
                parts.Add($"{(names.TryGetValue(list[i].Key, out var name) ? name : list[i].Key.ToString())} {list[i].Value.HalfX * 2:0.#}x{list[i].Value.HalfZ * 2:0.#}");
            return string.Join(", ", parts);
        }

        // Planta crescida nasce sem criador: e a unica peca sem autor que entra no mapa.
        public bool IsCrop(int prefab) => _crops.Contains(prefab);

        static PieceKind KindOf(GameObject prefab, Piece piece, bool crop)
        {
            if (crop || prefab.GetComponent<Plant>() != null)
                return PieceKind.Crop;
            if (prefab.GetComponent<Ship>() != null)
                return PieceKind.Ship;
            var wear = prefab.GetComponent<WearNTear>();
            bool building = piece != null && (piece.m_category == Piece.PieceCategory.BuildingWorkbench
                || piece.m_category == Piece.PieceCategory.BuildingStonecutter
                || piece.m_category == Piece.PieceCategory.DeepNorth);
            if (wear == null || !building)
                return PieceKind.Furniture;
            switch (wear.m_materialType)
            {
                case WearNTear.MaterialType.Wood: return PieceKind.Wood;
                case WearNTear.MaterialType.HardWood: return PieceKind.HardWood;
                case WearNTear.MaterialType.Timberwood: return PieceKind.Timberwood;
                case WearNTear.MaterialType.Stone: return PieceKind.Stone;
                case WearNTear.MaterialType.Marble: return PieceKind.Marble;
                case WearNTear.MaterialType.Ashstone: return PieceKind.Grausten;
                case WearNTear.MaterialType.Iron: return PieceKind.Iron;
                case WearNTear.MaterialType.Ancient: return PieceKind.Ancient;
                case WearNTear.MaterialType.Ice: return PieceKind.Ice;
                default: return PieceKind.Wood;
            }
        }

        // Caixa em x/z, no espaco do prefab, que cobre os colliders solidos (inclusive dos filhos
        // inativos: o estado novo/gasto/quebrado troca so a malha).
        static void Footprint(GameObject prefab, out float cx, out float cz, out float hx, out float hz)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            var toRoot = prefab.transform.worldToLocalMatrix;
            foreach (var col in prefab.GetComponentsInChildren<Collider>(true))
            {
                // EffectArea (area de base, fogo, sem-monstro) so vira trigger no Awake: no prefab parece solido.
                if (col.isTrigger || col.GetComponent<EffectArea>() != null)
                    continue;
                Vector3 center, size;
                switch (col)
                {
                    case BoxCollider box: center = box.center; size = box.size; break;
                    case SphereCollider sphere: center = sphere.center; size = Vector3.one * sphere.radius * 2; break;
                    case CapsuleCollider cap:
                        center = cap.center;
                        size = Vector3.one * cap.radius * 2;
                        size[cap.direction] = Math.Max(cap.height, cap.radius * 2);
                        break;
                    case MeshCollider mesh when mesh.sharedMesh != null:
                        center = mesh.sharedMesh.bounds.center;
                        size = mesh.sharedMesh.bounds.size;
                        break;
                    default: continue;
                }
                var m = toRoot * col.transform.localToWorldMatrix;
                for (int k = 0; k < 8; k++)
                {
                    var corner = center + Vector3.Scale(size * 0.5f, new Vector3((k & 1) != 0 ? 1 : -1, (k & 2) != 0 ? 1 : -1, (k & 4) != 0 ? 1 : -1));
                    var p = m.MultiplyPoint3x4(corner);
                    minX = Math.Min(minX, p.x);
                    maxX = Math.Max(maxX, p.x);
                    minZ = Math.Min(minZ, p.z);
                    maxZ = Math.Max(maxZ, p.z);
                }
            }
            if (minX > maxX)
            {
                cx = cz = 0;
                hx = hz = Fallback;
                return;
            }
            cx = (minX + maxX) / 2;
            cz = (minZ + maxZ) / 2;
            hx = (maxX - minX) / 2;
            hz = (maxZ - minZ) / 2;
        }
    }

    // Varredura das pecas feita aos pedacos na thread principal (o ZDOMan nao e thread-safe): a cada
    // frame, setores de 64 m inteiros ate estourar o orcamento. So entra o que as mesas mostram e o
    // que alguem construiu (criador preenchido), ou planta crescida.
    sealed class PieceScan
    {
        readonly List<ZDO>[] _sectors;
        readonly bool[] _explored;
        readonly PieceCatalog _catalog;
        readonly List<PieceMark> _marks = new List<PieceMark>();
        int _next = 1;

        public int Frames;
        public double Seconds;
        public double MaxFrameSeconds;
        public int Zdos;

        public PieceScan(List<ZDO>[] sectors, bool[] explored, PieceCatalog catalog)
        {
            _sectors = sectors;
            _explored = explored;
            _catalog = catalog;
        }

        public bool Done => _next >= _sectors.Length;
        public bool[] Explored => _explored;
        public List<PieceMark> Marks => _marks;

        public void Step(double budgetSeconds)
        {
            var sw = Stopwatch.StartNew();
            Frames++;
            while (_next < _sectors.Length && sw.Elapsed.TotalSeconds < budgetSeconds)
            {
                int s = _next++;
                var list = _sectors[s];
                if (list == null || list.Count == 0)
                    continue;
                float sx = (s % 512 - 256) * Zone.Size, sz = (s / 512 - 256) * Zone.Size;
                if (!Near(sx, sz))
                    continue;
                foreach (var zdo in list)
                {
                    Zdos++;
                    int prefab = zdo.GetPrefab();
                    if (!_catalog.TryGet(prefab, out var shape))
                        continue;
                    if (!_catalog.IsCrop(prefab) && zdo.GetLong(ZDOVars.s_creator) == 0)
                        continue;
                    var pos = zdo.GetPosition();
                    if (!SharedMap.ToPixel(pos.x, pos.z, out var j, out var i) || !_explored[i * SharedMap.Size + j])
                        continue;
                    var rot = zdo.GetRotation();
                    var offset = rot * new Vector3(shape.CenterX, 0, shape.CenterZ);
                    _marks.Add(new PieceMark(shape.Kind, pos.x + offset.x, pos.y, pos.z + offset.z, rot.eulerAngles.y, shape.HalfX, shape.HalfZ));
                }
            }
            double spent = sw.Elapsed.TotalSeconds;
            Seconds += spent;
            if (spent > MaxFrameSeconds)
                MaxFrameSeconds = spent;
        }

        // Setor com algum pixel explorado por perto (a zona de 64 m cobre ~5 pixels de 12 m).
        bool Near(float x, float z)
        {
            for (float dz = -32; dz <= 32; dz += 16)
                for (float dx = -32; dx <= 32; dx += 16)
                    if (SharedMap.ToPixel(x + dx, z + dz, out var j, out var i) && _explored[i * SharedMap.Size + j])
                        return true;
            return false;
        }
    }
}
