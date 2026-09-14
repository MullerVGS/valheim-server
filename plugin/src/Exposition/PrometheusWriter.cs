using System.Globalization;
using System.Text;

namespace ValheimMetrics.Exposition
{
    // Formato texto do Prometheus (0.0.4). Um writer por snapshot, reaproveitado via Reset.
    public sealed class PrometheusWriter
    {
        readonly StringBuilder _sb = new StringBuilder(32 * 1024);

        public void Reset() => _sb.Clear();

        public override string ToString() => _sb.ToString();

        public void Family(string name, string type, string help)
        {
            _sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            _sb.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
        }

        // labels: pares nome, valor.
        public void Sample(string name, double value, params string[] labels)
        {
            _sb.Append(name);
            AppendLabels(labels, null);
            _sb.Append(' ');
            AppendValue(value);
            _sb.Append('\n');
        }

        public void Histogram(string name, Histogram h, params string[] labels)
        {
            var bounds = h.Bounds;
            long cumulative = 0;
            for (int i = 0; i < bounds.Length; i++)
            {
                cumulative += h.BucketCount(i);
                _sb.Append(name).Append("_bucket");
                AppendLabels(labels, Format(bounds[i]));
                _sb.Append(' ').Append(cumulative.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            _sb.Append(name).Append("_bucket");
            AppendLabels(labels, "+Inf");
            _sb.Append(' ').Append(h.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');

            _sb.Append(name).Append("_sum");
            AppendLabels(labels, null);
            _sb.Append(' ');
            AppendValue(h.Sum);
            _sb.Append('\n');

            _sb.Append(name).Append("_count");
            AppendLabels(labels, null);
            _sb.Append(' ').Append(h.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        void AppendLabels(string[] labels, string le)
        {
            if (labels.Length == 0 && le == null)
                return;
            _sb.Append('{');
            for (int i = 0; i + 1 < labels.Length; i += 2)
            {
                if (i > 0)
                    _sb.Append(',');
                _sb.Append(labels[i]).Append("=\"");
                AppendEscaped(labels[i + 1]);
                _sb.Append('"');
            }
            if (le != null)
            {
                if (labels.Length > 0)
                    _sb.Append(',');
                _sb.Append("le=\"").Append(le).Append('"');
            }
            _sb.Append('}');
        }

        void AppendEscaped(string value)
        {
            foreach (var c in value ?? "")
            {
                switch (c)
                {
                    case '\\': _sb.Append("\\\\"); break;
                    case '"': _sb.Append("\\\""); break;
                    case '\n': _sb.Append("\\n"); break;
                    default: _sb.Append(c); break;
                }
            }
        }

        void AppendValue(double v) => _sb.Append(Format(v));

        static string Format(double v)
        {
            if (double.IsNaN(v)) return "NaN";
            if (double.IsPositiveInfinity(v)) return "+Inf";
            if (double.IsNegativeInfinity(v)) return "-Inf";
            return v.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
