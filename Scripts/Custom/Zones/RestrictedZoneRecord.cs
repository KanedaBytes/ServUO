using System;
using System.Collections.Generic;

using Newtonsoft.Json;

namespace Server.Custom
{
    /// <summary>
    /// Root of Data/Custom/restricted-zones.json.
    ///
    /// A wrapper object rather than a bare array on purpose: if the root key is ever renamed,
    /// binding fails loudly instead of silently producing zero zones.
    /// </summary>
    public class RestrictedZoneStore : IValidatableConfig
    {
        [JsonProperty("zones")]
        public List<RestrictedZoneRecord> Zones { get; set; }

        public void Validate(ConfigErrors errors)
        {
            if (Zones == null)
            {
                errors.Add("'zones' section is missing");
                return;
            }

            // Case-insensitive, because Find() matches names case-insensitively - two zones
            // differing only in case would be indistinguishable to every command.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < Zones.Count; i++)
            {
                RestrictedZoneRecord record = Zones[i];
                string where = String.Format("zones[{0}]", i);

                if (record == null)
                {
                    errors.Add(where + " is null");
                    continue;
                }

                if (String.IsNullOrWhiteSpace(record.Name))
                {
                    errors.Add(where + " has no name");
                }
                else if (!seen.Add(record.Name))
                {
                    errors.Add("{0} duplicates the name '{1}'", where, record.Name);
                }

                if (record.Map == null)
                {
                    errors.Add(
                        "{0} map '{1}' is not a valid facet. Valid: {2}",
                        where,
                        record.MapName,
                        JsonConfig.ValidMapNames());
                }

                if (record.Width <= 0 || record.Height <= 0)
                {
                    errors.Add(
                        "{0} bounds must have a positive width and height (found {1}x{2})",
                        where,
                        record.Width,
                        record.Height);
                }
            }
        }
    }

    /// <summary>
    /// One restricted zone.
    ///
    /// The schema is byte-identical to the ModernUO shard's so the map editor needs no change:
    /// flat integer coordinates (there is no Rectangle2D JSON converter on either shard), and
    /// x/y/width/height as origin-plus-size, NOT a corner pair.
    /// </summary>
    public class RestrictedZoneRecord
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("map")]
        public string MapName { get; set; }

        [JsonProperty("x")]
        public int X { get; set; }

        [JsonProperty("y")]
        public int Y { get; set; }

        [JsonProperty("width")]
        public int Width { get; set; }

        [JsonProperty("height")]
        public int Height { get; set; }

        [JsonConstructor]
        public RestrictedZoneRecord()
        {
            // Applied before the deserializer sets properties, so an absent "map" key defaults
            // to the shard's primary facet rather than to null.
            MapName = "Trammel";
        }

        public RestrictedZoneRecord(string name, Map map, Rectangle2D bounds)
        {
            Name = name;
            MapName = map != null ? map.Name : null;
            X = bounds.X;
            Y = bounds.Y;
            Width = bounds.Width;
            Height = bounds.Height;
        }

        /// <summary>
        /// Null when the facet name is unrecognised. Resolved through JsonConfig.TryParseMap
        /// rather than Map.Parse, which throws ArgumentException on bad input - a property
        /// getter must never throw.
        /// </summary>
        [JsonIgnore]
        public Map Map
        {
            get
            {
                Map map;
                return JsonConfig.TryParseMap(MapName, out map) ? map : null;
            }
        }

        [JsonIgnore]
        public Rectangle2D Bounds
        {
            get { return new Rectangle2D(X, Y, Width, Height); }
        }

        public override string ToString()
        {
            return String.Format("{0}: {1}, {2}x{3} at {4}, {5}", Name, MapName, Width, Height, X, Y);
        }
    }
}
