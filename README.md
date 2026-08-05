# Gubal Library

*Community language localizer for FFXIV.*

Final Fantasy XIV speaks four languages. Its players speak dozens.

Every one of those communities has had the same thought at some point: *we could translate this
ourselves.* What stopped them was never the will, and never the words — it was that there was nowhere
to put them. Gubal Library is that place. It takes a language pack the community built and puts it on
screen in the live game: the game's own font, the game's own typewriter reveal, the game's own line
breaks, formatting intact.

Named for the Great Gubal Library, Sharlayan's repository of all written knowledge — which is what a
language pack is: a library of the game's words in a language it was never given.

A Dalamud plugin that replaces the game's on-screen text with a translation you supply. Gubal Library
is a multilingual engine — it reads a language pack and injects it directly into the game. Any
language works: the pack declares its own target language, and nothing in the plugin knows or cares
which one it is.

**This project does not distribute language packs.** Users either find a pack the community has
published for their language or build their own — which is why [CORPUS.md](CORPUS.md) documents the
format in full rather than sketching it.

**So it ships empty**, and the documentation says so first, before anything else. Installing it
changes nothing until a language pack is loaded.

There is a self-test bundled — two lines from one NPC — but that is a smoke test, not content. 

- **Want to build a language pack for your language?** → [CORPUS.md](CORPUS.md)

## Examples

The live game with a Spanish language pack loaded. **The pack is not part of this project** and is
not distributed with it — the translation is somebody's separate work. These images show what the
engine does with a pack, not an offer of one: load a pack for another language and the same screens
come out in that language instead.

None of it is a mock-up or an overlay. It is the game's own dialogue box, the game's own font and the
game's own line breaking, with the text replaced underneath.

One per surface the plugin supports.

**NPC dialogue — the `Talk` window**

![Spanish NPC dialogue in the Talk window](images/example1.png)

**Cutscene narration — `TalkSubtitle`**

![Spanish cutscene narration over the scene](images/example4.png)

**Speech balloons — `_MiniTalk`**

![A Spanish speech balloon above an NPC](images/example3.png)

**Combat callouts — `_BattleTalk`**

![A Spanish combat callout during a boss fight](images/example2.png)

The last two are the surfaces the plugin has to size itself, because the game lays them out before a
translation can reach them — see *How it works*. In both, the panel has been grown to hold a line
longer than the English it replaced. The first two need none of that: their text is swapped before the
game builds the box, so the game does its own wrapping from the Spanish.

## Install

A testing build, so expect rough edges. It needs XIVLauncher/Dalamud, and it makes no network calls.

1. **Add the repository.** `/xlsettings` → **Experimental** → paste the URL into an empty
   **Custom Plugin Repositories** box, press **+**, tick *Enabled*, then **Save and Close**:

   ```
   https://raw.githubusercontent.com/ashdam/gubal-library/main/repo.json
   ```

2. **Install the plugin.** `/xlplugins` → search for **Gubal Library** → **Install**.

3. **Check it installed.** Talk to **Ahldskyf**, on the pier by the Orion in Limsa Lominsa Lower
   Decks. His two lines should read *"Hello World from Gubal Library!"* — that is the bundled
   self-test, and it exists to tell "installed" apart from "installed and broken". `/gubal` shows a
   red warning for as long as it is all that is loaded; `/gubal status` says how many entries loaded.

4. **Load a language pack.** The step that actually matters — everything else in the game stays as it
   was until you do. Keep the file wherever you like: `/gubal` → **Browse...** under *Corpus path* →
   pick it. It loads immediately, with no renaming, no restart and no `reload`; the entry count
   updates and the red warning goes. If you would rather not set a path, name it `corpus.json` in
   `%AppData%\XIVLauncher\pluginConfigs\GubalLibrary` and run `/gubal reload`. Still 0 entries means
   the file loaded but nothing in it was usable — check it against [CORPUS.md](CORPUS.md).

**Reporting a problem.** Open an [issue](https://github.com/ashdam/gubal-library/issues) with what you
expected and what you got (a screenshot beats a description) and the output of `/gubal status`. If the
game closed, add the most recent `crash-<date>.tspack` from `%AppData%\XIVLauncher\`. If a line stayed
in the original language, add `misses.jsonl` from the plugin config directory — it records the exact
key the lookup used, and that is usually a fault in the language pack rather than the plugin, so take
it to whoever maintains the pack first.

**Uninstalling.** `/xlplugins` → **Gubal Library** → **Uninstall**. To drop the repository too,
`/xlsettings` → **Experimental** → the bin icon on that URL's row.

## What's supported in-game

Testing build. The in-game text Gubal Library localizes today:

| Where the text appears | Addon | State |
|---|---|---|
| NPC dialogue, quest text, cutscene speech | `Talk` | supported |
| Cutscene narration | `TalkSubtitle` | supported |
| Speech balloons above NPCs | `_MiniTalk` | supported — the balloon resizes to the translation |
| Combat callouts | `_BattleTalk` | supported — the node grows to fit the line count |
| On-screen banners | `_ScreenInfoFront` | registered, reports itself in the log when it fires, not characterised |
| Dialogue choice lists | `SelectString` | not handled |

This localizes what characters say, not what the client labels: menus, item names, tooltips and job
UI are untouched.

**Formatting survives injection.** Italics, colour and gender conditionals reach the screen, because
the injected value is written as SeString bytes rather than flattened to a string — see
`SeStringWriter` and `MacroResolver`. A translation whose macros will not evaluate is not injected at
all: the game's own line is better than a visible `<if(gnum4,…)>`.

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
| `_BattleTalk` injecting and fitting | yes — Enuo, The Unmaking; node 6 grew 20 → 46 for a two-line line |
| **The character-select crash fix, in game** | **no — never re-run** |
| `<split>` and `<string>` on screen | no — 903 and 918 occurrences, never observed |
| The bundled sample injects in game | no |
| The red sample warning renders | no |
| The release workflow | yes — four tags built and published a `latest.zip`, 0.1.3.0 through 0.1.6.0 |

**The crash fix is the one that matters.** It is the reason the plugin was touched at all: an
`AccessViolationException` was killing the client at character select, the cause was found (reading
the string field of an `AtkValue` holding an `Int`, guarded now in `AtkText.cs`) and the fix compiles
— but reaching character select to reproduce it was never done again. Until that is run, the most
important change in the plugin is unconfirmed.

## Commands

| Command | Effect |
|---|---|
| `/gubal` | Open the settings window |
| `/gubal on` / `off` | Master switch |
| `/gubal reload` | Re-read the language pack without restarting the game |
| `/gubal status` | Entries and NPC names indexed, source file, lines injected, distinct misses, translations refused because their macros would not evaluate, and which character the index was built for |
| `/gubal dump` | Toggle miss logging to `misses.jsonl` |
| `/gubal probe` | Log the event handler and resolved conversation per line |
| `/gubal find <text>` | Listen to every addon and report which one carries that string |
| `/gubal find` | With no text: stop listening and clear the stored needle |
| `/gubal clearmisses` | Delete `misses.jsonl` and reset the dedup set |

Every command answers in chat; `probe`, `find` and the load failures write to `/xllog`.

`find` and `probe` are diagnostics. `find` registers on **every addon in the game**; leave it off
unless you are chasing something. Note that its needle is persisted in the config, so a hunt left on
is re-armed on the next start — `/gubal find` with no text is how you turn it off.

The index is built per character on login and rebuilt when a different one logs in, which is why
`status` names the character it was built for.
