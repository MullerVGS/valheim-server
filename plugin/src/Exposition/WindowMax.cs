using System;

namespace ValheimMetrics.Exposition
{
    // Maior valor nos ultimos N segundos inteiros, em anel de baldes de 1s. Add nao aloca.
    public sealed class WindowMax
    {
        readonly long[] _second;
        readonly double[] _max;

        public WindowMax(int windowSeconds)
        {
            _second = new long[windowSeconds];
            _max = new double[windowSeconds];
            for (int i = 0; i < windowSeconds; i++)
                _second[i] = long.MinValue;
        }

        public void Add(double now, double value)
        {
            long s = (long)Math.Floor(now);
            int i = (int)(((s % _second.Length) + _second.Length) % _second.Length);
            if (_second[i] != s)
            {
                _second[i] = s;
                _max[i] = value;
            }
            else if (value > _max[i])
            {
                _max[i] = value;
            }
        }

        public double Max(double now)
        {
            long s = (long)Math.Floor(now);
            double max = 0;
            for (int i = 0; i < _second.Length; i++)
            {
                if (_second[i] > s - _second.Length && _second[i] <= s && _max[i] > max)
                    max = _max[i];
            }
            return max;
        }
    }
}
