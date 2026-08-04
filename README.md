# Gubal Library

*Community language localizer for FFXIV.*

A Dalamud plugin that replaces the game's on-screen text with a translation you supply. Named for the
Great Gubal Library, Sharlayan's repository of all written knowledge — which is what a corpus is.

**It is a lookup engine, not a translator.** It makes no network calls of any kind: it loads a corpus
file, matches what is on screen against it, and injects the result. Nothing is sent anywhere and
nothing is translated at runtime.

**You bring the corpus, and this project does not distribute one.** The translations are somebody's
separate work; the plugin only reads them. Users either find a corpus the community has published for
their language or build their own — which is why [CORPUS.md](CORPUS.md) documents the format in full
rather than sketching it. Any language works: the corpus declares its own target language and nothing
in the plugin knows or cares which one it is.

**So it ships empty**, and the documentation says so first, before anything else. Installing it
changes nothing until a corpus is loaded.

There is a self-test bundled — two lines from one NPC — but that is a smoke test, not content. It
exists because an empty plugin and a broken plugin look identical from the outside, and "did it
install?" is the first question every tester asks. It must never be described as though it were a
translation, and the settings window stays red for as long as it is all that is loaded.

- **Just want to try it?** → [INSTALL.md](INSTALL.md)
- **Want to build a corpus for your language?** → [CORPUS.md](CORPUS.md)

## Status

Testing build. Covers `Talk` (NPC dialogue, quest text, cutscene speech), `TalkSubtitle` (cutscene
narration) and `_MiniTalk` (speech balloons). `_BattleTalk` and `_ScreenInfoFront` are registered and
report themselves in the log when they fire, but are not characterised. `SelectString` is not handled.

**Formatting survives injection.** Italics, colour and gender conditionals reach the screen, because
the injected value is written as SeString bytes rather than flattened to a string — see
`SeStringWriter` and `MacroResolver` under *Layout*. A translation whose macros will not evaluate is
not injected at all: the game's own line is better than a visible `<if(gnum4,…)>`.

### What is verified, and what is only compiled

Kept honest on purpose, because "builds clean" and "works" are different claims and this project has
already published a conclusion that rested on the second being assumed from the first.

| | |
|---|---|
| Builds clean, 0 warnings | yes, repeatedly |
| Release zip correct from a clean tree | yes — 4 entries, 45,042 bytes |
| `repo.json` agrees with the built manifest | yes, field by field |
| Dalamud / FFXIVClientStructs APIs used | verified by introspecting the installed DLLs |
| Quest-scoped lookup resolves in game | yes — Nananji, quest 4779, 11 of 11 lines, zero misses |
| Italics reach the screen | yes — Ahldskyf's *Orion*, Limsa Lominsa Lower Decks |
| An escaped `\<` stays literal text | yes — Tatasosa's four `\<crujido>`, New Gridania |
| Gender conditionals resolve | yes — Hida, New Gridania |
| `<br>` breaks the line | yes — Tatasosa, three lines |
| Restoring the game's line on unload | yes — English returns, with its own italics intact |
| Speech balloons resize to the translation | yes — several FATEs, native size for the line count |
| **The character-select crash fix, in game** | **no — never re-run** |
| `_BattleTalk` injecting in game | no — the addon fires, but no translated line has been seen in it |
| `<split>` and `<string>` on screen | no — 903 and 918 occurrences, never observed |
| The bundled sample injects in game | no |
| The red sample warning renders | no |
| The release workflow | no — never executed, never pushed |

**The crash fix is the one that matters.** It is the reason the plugin was touched at all: an
`AccessViolationException` was killing the client at character select, the cause was found (see
`AtkText` under *Layout*) and the fix compiles — but reaching character select to reproduce it was
never done again. Until that is run, the most important change in the plugin is unconfirmed.

## How it works

On `PreRefresh` the plugin swaps the addon's **source values** before the game builds its text nodes,
so the game does its own word wrapping and plays its own typewriter reveal from the translated
string. Both come free.

A second pass on `PreDraw` re-asserts the text for addons that write straight into their nodes
without ever refreshing. Both routes are hooked deliberately: narration proved the point, where one
sentence arrived as a value and the next produced no refresh at all. Hooking one and reasoning the
other away has cost a debugging round twice.

Lookup is on text, not on a row id, because the addon does not expose one. `TextKey.Normalize` is
applied to both sides to absorb the ways two readers render the same payload differently. Where a
quest is running, the live `QuestEventHandler` names the conversation and the join is scoped to it,
which keeps identical source text in two quests from collapsing onto one translation.

## Commands

| Command | Effect |
|---|---|
| `/gubal` | Open settings |
| `/gubal on` / `off` | Master switch |
| `/gubal reload` | Re-read the corpus without restarting the game |
| `/gubal status` | Entry count, source file, injected count, miss count |
| `/gubal dump` | Toggle miss logging |
| `/gubal probe` | Log the event handler and resolved conversation per line |
| `/gubal find <text>` | Listen to every addon and report which one carries that string |
| `/gubal clearmisses` | Delete `misses.jsonl` and reset dedup |

`find` and `probe` are diagnostics. `find` registers on **every addon in the game**; leave it off
unless you are chasing something. Note that its needle is persisted in the config, so a hunt left on
is re-armed on the next start.

## Building

Requires the **.NET 10 SDK** and a Dalamud install (launch the game through XIVLauncher once).

```powershell
dotnet build -c Release
```

Output lands in `bin\x64\Release\win-x64\` — note the `win-x64` level, appended because the project
sets a `RuntimeIdentifier`. Both `GubalLibrary.dll` **and** `GubalLibrary.json` must be there; a DLL
without its sibling manifest is silently skipped by Dalamud, with the error going only to the log.
The packager also emits `win-x64\GubalLibrary\latest.zip`, which is what the plugin repository
serves.

The packager zips whatever is in the output folder, so build on a clean tree — a stale artifact from
a previous name or version gets shipped inside the release zip otherwise.

To build without XIVLauncher installed — which is what CI does — set `DALAMUD_HOME` to an unpacked
copy of <https://goatcorp.github.io/dalamud-distrib/latest.zip>. The SDK has no NuGet fallback and
fails the build outright if it cannot find a Dalamud install.

## Developing against a live game

1. `/xlsettings` → **Experimental** → tick **Enable Developer Mode**. This reveals the **Dev Plugin
   Locations** panel.
2. Add the path to the **DLL itself** — this Dalamud build wants the file, not the folder:

   ```
   <repo>\bin\x64\Release\win-x64\GubalLibrary.dll
   ```

3. `/xlplugins` → **Dev Tools → Installed Dev Plugins** → enable *Gubal Library*.
4. Reload from that same panel after each rebuild. No game restart needed.
5. `/xllog` for the log.

## Releasing

`.github/workflows/release.yml` builds and publishes on a `v*` tag, then rewrites `repo.json` on
`main` and pushes it back. The download links point at `/releases/latest/` and never change.

The tag must match `<Version>` in the csproj or the workflow refuses to publish. Dalamud offers an
update by comparing `AssemblyVersion`, not the tag, so a mismatch is invisible to testers rather than
merely untidy.

```powershell
# after bumping <Version> in the csproj
git tag v0.1.1.0
git push origin v0.1.1.0
```

Corpora are not part of this flow, and not part of any flow here — releases carry the plugin and the
bundled sample, nothing else. Keep it that way: the moment a real corpus ships from this repository,
the repository is redistributing the game's text rather than a tool that reads it.

## Layout

| | |
|---|---|
| `Plugin.cs` | Entry point, command handling, service wiring |
| `TalkHandler.cs` | The `Talk` addon: two strings, wrap width to capture and restore |
| `OverlayHandler.cs` | `TalkSubtitle`, `_BattleTalk`, `_MiniTalk`, `_ScreenInfoFront` — one string, one node |
| `TranslationStore.cs` | The corpus in memory: two indexes, clock-dependent re-keying |
| `TextKey.cs` | The normalization contract, applied to both sides of the join |
| `AtkText.cs` | Reading strings out of `AtkValue` and text nodes, safely |
| `AddonNodes.cs` | Walking an addon's node tree, into components |
| `AddonInspector.cs`, `AddonFinder.cs`, `EventProbe.cs` | Diagnostics |
| `MissLog.cs` | Append-only record of lines that found no translation |

`AtkText` is not incidental. `AtkValue` is a tagged union, and reading its string field on a value
holding an `Int` dereferences a number as a pointer — which crashed the game at character select
until the type check landed. An `AccessViolationException` is uncatchable on .NET Core, so the guard
has to come before the read, and it lives in one place so it cannot be half-applied. It was written
four times before that, and two of the copies did not check.
