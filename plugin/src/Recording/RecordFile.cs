using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace ValheimMetrics.Recording
{
    // Compresses and writes on its own thread so the game thread only hands arrays over.
    // When the disk falls behind, whole chunks are dropped instead of stalling the game: chunks
    // hold whole records, so the file stays readable and the gap is counted.
    sealed class RecordFile
    {
        public const int ChunkBytes = 256 * 1024;
        const double FlushSeconds = 5;

        struct Chunk
        {
            public byte[] Data;
            public int Length;
        }

        readonly ConcurrentQueue<Chunk> _queue = new ConcurrentQueue<Chunk>();
        readonly ConcurrentBag<byte[]> _pool = new ConcurrentBag<byte[]>();
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        readonly long _maxQueuedBytes;
        readonly Thread _thread;
        readonly Stream _file;
        volatile bool _closing;
        long _queuedBytes;
        long _compressedBytes;

        public string Path { get; }
        public long WrittenBytes { get; private set; }
        public long DroppedBytes { get; private set; }
        public long QueuedBytes => Interlocked.Read(ref _queuedBytes);
        public long CompressedBytes => Interlocked.Read(ref _compressedBytes);
        public bool Finished { get; private set; }
        public Exception Error { get; private set; }

        public RecordFile(string path, long maxQueuedBytes)
        {
            Path = path;
            _maxQueuedBytes = maxQueuedBytes;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            _file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            _thread = new Thread(Run) { IsBackground = true, Name = "recorder", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public byte[] Rent() => _pool.TryTake(out var b) ? b : new byte[ChunkBytes];

        // Takes ownership of `data`. False = dropped (queue full or file failed).
        public bool Submit(byte[] data, int length)
        {
            if (length == 0)
            {
                _pool.Add(data);
                return true;
            }
            if (Error != null || _closing || QueuedBytes + length > _maxQueuedBytes)
            {
                DroppedBytes += length;
                _pool.Add(data);
                return false;
            }
            WrittenBytes += length;
            Interlocked.Add(ref _queuedBytes, length);
            _queue.Enqueue(new Chunk { Data = data, Length = length });
            _wake.Set();
            return true;
        }

        // Does not wait: the thread drains the queue, closes the file and sets Finished.
        public void Close()
        {
            _closing = true;
            _wake.Set();
        }

        // Tests and shutdown only.
        public bool Wait(int milliseconds) => _thread.Join(milliseconds);

        void Run()
        {
            Stream gzip = null;
            try
            {
                gzip = new GZipStream(_file, CompressionLevel.Fastest, leaveOpen: true);
                var sinceFlush = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    bool closing = _closing;
                    while (_queue.TryDequeue(out var chunk))
                    {
                        gzip.Write(chunk.Data, 0, chunk.Length);
                        Interlocked.Add(ref _queuedBytes, -chunk.Length);
                        _pool.Add(chunk.Data);
                    }
                    if (closing)
                        break;
                    // A crash or kill loses at most the last few seconds, not the whole deflate window.
                    if (sinceFlush.Elapsed.TotalSeconds >= FlushSeconds)
                    {
                        gzip.Flush();
                        _file.Flush();
                        sinceFlush.Restart();
                    }
                    Interlocked.Exchange(ref _compressedBytes, _file.Position);
                    _wake.WaitOne(1000);
                }
            }
            catch (Exception e)
            {
                Error = e;
            }
            finally
            {
                try
                {
                    gzip?.Dispose();
                    Interlocked.Exchange(ref _compressedBytes, _file.Position);
                    _file.Dispose();
                }
                catch (Exception e)
                {
                    Error = Error ?? e;
                }
                Finished = true;
            }
        }
    }
}
