using System;

using Newtonsoft.Json;

namespace Server.Custom
{
    /// <summary>
    /// Data/Custom/bots.json, bound with MissingMemberHandling.Error like every other custom
    /// config, so a misspelled key is a loud failure rather than a silent default.
    /// </summary>
    public class BotStore : IValidatableConfig
    {
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("comment", NullValueHandling = NullValueHandling.Ignore)]
        public string Comment { get; set; }

        [JsonProperty("caps")]
        public BotCapsConfig Caps { get; set; }

        [JsonConstructor]
        public BotStore()
        {
            SchemaVersion = BotSystem.SchemaVersion;
            Caps = new BotCapsConfig();
        }

        public void Validate(ConfigErrors errors)
        {
            if (SchemaVersion > BotSystem.SchemaVersion)
            {
                errors.Add(
                    "schemaVersion {0} is newer than this build understands ({1}).",
                    SchemaVersion,
                    BotSystem.SchemaVersion);
            }

            if (Caps == null)
            {
                // A present-but-null "caps" key. Treat it as absent rather than failing: the
                // whole point of the section is that it is optional.
                Caps = new BotCapsConfig();
            }

            Caps.Validate(errors);
        }
    }

    /// <summary>
    /// The four caps a bot template is built against, every one optional.
    ///
    /// Absent means "whatever the shard caps players at" - read from Config/PlayerCaps.cfg, which
    /// is this shard's authoritative source and already carries the EJ values. Duplicating those
    /// numbers here would create a second source of truth that drifts silently the first time
    /// PlayerCaps.cfg is edited.
    ///
    /// A value present here deliberately diverges bots from players, which is a legitimate thing
    /// to want (a shard of deliberately weaker bots, say) - so it is allowed, but it is never
    /// silent: the Bots.Population health check names every override.
    /// </summary>
    public class BotCapsConfig : IValidatableConfig
    {
        [JsonProperty("skillCap", NullValueHandling = NullValueHandling.Ignore)]
        public double? SkillCap { get; set; }

        [JsonProperty("skillTotal", NullValueHandling = NullValueHandling.Ignore)]
        public double? SkillTotal { get; set; }

        [JsonProperty("statCap", NullValueHandling = NullValueHandling.Ignore)]
        public int? StatCap { get; set; }

        [JsonProperty("statTotal", NullValueHandling = NullValueHandling.Ignore)]
        public int? StatTotal { get; set; }

        [JsonConstructor]
        public BotCapsConfig()
        {
        }

        public void Validate(ConfigErrors errors)
        {
            if (SkillCap.HasValue && SkillCap.Value <= 0.0)
            {
                errors.Add("caps.skillCap must be positive (got {0}).", SkillCap.Value);
            }

            if (SkillTotal.HasValue && SkillTotal.Value <= 0.0)
            {
                errors.Add("caps.skillTotal must be positive (got {0}).", SkillTotal.Value);
            }

            if (StatCap.HasValue && StatCap.Value <= 0)
            {
                errors.Add("caps.statCap must be positive (got {0}).", StatCap.Value);
            }

            if (StatTotal.HasValue && StatTotal.Value <= 0)
            {
                errors.Add("caps.statTotal must be positive (got {0}).", StatTotal.Value);
            }

            // A per-skill cap above the total is not wrong so much as meaningless, and it is the
            // shape of a typo (a total typed into the per-skill slot).
            if (SkillCap.HasValue && SkillTotal.HasValue && SkillCap.Value > SkillTotal.Value)
            {
                errors.Add(
                    "caps.skillCap ({0}) exceeds caps.skillTotal ({1}); one skill cannot cap above the whole template.",
                    SkillCap.Value,
                    SkillTotal.Value);
            }

            if (StatCap.HasValue && StatTotal.HasValue && StatCap.Value > StatTotal.Value)
            {
                errors.Add(
                    "caps.statCap ({0}) exceeds caps.statTotal ({1}).",
                    StatCap.Value,
                    StatTotal.Value);
            }
        }
    }

    /// <summary>
    /// The effective caps, and where each one came from.
    ///
    /// Every number a bot template produces is scaled off these. There is no cap literal anywhere
    /// in BotSkillTemplate - that is the whole point of this type, and the reason the era switch
    /// from T2A to EJ is a config change rather than an edit.
    ///
    /// Rebuilt on every config load, so a [BotsReload picks up a PlayerCaps.cfg change too.
    /// </summary>
    public sealed class BotCaps
    {
        /// <summary>Highest value any single skill may reach.</summary>
        public double SkillCap { get; private set; }

        /// <summary>Highest sum across every skill in a template.</summary>
        public double SkillTotal { get; private set; }

        /// <summary>Highest value any single stat may reach.</summary>
        public int StatCap { get; private set; }

        /// <summary>Highest sum of Str + Dex + Int.</summary>
        public int StatTotal { get; private set; }

        public bool SkillCapOverridden { get; private set; }
        public bool SkillTotalOverridden { get; private set; }
        public bool StatCapOverridden { get; private set; }
        public bool StatTotalOverridden { get; private set; }

        public bool AnyOverridden
        {
            get { return SkillCapOverridden || SkillTotalOverridden || StatCapOverridden || StatTotalOverridden; }
        }

        private BotCaps()
        {
        }

        /// <summary>
        /// Resolve the caps: the shard's player caps, with any deliberate bots.json override
        /// applied on top.
        ///
        /// PlayerCaps.cfg stores skills at ten times their displayed value, because a Skill's
        /// Base is a double with one decimal place and its Cap is stored as a fixed-point int
        /// (Scripts/Misc/CharacterCreation.cs:211 divides by 10 for exactly this reason).
        /// </summary>
        public static BotCaps Resolve(BotCapsConfig config)
        {
            var caps = new BotCaps
            {
                SkillCap = Config.Get("PlayerCaps.SkillCap", 1000.0d) / 10.0,
                SkillTotal = Config.Get("PlayerCaps.TotalSkillCap", 7000.0d) / 10.0,
                StatCap = Config.Get("PlayerCaps.StrCap", 125),
                StatTotal = Config.Get("PlayerCaps.TotalStatCap", 225),
            };

            if (config != null)
            {
                if (config.SkillCap.HasValue)
                {
                    caps.SkillCap = config.SkillCap.Value;
                    caps.SkillCapOverridden = true;
                }

                if (config.SkillTotal.HasValue)
                {
                    caps.SkillTotal = config.SkillTotal.Value;
                    caps.SkillTotalOverridden = true;
                }

                if (config.StatCap.HasValue)
                {
                    caps.StatCap = config.StatCap.Value;
                    caps.StatCapOverridden = true;
                }

                if (config.StatTotal.HasValue)
                {
                    caps.StatTotal = config.StatTotal.Value;
                    caps.StatTotalOverridden = true;
                }
            }

            return caps;
        }

        /// <summary>
        /// The caps clause for the health line. Always present, so a divergence between bots and
        /// players can never be silent.
        /// </summary>
        public string Describe()
        {
            if (!AnyOverridden)
            {
                return String.Format(
                    "caps: from PlayerCaps.cfg (skill {0:0.#}/{1:0.#}, stat {2}/{3})",
                    SkillCap,
                    SkillTotal,
                    StatCap,
                    StatTotal);
            }

            var parts = new System.Collections.Generic.List<string>();

            if (SkillCapOverridden)
            {
                parts.Add(String.Format(
                    "skillCap {0:0.#} vs players {1:0.#}",
                    SkillCap,
                    Config.Get("PlayerCaps.SkillCap", 1000.0d) / 10.0));
            }

            if (SkillTotalOverridden)
            {
                parts.Add(String.Format(
                    "skillTotal {0:0.#} vs players {1:0.#}",
                    SkillTotal,
                    Config.Get("PlayerCaps.TotalSkillCap", 7000.0d) / 10.0));
            }

            if (StatCapOverridden)
            {
                parts.Add(String.Format(
                    "statCap {0} vs players {1}",
                    StatCap,
                    Config.Get("PlayerCaps.StrCap", 125)));
            }

            if (StatTotalOverridden)
            {
                parts.Add(String.Format(
                    "statTotal {0} vs players {1}",
                    StatTotal,
                    Config.Get("PlayerCaps.TotalStatCap", 225)));
            }

            return "caps: overridden (" + String.Join(", ", parts.ToArray()) + ")";
        }
    }
}
