using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ValheimMetrics.Map
{
    // PNG RGBA 8 bits sem depender do Unity: o servidor roda com -nographics e o EncodeToPNG nao e
    // garantido fora da thread principal. zlib = cabecalho de 2 bytes + deflate + adler32.
    public static class Png
    {
        static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        static readonly uint[] CrcTable = BuildCrcTable();

        public static byte[] Encode(int width, int height, byte[] rgba) => Encode(width, height, rgba, null);

        // raw: buffer reaproveitavel das linhas com o byte de filtro (altura x (largura x 4 + 1)).
        public static byte[] Encode(int width, int height, byte[] rgba, byte[] raw)
        {
            if (rgba.Length != width * height * 4)
                throw new ArgumentException("rgba nao bate com a dimensao");

            if (raw == null || raw.Length != height * (width * 4 + 1))
                raw = new byte[height * (width * 4 + 1)];
            for (int y = 0; y < height; y++)
            {
                int row = y * (width * 4 + 1);
                raw[row] = 0;
                Buffer.BlockCopy(rgba, y * width * 4, raw, row + 1, width * 4);
            }

            byte[] idat;
            using (var zlib = new MemoryStream())
            {
                zlib.WriteByte(0x78);
                zlib.WriteByte(0x9C);
                using (var deflate = new DeflateStream(zlib, CompressionLevel.Optimal, leaveOpen: true))
                    deflate.Write(raw, 0, raw.Length);
                uint adler = Adler32(raw);
                zlib.WriteByte((byte)(adler >> 24));
                zlib.WriteByte((byte)(adler >> 16));
                zlib.WriteByte((byte)(adler >> 8));
                zlib.WriteByte((byte)adler);
                idat = zlib.ToArray();
            }

            using (var png = new MemoryStream())
            {
                png.Write(Signature, 0, Signature.Length);
                var ihdr = new byte[13];
                WriteBE(ihdr, 0, (uint)width);
                WriteBE(ihdr, 4, (uint)height);
                ihdr[8] = 8;
                ihdr[9] = 6;
                WriteChunk(png, "IHDR", ihdr);
                WriteChunk(png, "IDAT", idat);
                WriteChunk(png, "IEND", new byte[0]);
                return png.ToArray();
            }
        }

        static void WriteChunk(Stream s, string type, byte[] data)
        {
            var head = new byte[8];
            WriteBE(head, 0, (uint)data.Length);
            Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
            s.Write(head, 0, 8);
            s.Write(data, 0, data.Length);
            uint crc = 0xFFFFFFFF;
            crc = Crc(crc, head, 4, 4);
            crc = Crc(crc, data, 0, data.Length);
            var tail = new byte[4];
            WriteBE(tail, 0, crc ^ 0xFFFFFFFF);
            s.Write(tail, 0, 4);
        }

        static uint Crc(uint crc, byte[] buf, int offset, int count)
        {
            for (int i = offset; i < offset + count; i++)
                crc = CrcTable[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (var d in data)
            {
                a = (a + d) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        static void WriteBE(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }
    }
}
