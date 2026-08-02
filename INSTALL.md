# Installing (for testers)

Gubal Library puts FFXIV's on-screen text into another language, using a corpus file you supply.
This is **a testing build**: expect rough edges.

> **The plugin ships empty.** It contains no translations, and it makes no network calls. Until you
> load a corpus it changes nothing and the game reads exactly as it always did. Getting hold of a
> corpus is step 4, and it is the step that actually matters.

## 1. Add the repository

1. In game, type `/xlsettings` and press Enter.
2. Go to the **Experimental** tab.
3. Under **Custom Plugin Repositories**, paste this URL into an empty box and press **+**:

   ```
   https://raw.githubusercontent.com/ashdam/gubal-library/main/repo.json
   ```

4. Tick **Enabled** on that row and press **Save and Close**.

## 2. Install the plugin

`/xlplugins` → search for **Gubal Library** → **Install**.

## 3. Check that it installed

There is a self-test built in: two lines from one NPC, so you can tell "installed correctly" apart
from "installed and broken" before you go looking for a corpus. It is a smoke test, not content.

Go to **Limsa Lominsa Lower Decks** and talk to **Ahldskyf**, on the pier by the Orion. His lines
should come out as *"Hello World from Gubal Library!"*.

If they do, the plugin is wired up correctly. Open `/gubal` and you will see a red warning saying the
self-test is all that is loaded — it stays there until you do step 4.

If they do not, `/gubal status` will tell you how many entries loaded.

## 4. Get a corpus for your language

This is the real step. Two lines from one NPC in Limsa is not a translation — everything else in the
game stays exactly as it was until you load a corpus.

**This project does not distribute corpora.** The plugin is a lookup engine; the translations are
somebody's separate work, and where you get one is up to you. Two options:

- **Find one.** If someone in the community has published a corpus for your language, use theirs.
- **Build one.** [CORPUS.md](CORPUS.md) documents the file format completely — the schema, the rule
  your text has to satisfy to match, how macros and speaker names work, and the three stages of the
  pipeline. It is written so that somebody starting from nothing can produce a working file for any
  language.

Once you have one, keep it wherever you like and point the plugin at it:

1. `/gubal` to open the settings window.
2. **Browse...** next to *Corpus path*, and pick your file.

That is the whole thing. The path is saved and the corpus is loaded immediately — no renaming, no
restart, no `reload`. The entry count updates and the red sample warning disappears.

If it still says 0 entries, the file loaded but nothing in it was usable; check it against
[CORPUS.md](CORPUS.md).

<details>
<summary>Alternative: let the plugin find it automatically</summary>

If you would rather not set a path, name the file `corpus.json` and drop it in

```
%AppData%\XIVLauncher\pluginConfigs\GubalLibrary
```

then run `/gubal reload`. The folder is created when the plugin first loads, so if it is not there
you have not started the plugin yet. This route only exists for people who prefer it; **Browse...**
is easier and works with any filename and any location.

</details>

## Commands

| Command | What it does |
|---|---|
| `/gubal` | Open settings |
| `/gubal on` / `off` | Turn injection on or off without uninstalling |
| `/gubal status` | Entries loaded, file in use, lines injected, lines missed |
| `/gubal reload` | Re-read the corpus without restarting the game |
| `/gubal clearmisses` | Empty the record of lines that were not found |

## Reporting a problem

Open an issue on [GitHub](https://github.com/ashdam/gubal-library/issues) and include:

- **What you expected and what you got.** A screenshot beats a description.
- **The output of `/gubal status`.**
- **If the game closed:** the `.tspack` Dalamud writes. Look in `%AppData%\XIVLauncher\` for the most
  recent `crash-<date>.tspack`.
- **If a line stayed in the original language and you think it should not have:** the `misses.jsonl`
  file from the plugin config directory. It records the exact key the lookup used, which is what is
  needed to work out why it did not match. Note this is a problem with the corpus, not usually with
  the plugin — take it to whoever maintains the corpus first.

## Uninstalling

`/xlplugins` → **Gubal Library** → **Uninstall**. To drop the repository too, go to `/xlsettings` →
**Experimental** and use the bin icon on the URL's row.
