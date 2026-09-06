# Data/Custom/BotChat — the bot chat corpus

**Copied unchanged** from the PlayerBots system in
[`Klein187/uo-offline`](https://github.com/Klein187/uo-offline), whose
`Distribution/Data/PlayerBotChat/` this is a byte-for-byte copy of. 123 `.txt` files here plus
22 in `Gossip/`; 1,724 lines.

## Licence

**GNU General Public License, version 3.** See [`LICENSE-BOTS`](../../../LICENSE-BOTS) at the
repository root, which covers `Scripts/Custom/Bots/` and this data with it.

> **Read this before "correcting" the licence.** The `LICENSE` file at the root of the
> `uo-offline` repository is **MIT, Copyright (c) 2026 Mike** — and it does *not* apply here. That
> file covers the **installer**; its own text says so, listing the third-party software the
> installer downloads. The PlayerBots system is licensed separately, in that project's README:
>
> > The PlayerBots system was built for this project. GPL-3.0.
>
> So the corpus and the code derived from it are GPL-3.0. This note exists because the root MIT
> file is the first thing you find, and it makes the GPL-3 notice look like a mistake. It is not.

## Two structural rules travel with this data

**1. The scan is non-recursive.** `ChatLibrary` enumerates `*.txt` in *this* directory only.
`Gossip/` is a subdirectory precisely so its files are never picked up as ambient chatter — those
are `{actor}`/`{other}`/`{place}`/`{when}` templates, and a flat scan would have a bot say
`{other} killed {actor} at {place}` out loud, verbatim. Do not "fix" the loader to recurse.

**2. The `_self` pairing is load-bearing.** Seven `Gossip/` files come in pairs — `pk.txt` with
`pk_self.txt`, and likewise for `death`, `duel`, `faction`, `find`, `kill`, `party`, `tame`. The
`_self` half is the same event told by the bot it happened *to*, in the first person. Renaming
either half silently breaks the pairing.

## Format

One utterance per line. Blank lines and lines starting with `#` are skipped; the rest of a file's
header comments are the original author's notes on that category and are worth reading before
editing. The filename without its extension is the category name, matched case-insensitively.

A line starting with `*` is **rejected at load** and reported by `Bots.Chat`. Upstream routed
those to `Emote()`; this shard has no emote path and would say the asterisks aloud. No shipped
line starts with one.

`{token}` placeholders are resolved by `ChatTokens`, which knows every token in this corpus and
which session owns it. A line whose tokens cannot all be resolved yet is counted as **reserved**
and is *never spoken* — so the economy, taming, adventurer and event-journal lines sit here
harmlessly until the sessions that own them land. See `Scripts/Custom/Bots/README.md`.

## Editing

`[BotsReload` re-reads this directory; no restart and no recompile. Roughly 18 files carry
T2A-flavoured content that would want an editing pass for an EJ shard — that is a deliberate
later data pass, and nothing in the format is era-bound.
