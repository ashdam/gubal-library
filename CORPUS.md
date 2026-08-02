# Building a corpus

Gubal Library is a lookup engine with no data in it. This document is the contract: produce a file
that satisfies it and the plugin will use it, whatever built it and whatever language it targets.

The plugin has no notion of Spanish, or of any particular language. `targetLanguage` is a field in
the file, reported by `/gubal status` and otherwise unused. A corpus for any locale works the same
way, and the mechanics below — text-keyed lookup, macro resolution, conversation scoping — are
properties of how the game delivers text, not of the language you are translating into.

**No corpus is distributed from this repository.** The text is Square Enix's, and a translation of it
is a derivative work; the plugin itself reproduces none of it. Where a corpus comes from is the
corpus author's business, not the plugin's.

## The file

UTF-8, **no BOM**. Comments and trailing commas are tolerated by the loader, so a hand-edited file is
fine.

```json
{
  "schemaVersion": 2,
  "sourceLanguage": "en",
  "targetLanguage": "es",
  "gameVersion": "2026.07.16.0001.0000",
  "npcNames": { "Alphinaud": "Alphinaud" },
  "entries": [
    {
      "gameKey": "quest/047/SubWil901_04779#12",
      "conversation": "quest/047/SubWil901_04779",
      "source": "Off you go now, .",
      "macro": "Off you go now, <if(...)>.",
      "target": "Vete ya, ."
    }
  ]
}
```

| Field | Required | Meaning |
|---|---|---|
| `schemaVersion` | yes | Must be `2`. Anything else loads with a warning. |
| `sourceLanguage`, `targetLanguage` | no | Which languages the entries are in. Reported by `/gubal status`. |
| `gameVersion` | no | The patch the corpus was built against. Reported by `/gubal status`. |
| `npcNames` | no | Source → translated speaker names. Only used when **Translate NPC names** is on. |
| `entries[].source` | yes | The source line **as the game renders it**. See below. |
| `entries[].target` | yes | The translation. Entries with either side empty are skipped at load. |
| `entries[].macro` | no | The unresolved macro form. Preferred over `source` for building the key. |
| `entries[].conversation` | no | Scopes the entry to one conversation, e.g. `quest/047/SubWil901_04779`. |
| `entries[].gameKey` | no | Provenance only. **Never used for lookup.** |

**The entry fields do not name their language.** Schema 1 called them `en` and `es`, which put the
same fact in two places and let them disagree — a file declaring `"targetLanguage": "it"` whose
entries still said `es`. The header is the single answer now, and an Italian corpus is this same file
with a different header and nothing else changed.

## The one rule

The `Talk` addon does not expose the row id of the line it is showing — only the finished string. So
the runtime join is **on text**, and `source` must match what the game puts on screen, modulo
`TextKey.Normalize`, which is applied to both sides:

- A leading `(-Speaker-)` label is stripped.
- Asterisks are removed. Dalamud's reader renders emphasis payloads as literal `*`; other readers
  drop them. Stripping makes the two agree.
- Every Unicode dash (U+2010–U+2015, U+2212) folds to an ASCII hyphen. `city<-->state` comes back as
  an en dash from one reader and a plain hyphen from another — one codepoint, total miss.
- Whitespace runs collapse to a single space, then the string is trimmed.

Nothing else is tokenized. In particular **there is no `{PLAYER}` token**: resolve macros against the
game's real state instead (see below) and both sides arrive carrying identical literal text.

## Macros

Lines containing game macro syntax — `<if([gnum11<12],Good morning,Good evening)>` — must ship their
`macro` field. The plugin runs it through the game's own evaluator at load to build the key, which
reproduces exactly what the player sees. Flattened text does not: an extractor that deletes macros
produces `"Off you go now, ."` where the game says `"Off you go now, Mini."`.

Two consequences worth knowing:

- **Keys are per character.** Macros resolve against name, gender and Grand Company rank, so the
  index is rebuilt on login. A corpus indexed for one character does not match another.
- **`gnum11` is the Eorzean hour.** Entries using it are re-keyed as the clock advances; an Eorzean
  hour is under three real minutes, so a key built once would stop matching almost immediately.
- **A `macro` that will not evaluate drops the entry**, with a count in the load log. It is not
  indexed under the flattened `source` instead: that form has holes where the macros were, so it can
  only match by accident, and an accident here means injecting the wrong translation over a line that
  happened to collide. Leaving the game's own text on screen is the correct answer when the plugin
  cannot work out what the line says.

Translations may carry macro syntax too — `<if(gnum4,cansada,cansado)>` — and are resolved at display
time. One syntax serves both sides, so there is no second format to define. A translation that fails
to evaluate is injected verbatim rather than dropping the line.

Do not put asterisks in `target`. The injector marshals plain UTF-8 and does no macro parsing, so
`*Orion*` renders with the asterisks visible. They are stripped at load, with a count in the log.

## Duplicate English

Identical English in two different quests is common — in one measured corpus, 4,407 of 5,378 repeated
lines occurred in more than one quest. Set `conversation` and both stay reachable: the plugin keeps a
conversation-scoped index consulted first, and a text-only index as the fallback. Without
`conversation` they collapse onto whichever loaded first and the rest are unreachable.

The fallback matters as much as the scoped hit. Ambient chatter has no quest handler, so its
`conversation` is null at runtime and the text-only index is the only thing that can answer.

## The pipeline this was built against

Three stages, of which only the last is the plugin's concern:

1. **Extract.** Read the game's own Excel sheets (via Lumina, off the `sqpack` directory) and emit
   this schema with every `target` empty. Quest sheets, cutscene sheets and the flat ambient sheets
   (`DefaultTalk`, `NpcYell`, `Balloon`, …) are separate families and worth extracting separately.
   The game ships professional translations of every line in ja/de/fr, which are the best available
   reference — French especially, for a Spanish target: same grammatical gender, same formal and
   informal split.
2. **Translate.** Entirely offline, by whatever means. This is where a glossary of proper nouns earns
   its keep; term drift across a corpus this size is invisible without one.
3. **Merge.** Emit the runtime file: `source`/`target` only, translated entries only, compact. The editing
   corpus and the runtime corpus are not the same artifact, and shipping the former wastes most of
   the load.

The extractor and merge tooling are not published. They are specific to one game install and one
translation workflow, and the schema above is the whole interface — nothing in the plugin depends on
how the file was produced.

## Load cost

The loader deserializes straight from the file stream rather than reading it into a string first: at
85 MB, `ReadAllText` alone is a ~170 MB UTF-16 allocation on the large object heap before any of the
data you actually keep. Every load logs file size, read time, parse time, retained heap and total
allocation, so a regression here is measurable rather than a matter of opinion.

Both indexes hold the same string references, so scoping roughly doubles the key overhead but not the
values.

## Debugging a line that will not match

Turn on miss logging (`/gubal dump`, on by default) and read `misses.jsonl` in the plugin config
directory. Each record holds the normalized key the lookup actually used and, when normalization
changed something, the raw string as well — so a rule that eats something it should not looks
different from a genuinely missing entry.

Paste a recorded key into `corpus.json` as a `source` value and you have an entry guaranteed to match.

If a line never reaches the miss log at all, it is arriving through an addon the plugin does not
watch. `/gubal find <text>` listens to every addon in the game, by both delivery routes, and
reports which one carries that string.
