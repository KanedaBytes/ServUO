# Custom/Jail

The jail administration layer. ServUO already provides the jail *region* and its rules
(`Scripts/Regions/Jail.cs`, `Data/Regions.xml:699` and `:1770`) plus ten subsystems that react
to it — see CLAUDE.md §13. What is missing, and what lives here, is: the `[Jail` / `[Unjail`
commands, sentence records and escalation, the release timer, persistence, and the status gump.

Behaviour contract carried over from ModernUO:

- re-jailing an active prisoner must stop the existing timer before replacing it
- no private "currently being jailed" latch that can stick after a restart
- a sentence that expired while the server was down releases the player on login
- release to the origin facet, clamped to Felucca/Trammel, falling back to Trammel
- a public `GetJailEndTime` accessor, which the status gump needs
