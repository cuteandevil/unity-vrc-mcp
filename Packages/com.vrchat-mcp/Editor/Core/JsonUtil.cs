using System.Text;

namespace VrcMcp.Core
{
    /// <summary>Tiny JSON builder helpers (no external deps).</summary>
    public static class JsonUtil
    {
        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public static void WriteString(StringBuilder sb, string name, string value)
        {
            sb.Append('"').Append(Escape(name)).Append("\":\"").Append(Escape(value)).Append('"');
        }

        public static void WriteFloatArray(StringBuilder sb, string name, float x, float y, float z)
        {
            sb.Append('"').Append(Escape(name)).Append("\":[").Append(x.ToString("R")).Append(',')
              .Append(y.ToString("R")).Append(',').Append(z.ToString("R")).Append(']');
        }
    }
}