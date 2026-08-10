# Building a language pack

Gubal Library ships with no text in it. This document is the contract: produce a pack that satisfies
it and the plugin will serve it, whatever built it and whatever language it targets.

The plugin has no notion of Spanish, or of any particular language. `language` is a field in the
manifest, shown in the settings window and otherwise unused.

**No language pack is distributed from this repository.** The text is Square Enix's, and a
translation of it is a derivative work; the plugin itself reproduces none of it. Where a pack comes
from is its author's business, not the plugin's.

## What a pack is

The game keeps its text in Excel sheets inside its archives — `.exh` headers and `.exd` pages. A
language pack is **those same pages, rebuilt with your text inside them**, plus a manifest describing
what they are.

```
whatever-you-called-it/
  gubal-manifest.json
  exd/
    ContentFinderConditionTransient_0_en.exd
    Quest_65536_en.exd
    quest/041/AktKba101_04102_0_en.exd
    custom/000/RegSeaAetheGuid_00051_0_en.exd
    ...
```

The plugin walks the folder for `*.exd` and derives each game path from the path relative to the
root, so the layout must mirror the archive exactly. A zip of that folder's **contents** — not of the
folder itself — is a distributable pack.

Two consequences worth stating plainly, because neither is obvious:

**The game has no Spanish slot, or Italian, or Polish.** It knows `ja`, `en`, `de`, `fr`. A pack
therefore overwrites one of those, and `_en` is the usual choice. Rows you have not translated keep
the original text, so partial coverage costs nothing and needs no fallback logic — but the language
you replaced is genuinely gone while the pack is on.

**A page is all-or-nothing.** If a rebuilt page is wrong, that whole sheet is wrong. Whatever builds
your pack should reproduce the game's own bytes exactly when it substitutes nothing, and verify that
before it substitutes anything.

## The manifest

`gubal-manifest.json` at the root of the pack, UTF-8.

```json
{
  "name": "FFXIV Language Pack (ES)",
  "language": "es",
  "languageName": "Español (España)",
  "author": "ashdam",
  "updateUrl": "https://example.org/packs/es/latest.json",
  "translationVersion": "2026.08.08.1756",
  "gameVersion": "2026.08.05.0000.0000",
  "pages": 3414,
  "lines": 95628,
  "rows": 405771
}
```

| Field | Required | What it does |
|---|---|---|
| `gameVersion` | **yes** | The patch the pages were built from. **The plugin refuses to serve the pack when this does not match the running client.** |
| `translationVersion` | yes | Which generation of the translation this is. Compared as text, so any format that sorts correctly works; a stamp like `yyyy.MM.dd.HHmm` does. |
| `name`, `language`, `languageName`, `author` | no | Shown in the settings window. |
| `updateUrl` | no | Where to fetch a copy of the newest manifest. See below. |
| `pages`, `lines`, `rows` | no | Shown as a coverage line: `lines` of `rows` translated across `pages` pages. |

**`gameVersion` is the one that matters and it is a refusal, not a warning.** Rows shift between
patches. Serving pages built against the previous patch to a client running the next one puts text on
the wrong rows, and it does it silently — every line is well-formed and simply belongs to something
else. Losing the translation until somebody rebuilds is enormously preferable, so that is what
happens.

## Updates

`updateUrl` points at a copy of the **newest** pack's `gubal-manifest.json`, at an address that does
not change. The plugin fetches it in the background, compares `translationVersion`, and offers the
newer one; it never downloads a pack without being asked.

Because the address travels inside the pack, each installation brings its own — so moving hosting
between releases carries your existing users along instead of stranding them.

There is deliberately **no field saying where the archive is**. The user typed that in to install the
pack, and a manifest repeating it would be a second copy of a fact that can disagree with the first.
The contract that implies is the right one anyway: publish successive versions at a stable address,
because taking an update re-downloads from wherever the pack came from.

**Leaving `updateUrl` out is fine** — a test build, or an author with nowhere to host a manifest.
The plugin then never touches the network at all. It does say so in the settings window: a pack that
cannot update itself looks, to the person using it, exactly like one nobody is working on.

## Building one

This repository ships no builder, and nothing above depends on how the pages were produced. Any
program that writes valid `.exd` pages and a manifest beside them produces a pack the plugin will
serve.

Lumina, the game-data library
Dalamud itself uses and exposes to every plugin, reads the same `.exh` headers and `.exd` pages a pack
is made of; [Dalamud's developer documentation](https://dalamud.dev) is where to start.

Whatever you build, reproduce the
game's own bytes exactly when substituting nothing, and check that before substituting anything.
