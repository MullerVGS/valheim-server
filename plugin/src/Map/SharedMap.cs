using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ValheimMetrics.Map
{
    public sealed class MapPin
    {
        public long Owner;
        public string Name;
        public float X;
        public float Y;
        public float Z;
        public int Type;
        public bool Checked;
        public string Author;
    }

    // O que a mesa de cartografia guarda no campo "data" do ZDO: gzip de um ZPackage escrito por
    // Minimap.GetSharedMapData. Versao (int), tamanho (int) e um bool por pixel do mapa em ordem de
    // linha (i = z, j = x), depois os pins; a partir da versao 3 cada pin traz o "Steam_<id>" do autor.
    public sealed class SharedMap
    {
        public const int Size = 2048;
        public const float PixelSize = 12f;

        public readonly bool[] Explored;
        public readonly List<MapPin> Pins;

        public SharedMap(bool[] explored, List<MapPin> pins)
        {
            Explored = explored;
            Pins = pins;
        }

        public static SharedMap FromCompressed(byte[] data)
        {
            using (var input = new MemoryStream(data))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                gzip.CopyTo(output);
                return Parse(output.ToArray());
            }
        }

        public static SharedMap Parse(byte[] raw)
        {
            using (var reader = new BinaryReader(new MemoryStream(raw), Encoding.UTF8))
            {
                int version = reader.ReadInt32();
                int count = reader.ReadInt32();
                if (count != Size * Size)
                    throw new InvalidDataException($"mapa com {count} pixels, esperado {Size * Size}");
                var explored = new bool[count];
                var bytes = reader.ReadBytes(count);
                for (int i = 0; i < count; i++)
                    explored[i] = bytes[i] != 0;

                var pins = new List<MapPin>();
                if (version >= 2)
                {
                    int n = reader.ReadInt32();
                    for (int i = 0; i < n; i++)
                    {
                        var pin = new MapPin
                        {
                            Owner = reader.ReadInt64(),
                            Name = reader.ReadString(),
                            X = reader.ReadSingle(),
                            Y = reader.ReadSingle(),
                            Z = reader.ReadSingle(),
                            Type = reader.ReadInt32(),
                            Checked = reader.ReadBoolean(),
                        };
                        pin.Author = version >= 3 ? reader.ReadString() : "";
                        pins.Add(pin);
                    }
                }
                return new SharedMap(explored, pins);
            }
        }

        // Pixel do mapa que contem o ponto, como Minimap.WorldToPixel.
        public static bool ToPixel(double x, double z, out int j, out int i)
        {
            j = (int)Math.Floor(x / PixelSize + Size / 2);
            i = (int)Math.Floor(z / PixelSize + Size / 2);
            return j >= 0 && j < Size && i >= 0 && i < Size;
        }

        // Varias mesas: explorado e a uniao; o mesmo pin copiado de uma mesa para outra vale uma vez.
        public static SharedMap Union(IList<SharedMap> maps)
        {
            var explored = new bool[Size * Size];
            var pins = new List<MapPin>();
            var seen = new HashSet<string>();
            foreach (var map in maps)
            {
                for (int i = 0; i < explored.Length; i++)
                    explored[i] |= map.Explored[i];
                foreach (var pin in map.Pins)
                {
                    var key = $"{Math.Round(pin.X)}|{Math.Round(pin.Z)}|{pin.Type}|{pin.Name}";
                    if (seen.Add(key))
                        pins.Add(pin);
                }
            }
            return new SharedMap(explored, pins);
        }
    }
}
