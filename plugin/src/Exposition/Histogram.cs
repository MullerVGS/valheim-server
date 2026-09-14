namespace ValheimMetrics.Exposition
{
    // Contagem por bucket nao cumulativa; o writer acumula. Observe nao aloca.
    public sealed class Histogram
    {
        readonly double[] _bounds;
        readonly long[] _counts;

        public Histogram(double[] bounds)
        {
            _bounds = bounds;
            _counts = new long[bounds.Length];
        }

        public double[] Bounds => _bounds;
        public long Count { get; private set; }
        public double Sum { get; private set; }

        public long BucketCount(int i) => _counts[i];

        public void Observe(double value)
        {
            Count++;
            Sum += value;
            for (int i = 0; i < _bounds.Length; i++)
            {
                if (value <= _bounds[i])
                {
                    _counts[i]++;
                    return;
                }
            }
        }
    }
}
