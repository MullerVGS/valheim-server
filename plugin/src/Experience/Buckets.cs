namespace ValheimMetrics.Experience
{
    public static class Buckets
    {
        public static readonly double[] Delay = { 0.025, 0.05, 0.1, 0.2, 0.3, 0.5, 1, 2, 5 };
        public static readonly double[] Gap = { 0.1, 0.2, 0.3, 0.5, 1, 2, 5, 10 };
        public static readonly double[] Action = { 0.1, 0.2, 0.3, 0.5, 1, 2, 3, 5, 10, 30 };
    }
}
