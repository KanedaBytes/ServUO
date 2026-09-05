# Custom/Zones

Restricted zones: entering one starts a countdown gump, and at zero the player is handed to the
jail system.

Ported from ModernUO `Custom/Zones/`. Config lives in `Data/Custom/restricted-zones.json`.

Notes carried over from the original: resolve the parent region at the zone centre so a zone
layers on top of town rules; leave the region name null so it is not added to `Map.Regions`;
handle `OnResurrect`, because death fires no region change and dying at 29 seconds would
otherwise be an exploit.
