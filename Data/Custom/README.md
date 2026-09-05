# Data/Custom

JSON configuration for custom systems, editable by the shard editor.

Conventions (each encodes a bug that actually shipped on the ModernUO shard):

- every config member carries an explicit `[JsonProperty("camelCase")]`
- flat int coordinates, not `Point3D`/`Rectangle2D` — the editor edits scalars
- a wrapper object, never a bare array, so a renamed key is a loud error
- validate before replacing the live config, collecting every problem rather than the first
- write with the compact one-object-per-line writer so editor saves diff cleanly
- resolve maps with a guarded `Map.Parse` — ServUO has no `Map.TryParse`
