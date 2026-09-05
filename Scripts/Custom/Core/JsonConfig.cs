using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Server.Custom
{
    /// <summary>Collects every problem found while validating a config, not just the first.</summary>
    public sealed class ConfigErrors
    {
        private readonly List<string> _errors = new List<string>();

        public int Count { get { return _errors.Count; } }

        public bool HasErrors { get { return _errors.Count > 0; } }

        public IList<string> Items { get { return _errors.AsReadOnly(); } }

        public void Add(string message)
        {
            if (!String.IsNullOrWhiteSpace(message))
            {
                _errors.Add(message);
            }
        }

        public void Add(string format, params object[] args)
        {
            try
            {
                Add(String.Format(format, args));
            }
            catch
            {
                Add(format);
            }
        }
    }

    /// <summary>
    /// Implement on a config root so JsonConfig validates it after binding. Report every
    /// problem you find rather than returning at the first - a config with three mistakes
    /// should take one edit to fix, not three restarts.
    /// </summary>
    public interface IValidatableConfig
    {
        void Validate(ConfigErrors errors);
    }

    /// <summary>
    /// JSON configuration for custom systems, backed by Newtonsoft.Json 13.0.3.
    /// ServUO ships no JSON support at all and net48 has no built-in System.Text.Json, so this
    /// is the shard's only JSON entry point.
    ///
    /// Conventions (each encodes a bug that actually shipped on the ModernUO shard):
    ///   - every config member carries an explicit [JsonProperty("camelCase")]
    ///   - a wrapper object, never a bare array, so a renamed root key is a loud error
    ///   - unknown/misspelled keys are errors, not silence (MissingMemberHandling.Error)
    ///   - validation collects every problem before the config is accepted
    ///   - files are written one-object-per-line so an editor save is a minimal diff
    /// </summary>
    public static class JsonConfig
    {
        private static readonly CustomLogger Log = CustomLogger.For("JsonConfig");

        private static readonly string[] NoErrors = new string[0];

        /// <summary>
        /// Binds a JSON file to <typeparamref name="T"/> and validates it.
        /// Returns false with every error collected; <paramref name="result"/> is then null and
        /// the caller must keep whatever config it already had.
        /// </summary>
        public static bool TryLoad<T>(string relativePath, out T result, out IList<string> errors)
            where T : class
        {
            result = null;
            errors = NoErrors;

            string fullPath = Resolve(relativePath);
            var collected = new List<string>();

            if (!File.Exists(fullPath))
            {
                collected.Add("File not found: " + relativePath);
                errors = collected;
                return false;
            }

            string text;

            try
            {
                text = File.ReadAllText(fullPath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                collected.Add("Could not read " + relativePath + ": " + ex.Message);
                errors = collected;
                return false;
            }

            T bound;

            try
            {
                bound = JsonConvert.DeserializeObject<T>(text, CreateSettings());
            }
            catch (JsonException ex)
            {
                // MissingMemberHandling.Error turns a misspelled or unknown key into this,
                // rather than binding nothing and leaving the feature silently inert.
                collected.Add("Invalid JSON in " + relativePath + ": " + ex.Message);
                errors = collected;
                return false;
            }
            catch (Exception ex)
            {
                collected.Add("Could not parse " + relativePath + ": " + ex.Message);
                errors = collected;
                return false;
            }

            if (bound == null)
            {
                collected.Add(relativePath + " is empty or contains only 'null'.");
                errors = collected;
                return false;
            }

            var validatable = bound as IValidatableConfig;

            if (validatable != null)
            {
                var configErrors = new ConfigErrors();

                try
                {
                    validatable.Validate(configErrors);
                }
                catch (Exception ex)
                {
                    configErrors.Add("Validation threw {0}: {1}", ex.GetType().Name, ex.Message);
                }

                if (configErrors.HasErrors)
                {
                    foreach (string error in configErrors.Items)
                    {
                        collected.Add(relativePath + ": " + error);
                    }

                    errors = collected;
                    return false;
                }
            }

            result = bound;
            return true;
        }

        /// <summary>Writes a config using the compact one-object-per-line layout.</summary>
        public static bool TrySave<T>(string relativePath, T value, out string error)
        {
            error = null;

            if (value == null)
            {
                error = "Nothing to save.";
                return false;
            }

            try
            {
                JToken token = JToken.FromObject(value, JsonSerializer.Create(CreateSettings()));
                return TrySaveToken(relativePath, token, out error);
            }
            catch (Exception ex)
            {
                error = "Could not serialize " + relativePath + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Loads a file as a raw JToken tree. Edits applied to the tree and written back with
        /// TrySaveToken preserve keys this shard does not model - which is what lets the shard
        /// editor patch one field without discarding the rest of the file.
        /// </summary>
        public static bool TryLoadToken(string relativePath, out JToken token, out string error)
        {
            token = null;
            error = null;

            string fullPath = Resolve(relativePath);

            if (!File.Exists(fullPath))
            {
                error = "File not found: " + relativePath;
                return false;
            }

            try
            {
                token = JToken.Parse(File.ReadAllText(fullPath, Encoding.UTF8));
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not parse " + relativePath + ": " + ex.Message;
                return false;
            }
        }

        public static bool TrySaveToken(string relativePath, JToken token, out string error)
        {
            error = null;

            if (token == null)
            {
                error = "Nothing to save.";
                return false;
            }

            string fullPath = Resolve(relativePath);

            try
            {
                string directory = Path.GetDirectoryName(fullPath);

                if (!String.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(fullPath, SerializeCompact(token), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not write " + relativePath + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Resolves a facet name from config input.
        ///
        /// Never use Map.Parse for this: the compiled overload (Server/Map.cs, the #else branch)
        /// throws ArgumentException on an unrecognised name rather than returning null.
        ///
        /// Maps are registered by MapDefinitions.Configure() at [CallPriority] 0, so any config
        /// resolving map names must load at a higher priority than that.
        /// </summary>
        public static bool TryParseMap(string name, out Map map)
        {
            map = null;

            if (String.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            name = name.Trim();

            foreach (Map candidate in Map.AllMaps)
            {
                if (candidate == null || candidate == Map.Internal)
                {
                    continue;
                }

                if (Insensitive.Equals(candidate.Name, name))
                {
                    map = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>The facet names TryParseMap accepts, for error messages.</summary>
        public static string ValidMapNames()
        {
            var names = new List<string>();

            foreach (Map candidate in Map.AllMaps)
            {
                if (candidate != null && candidate != Map.Internal)
                {
                    names.Add(candidate.Name);
                }
            }

            return String.Join(", ", names.ToArray());
        }

        public static string Resolve(string relativePath)
        {
            return Path.Combine(Core.BaseDirectory, relativePath);
        }

        private static JsonSerializerSettings CreateSettings()
        {
            return new JsonSerializerSettings
            {
                // An unknown or misspelled key is an error, not silence. On the ModernUO shard a
                // case mismatch left an entire feature inert with an empty Britain as the only
                // symptom; this is the guard against a repeat.
                MissingMemberHandling = MissingMemberHandling.Error,
                NullValueHandling = NullValueHandling.Include,
                DateParseHandling = DateParseHandling.None,
                Formatting = Formatting.None
            };
        }

        /// <summary>
        /// Compact layout: any object or array whose children are all scalars is written on one
        /// line; anything deeper is expanded. An array of flat objects therefore renders one
        /// object per line, which is how these files were hand-authored and what keeps an
        /// editor save to a minimal diff instead of a whole-file rewrite.
        /// </summary>
        public static string SerializeCompact(JToken token)
        {
            var sb = new StringBuilder();
            WriteToken(token, sb, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        private static void WriteToken(JToken token, StringBuilder sb, int indent)
        {
            if (token == null)
            {
                sb.Append("null");
                return;
            }

            var container = token as JContainer;

            if (container == null || IsFlat(container))
            {
                sb.Append(token.ToString(Formatting.None));
                return;
            }

            var obj = token as JObject;

            if (obj != null)
            {
                sb.Append("{\n");

                int i = 0;
                int last = obj.Count - 1;

                foreach (JProperty property in obj.Properties())
                {
                    Indent(sb, indent + 1);
                    sb.Append(JsonConvert.ToString(property.Name));
                    sb.Append(": ");
                    WriteToken(property.Value, sb, indent + 1);

                    if (i++ < last)
                    {
                        sb.Append(',');
                    }

                    sb.Append('\n');
                }

                Indent(sb, indent);
                sb.Append('}');
                return;
            }

            var array = token as JArray;

            if (array != null)
            {
                sb.Append("[\n");

                int i = 0;
                int last = array.Count - 1;

                foreach (JToken item in array)
                {
                    Indent(sb, indent + 1);
                    WriteToken(item, sb, indent + 1);

                    if (i++ < last)
                    {
                        sb.Append(',');
                    }

                    sb.Append('\n');
                }

                Indent(sb, indent);
                sb.Append(']');
                return;
            }

            sb.Append(token.ToString(Formatting.None));
        }

        /// <summary>True when every child is a scalar, so the container fits on one line.</summary>
        private static bool IsFlat(JContainer container)
        {
            var obj = container as JObject;

            if (obj != null)
            {
                foreach (JProperty property in obj.Properties())
                {
                    if (!(property.Value is JValue))
                    {
                        return false;
                    }
                }

                return true;
            }

            var array = container as JArray;

            if (array != null)
            {
                foreach (JToken item in array)
                {
                    if (!(item is JValue))
                    {
                        return false;
                    }
                }

                return true;
            }

            return false;
        }

        private static void Indent(StringBuilder sb, int levels)
        {
            sb.Append(' ', levels * 2);
        }
    }
}
