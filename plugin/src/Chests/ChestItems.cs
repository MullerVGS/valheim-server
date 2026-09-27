using System;
using System.Collections.Generic;
using System.IO;

namespace ValheimMetrics.Chests
{
    // O conteudo de um bau como o jogo grava em ZDOVars.s_items (Inventory.Save / ItemData.Save).
    // Cada item fica com os bytes originais: o servidor so tira ou poe itens inteiros e nunca reescreve
    // o que nao entende. Versao fora de 108..109 nao e mexida (update do jogo muda o formato).
    public sealed class ChestItem
    {
        public int PrefabHash;
        public int X;
        public int Y;
        public int WorldLevel;
        public int Quality;
        public int Stack;
        public byte[] Raw;
    }

    public sealed class ChestItems
    {
        public const int Smaller = 108;
        public const int ChunksNCheats = 109;

        public int Version;
        public readonly List<ChestItem> Items = new List<ChestItem>();

        public static bool TryParse(byte[] data, out ChestItems chest)
        {
            chest = null;
            if (data == null || data.Length < 6)
                return false;
            try
            {
                using (var reader = new BinaryReader(new MemoryStream(data)))
                {
                    var result = new ChestItems { Version = reader.ReadInt32() };
                    if (result.Version < Smaller || result.Version > ChunksNCheats)
                        return false;
                    int count = reader.ReadUInt16();
                    for (int i = 0; i < count; i++)
                        result.Items.Add(ReadItem(reader, result.Version));
                    if (reader.BaseStream.Position != data.Length)
                        return false;
                    chest = result;
                    return true;
                }
            }
            catch (EndOfStreamException)
            {
                return false;
            }
        }

        static ChestItem ReadItem(BinaryReader reader, int version)
        {
            long start = reader.BaseStream.Position;
            var item = new ChestItem();
            reader.ReadInt32(); // durabilidade x100
            item.X = reader.ReadByte();
            item.Y = reader.ReadByte();
            item.WorldLevel = reader.ReadByte();
            byte flags = reader.ReadByte();
            item.Quality = (flags & 4) != 0 ? reader.ReadUInt16() : 1;
            item.Stack = (flags & 8) != 0 ? reader.ReadUInt16() : 1;
            if ((flags & 16) != 0)
                reader.ReadInt32();
            if ((flags & 32) != 0)
            {
                reader.ReadInt64();
                reader.ReadString();
            }
            item.PrefabHash = (flags & 64) != 0 ? reader.ReadInt32() : 0;
            int custom = (flags & 128) != 0 ? ReadNumItems(reader) : 0;
            for (int i = 0; i < custom * 2; i++)
                reader.ReadString();
            if (version >= ChunksNCheats)
                reader.ReadByte();

            long end = reader.BaseStream.Position;
            reader.BaseStream.Position = start;
            item.Raw = reader.ReadBytes((int)(end - start));
            return item;
        }

        static int ReadNumItems(BinaryReader reader)
        {
            int num = reader.ReadByte();
            if ((num & 0x80) != 0)
                num = ((num & 0x7F) << 8) | reader.ReadByte();
            return num;
        }

        public byte[] ToBytes()
        {
            if (Items.Count > ushort.MaxValue)
                throw new InvalidOperationException("Itens demais para o formato.");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Version);
                writer.Write((ushort)Items.Count);
                foreach (var item in Items)
                    writer.Write(item.Raw);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public ChestItem At(int x, int y)
        {
            foreach (var item in Items)
                if (item.X == x && item.Y == y)
                    return item;
            return null;
        }

        // Pilha de zero, do jeito que ItemData.Save grava: sem variante, sem artesao, sem dado extra.
        // Inventory.Load a traz de volta com stack 0 (AddItem(item, 0, x, y) clona com 0).
        public ChestItem Ghost(int prefabHash, int x, int y, int worldLevel, int quality)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                byte flags = (byte)(8 | 64 | (quality != 1 ? 4 : 0));
                writer.Write(10000);
                writer.Write((byte)x);
                writer.Write((byte)y);
                writer.Write((byte)worldLevel);
                writer.Write(flags);
                if (quality != 1)
                    writer.Write((ushort)quality);
                writer.Write((ushort)0);
                writer.Write(prefabHash);
                if (Version >= ChunksNCheats)
                    writer.Write((byte)0);
                writer.Flush();
                return new ChestItem
                {
                    PrefabHash = prefabHash, X = x, Y = y, WorldLevel = worldLevel, Quality = quality, Stack = 0,
                    Raw = stream.ToArray(),
                };
            }
        }
    }
}
