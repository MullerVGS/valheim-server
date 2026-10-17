using System;
using System.IO;
using System.Text;

namespace ValheimMetrics.Recording
{
    // A recording is a gzip stream: "VREC", u16 format version, then records until the end.
    // Each record: u8 kind, u32 milliseconds since the session started, u32 payload length, payload.
    // Little endian throughout. Readers skip kinds they do not know, so new kinds stay compatible.
    //
    // Payloads (strings are u16 byte count + UTF-8; ZDO ids are i64 user + u32 id):
    //   Meta        str plugin, str game, str world, str label, str reason, str started (UTC, ISO 8601),
    //               f64 world time, f32 radius
    //   PrefabName  i32 hash, str name
    //   MethodName  i32 hash, str name
    //   Peer        i64 uid, str name, str host, zdoid character
    //   Zdo         zdoid, u16 owner revision, u32 data revision, i64 owner, f32 x, f32 y, f32 z,
    //               u32 length + ZDO.Serialize bytes (prefab, rotation and every field)
    //   Snapshot    same as Zdo: state of what was already in the area when it entered the recording
    //   Routed      i64 sender, i64 target peer, zdoid target, i32 method, u32 length + parameters
    //   World       f64 world time, u16 count, then per peer: i64 uid, f32 x, f32 y, f32 z (reference position)
    //   Marker      str text
    enum RecordKind : byte
    {
        Meta = 1,
        PrefabName = 2,
        MethodName = 3,
        Peer = 4,
        Zdo = 5,
        Snapshot = 6,
        Routed = 7,
        World = 8,
        Marker = 9,
    }

    static class RecordFormat
    {
        public const ushort Version = 1;
        public const int RecordHeaderBytes = 9;
        public static readonly byte[] Magic = { (byte)'V', (byte)'R', (byte)'E', (byte)'C' };
        public const int KindCount = 10;
    }

    // Appends records to a byte array that the file writer takes over whole; no allocation once warm.
    sealed class RecordBuffer
    {
        byte[] _data;
        int _length;
        int _lengthAt = -1;

        public RecordBuffer(int capacity) => _data = new byte[capacity];

        public int Length => _length;
        public byte[] Data => _data;

        // Hands the filled array over and continues on `fresh` (empty).
        public byte[] Swap(byte[] fresh)
        {
            if (_lengthAt >= 0)
                throw new InvalidOperationException("Swap inside a record.");
            var full = _data;
            _data = fresh;
            _length = 0;
            return full;
        }

        public void Header()
        {
            Bytes(RecordFormat.Magic, 0, RecordFormat.Magic.Length);
            U16(RecordFormat.Version);
        }

        public void Begin(RecordKind kind, uint ms)
        {
            if (_lengthAt >= 0)
                throw new InvalidOperationException("Record already open.");
            U8((byte)kind);
            U32(ms);
            _lengthAt = _length;
            U32(0);
        }

        public void End()
        {
            if (_lengthAt < 0)
                throw new InvalidOperationException("No record open.");
            Put32(_lengthAt, (uint)(_length - _lengthAt - 4));
            _lengthAt = -1;
        }

        // Drops a record left open by an exception, so the stream stays parseable.
        public void Abort(int at)
        {
            _length = at;
            _lengthAt = -1;
        }

        public void U8(byte v)
        {
            Ensure(1);
            _data[_length++] = v;
        }

        public void U16(ushort v)
        {
            Ensure(2);
            _data[_length++] = (byte)v;
            _data[_length++] = (byte)(v >> 8);
        }

        public void U32(uint v)
        {
            Ensure(4);
            Put32(_length, v);
            _length += 4;
        }

        public void I32(int v) => U32((uint)v);

        public void I64(long v)
        {
            U32((uint)v);
            U32((uint)((ulong)v >> 32));
        }

        public void F32(float v) => I32(BitConverter.SingleToInt32Bits(v));

        public void F64(double v) => I64(BitConverter.DoubleToInt64Bits(v));

        public void Str(string s)
        {
            s = s ?? "";
            int max = Encoding.UTF8.GetMaxByteCount(s.Length);
            Ensure(2 + max);
            int count = Encoding.UTF8.GetBytes(s, 0, s.Length, _data, _length + 2);
            if (count > ushort.MaxValue)
                throw new ArgumentException("String too long for a record.");
            _data[_length] = (byte)count;
            _data[_length + 1] = (byte)(count >> 8);
            _length += 2 + count;
        }

        public void Blob(byte[] src, int count)
        {
            U32((uint)count);
            Bytes(src, 0, count);
        }

        public void Bytes(byte[] src, int offset, int count)
        {
            Ensure(count);
            Buffer.BlockCopy(src, offset, _data, _length, count);
            _length += count;
        }

        void Put32(int at, uint v)
        {
            _data[at] = (byte)v;
            _data[at + 1] = (byte)(v >> 8);
            _data[at + 2] = (byte)(v >> 16);
            _data[at + 3] = (byte)(v >> 24);
        }

        void Ensure(int more)
        {
            if (_length + more <= _data.Length)
                return;
            var grown = new byte[Math.Max(_data.Length * 2, _length + more)];
            Buffer.BlockCopy(_data, 0, grown, 0, _length);
            _data = grown;
        }
    }

    // Reads a recording back (tests, inspection and the replay mod).
    sealed class RecordReader
    {
        readonly Stream _input;
        readonly byte[] _head = new byte[RecordFormat.RecordHeaderBytes];

        public ushort Version { get; }

        public RecordReader(Stream input)
        {
            _input = input;
            var start = new byte[6];
            if (!Fill(start, 6))
                throw new InvalidDataException("Empty recording.");
            for (int i = 0; i < 4; i++)
            {
                if (start[i] != RecordFormat.Magic[i])
                    throw new InvalidDataException("Not a recording.");
            }
            Version = (ushort)(start[4] | start[5] << 8);
            if (Version > RecordFormat.Version)
                throw new InvalidDataException($"Recording format {Version} is newer than {RecordFormat.Version}.");
        }

        // False at the end. A record cut short (server stopped mid-write) also ends the stream.
        public bool Next(out RecordKind kind, out uint ms, out byte[] payload)
        {
            kind = 0;
            ms = 0;
            payload = null;
            if (!Fill(_head, _head.Length))
                return false;
            kind = (RecordKind)_head[0];
            ms = Payload.Read32(_head, 1);
            uint length = Payload.Read32(_head, 5);
            payload = new byte[length];
            return Fill(payload, (int)length);
        }

        bool Fill(byte[] into, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n;
                try
                {
                    n = _input.Read(into, read, count - read);
                }
                catch (EndOfStreamException)
                {
                    return false;
                }
                catch (InvalidDataException)
                {
                    return false;
                }
                if (n <= 0)
                    return false;
                read += n;
            }
            return true;
        }
    }

    // Cursor over one record's payload.
    sealed class Payload
    {
        readonly byte[] _data;
        int _at;

        public Payload(byte[] data) => _data = data;

        public int Remaining => _data.Length - _at;

        public byte U8() => _data[_at++];

        public ushort U16()
        {
            var v = (ushort)(_data[_at] | _data[_at + 1] << 8);
            _at += 2;
            return v;
        }

        public uint U32()
        {
            var v = Read32(_data, _at);
            _at += 4;
            return v;
        }

        public int I32() => (int)U32();

        public long I64()
        {
            ulong lo = U32();
            ulong hi = U32();
            return (long)(lo | hi << 32);
        }

        public float F32() => BitConverter.Int32BitsToSingle(I32());

        public double F64() => BitConverter.Int64BitsToDouble(I64());

        public string Str()
        {
            int count = U16();
            var s = Encoding.UTF8.GetString(_data, _at, count);
            _at += count;
            return s;
        }

        public byte[] Blob()
        {
            int count = (int)U32();
            var b = new byte[count];
            Buffer.BlockCopy(_data, _at, b, 0, count);
            _at += count;
            return b;
        }

        internal static uint Read32(byte[] d, int at) =>
            (uint)(d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24);
    }
}
