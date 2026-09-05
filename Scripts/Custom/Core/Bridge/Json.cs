using System;
using System.Text;

namespace Server.Custom
{
    /// <summary>
    /// A JSON string quoter for the bridge's hand-built snapshots.
    ///
    /// Newtonsoft is available and is what JsonConfig uses, but these files are written on a
    /// two-second timer and are pure output - there is no model to bind, no schema to validate,
    /// and nothing to round-trip. Building the string directly avoids allocating an object graph
    /// per snapshot just to serialise it again.
    /// </summary>
    public static class Json
    {
        public static string Quote(string value)
        {
            if (value == null)
            {
                return "null";
            }

            var builder = new StringBuilder(value.Length + 2);

            builder.Append('"');

            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            builder.Append('"');

            return builder.ToString();
        }
    }
}
