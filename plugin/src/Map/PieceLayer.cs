using System;
using System.Collections.Generic;

namespace ValheimMetrics.Map
{
    // Cor da peca no mapa: material do WearNTear para o que e construcao; o resto por funcao.
    public enum PieceKind : byte
    {
        Wood,
        HardWood,
        Timberwood,
        Stone,
        Marble,
        Grausten,
        Iron,
        Ancient,
        Ice,
        Furniture,
        Crop,
        Ship,
    }

    // Uma peca vista de cima: retangulo no centro X/Z, girado pelo yaw, com meia largura nos eixos
    // locais x e z do prefab. Y e a altura, para o que esta mais alto (telhado) cobrir o de baixo.
    public struct PieceMark
    {
        public float X;
        public float Z;
        public float Y;
        public float Cos;
        public float Sin;
        public float HalfX;
        public float HalfZ;
        public PieceKind Kind;

        public PieceMark(PieceKind kind, float x, float y, float z, float yawDegrees, float halfX, float halfZ)
        {
            Kind = kind;
            X = x;
            Y = y;
            Z = z;
            double yaw = yawDegrees * Math.PI / 180.0;
            Cos = (float)Math.Cos(yaw);
            Sin = (float)Math.Sin(yaw);
            HalfX = halfX;
            HalfZ = halfZ;
        }

        public float Radius => (float)Math.Sqrt(HalfX * HalfX + HalfZ * HalfZ);

        // Estavel entre execucoes: so o que muda o desenho, arredondado ao centimetro.
        public ulong Hash()
        {
            ulong h = 1469598103934665603UL;
            h = Mix(h, (ulong)Kind);
            h = Mix(h, (ulong)(long)Math.Round(X * 100));
            h = Mix(h, (ulong)(long)Math.Round(Z * 100));
            h = Mix(h, (ulong)(long)Math.Round(Y * 100));
            h = Mix(h, (ulong)(long)Math.Round(Math.Atan2(Sin, Cos) * 1000));
            h = Mix(h, (ulong)(long)Math.Round(HalfX * 100));
            h = Mix(h, (ulong)(long)Math.Round(HalfZ * 100));
            return h;
        }

        internal static ulong Mix(ulong h, ulong v)
        {
            unchecked
            {
                h ^= v + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
                h *= 1099511628211UL;
                return h;
            }
        }
    }

    // As pecas do mundo num balde por zona de 64 m, para achar rapido as que caem num tile.
    public sealed class PieceLayer
    {
        // Abaixo disso a peca tem menos de 3 m por pixel e so vira ruido: o tile sai so com o terreno.
        public const int MinZoom = 13;
        const float Bucket = 64f;
        // Contorno escuro na borda da peca, em metros; so aparece quando a peca tem pixels de sobra.
        const double EdgeMeters = 0.12;

        readonly PieceMark[] _marks;
        readonly ulong[] _hashes;
        readonly Dictionary<long, List<int>> _buckets = new Dictionary<long, List<int>>();
        readonly float _maxRadius;
        readonly int[] _byKind = new int[Enum.GetValues(typeof(PieceKind)).Length];

        public PieceLayer(IList<PieceMark> marks)
        {
            _marks = new PieceMark[marks.Count];
            _hashes = new ulong[marks.Count];
            for (int i = 0; i < marks.Count; i++)
            {
                _marks[i] = marks[i];
                _hashes[i] = marks[i].Hash();
                _maxRadius = Math.Max(_maxRadius, marks[i].Radius);
                _byKind[(int)marks[i].Kind]++;
                long key = Key((int)Math.Floor(marks[i].X / Bucket), (int)Math.Floor(marks[i].Z / Bucket));
                if (!_buckets.TryGetValue(key, out var list))
                    _buckets[key] = list = new List<int>();
                list.Add(i);
            }
        }

        public int Count => _marks.Length;

        public int CountOf(PieceKind kind) => _byKind[(int)kind];

        static long Key(int bx, int bz) => ((long)bx << 32) ^ (uint)bz;

        // Indices das pecas cujo circulo toca o retangulo de mundo.
        public void Query(double x0, double z0, double x1, double z1, List<int> result)
        {
            result.Clear();
            double pad = _maxRadius;
            int bx0 = (int)Math.Floor((x0 - pad) / Bucket), bx1 = (int)Math.Floor((x1 + pad) / Bucket);
            int bz0 = (int)Math.Floor((z0 - pad) / Bucket), bz1 = (int)Math.Floor((z1 + pad) / Bucket);
            for (int bz = bz0; bz <= bz1; bz++)
            {
                for (int bx = bx0; bx <= bx1; bx++)
                {
                    if (!_buckets.TryGetValue(Key(bx, bz), out var list))
                        continue;
                    foreach (int i in list)
                    {
                        var m = _marks[i];
                        float r = m.Radius;
                        if (m.X + r >= x0 && m.X - r <= x1 && m.Z + r >= z0 && m.Z - r <= z1)
                            result.Add(i);
                    }
                }
            }
        }

        // Soma dos hashes: nao depende da ordem em que as pecas foram lidas.
        public ulong Signature(double x0, double z0, double x1, double z1, List<int> scratch)
        {
            Query(x0, z0, x1, z1, scratch);
            ulong sum = 0;
            unchecked
            {
                foreach (int i in scratch)
                    sum += _hashes[i];
                sum = PieceMark.Mix(sum, (ulong)scratch.Count);
            }
            return sum;
        }

        // Pinta as pecas por cima do terreno ja desenhado no tile. So onde o terreno tem alfa (area
        // que as mesas mostram); 2x2 amostras por pixel para a borda nao serrilhar.
        public void Draw(TileId tile, byte[] rgba, List<int> scratch)
        {
            if (tile.Zoom < MinZoom || _marks.Length == 0)
                return;
            const int N = MapProjection.TileSize;
            MapProjection.ToWorld(tile.Zoom, tile.X * N, (tile.Y + 1) * N, out var x0, out var z0);
            MapProjection.ToWorld(tile.Zoom, (tile.X + 1) * N, tile.Y * N, out var x1, out var z1);
            Query(x0, z0, x1, z1, scratch);
            if (scratch.Count == 0)
                return;
            scratch.Sort((a, b) =>
            {
                int c = _marks[a].Y.CompareTo(_marks[b].Y);
                return c != 0 ? c : a.CompareTo(b);
            });

            // Pixels por metro no zoom: a projecao e linear na escala de uma peca.
            double scale = MapProjection.TileSize * Math.Pow(2, tile.Zoom) / (360.0 * MapProjection.MetersPerDegree);
            double halfPixel = 0.5 / scale;
            double edge = scale >= 4 ? EdgeMeters : 0;

            foreach (int idx in scratch)
            {
                var m = _marks[idx];
                double hx = Math.Max(m.HalfX, halfPixel), hz = Math.Max(m.HalfZ, halfPixel);
                bool outline = edge > 0 && hx > edge * 2 && hz > edge * 2;
                Color(m.Kind, out var fr, out var fg, out var fb);
                byte er = (byte)(fr * 0.62), eg = (byte)(fg * 0.62), eb = (byte)(fb * 0.62);

                MapProjection.ToGlobalPixel(tile.Zoom, m.X, m.Z, out var gx, out var gy);
                double cx = gx - tile.X * N, cy = gy - tile.Y * N;
                double rp = Math.Sqrt(hx * hx + hz * hz) * scale + 1;
                int c0 = Math.Max(0, (int)Math.Floor(cx - rp)), c1 = Math.Min(N - 1, (int)Math.Ceiling(cx + rp));
                int r0 = Math.Max(0, (int)Math.Floor(cy - rp)), r1 = Math.Min(N - 1, (int)Math.Ceiling(cy + rp));

                for (int r = r0; r <= r1; r++)
                {
                    for (int c = c0; c <= c1; c++)
                    {
                        int o = (r * N + c) * 4;
                        if (rgba[o + 3] == 0)
                            continue;
                        int hits = 0, sr = 0, sg = 0, sb = 0;
                        for (int s = 0; s < 4; s++)
                        {
                            // Amostra no mundo: y do pixel cresce para o sul.
                            double dx = (c + 0.25 + 0.5 * (s & 1) - cx) / scale;
                            double dz = -(r + 0.25 + 0.5 * (s >> 1) - cy) / scale;
                            double lx = dx * m.Cos - dz * m.Sin;
                            double lz = dx * m.Sin + dz * m.Cos;
                            double ax = Math.Abs(lx), az = Math.Abs(lz);
                            if (ax > hx || az > hz)
                                continue;
                            hits++;
                            if (outline && (hx - ax < edge || hz - az < edge))
                            {
                                sr += er; sg += eg; sb += eb;
                            }
                            else
                            {
                                sr += fr; sg += fg; sb += fb;
                            }
                        }
                        if (hits == 0)
                            continue;
                        double a = hits / 4.0;
                        rgba[o] = (byte)Math.Round(rgba[o] * (1 - a) + sr / 4.0);
                        rgba[o + 1] = (byte)Math.Round(rgba[o + 1] * (1 - a) + sg / 4.0);
                        rgba[o + 2] = (byte)Math.Round(rgba[o + 2] * (1 - a) + sb / 4.0);
                    }
                }
            }
        }

        // Tons escolhidos para destacar do terreno de cada bioma sem virar cor de UI.
        public static void Color(PieceKind kind, out byte r, out byte g, out byte b)
        {
            switch (kind)
            {
                case PieceKind.Wood: (r, g, b) = ((byte)158, (byte)108, (byte)60); break;
                case PieceKind.HardWood: (r, g, b) = ((byte)112, (byte)70, (byte)40); break;
                case PieceKind.Timberwood: (r, g, b) = ((byte)190, (byte)150, (byte)96); break;
                case PieceKind.Stone: (r, g, b) = ((byte)150, (byte)150, (byte)144); break;
                case PieceKind.Marble: (r, g, b) = ((byte)232, (byte)228, (byte)214); break;
                case PieceKind.Grausten: (r, g, b) = ((byte)84, (byte)80, (byte)80); break;
                case PieceKind.Iron: (r, g, b) = ((byte)72, (byte)84, (byte)98); break;
                case PieceKind.Ancient: (r, g, b) = ((byte)96, (byte)110, (byte)70); break;
                case PieceKind.Ice: (r, g, b) = ((byte)186, (byte)222, (byte)240); break;
                case PieceKind.Furniture: (r, g, b) = ((byte)196, (byte)84, (byte)52); break;
                case PieceKind.Crop: (r, g, b) = ((byte)110, (byte)180, (byte)60); break;
                case PieceKind.Ship: (r, g, b) = ((byte)60, (byte)44, (byte)30); break;
                default: (r, g, b) = ((byte)200, (byte)0, (byte)200); break;
            }
        }
    }
}
