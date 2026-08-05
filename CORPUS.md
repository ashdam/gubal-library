# Building a language pack

Gubal Library is a lookup engine with no data in it. This document is the contract: produce a file
that satisfies it and the plugin will use it, whatever built it and whatever language it targets.

The plugin has no notion of Spanish, or of any particular language. `targetLanguage` is a field in
the file, logged when the pack loads and otherwise unused. A pack for any locale works the same way,
and the mechanics below — text-keyed lookup, macro resolution, conversation scoping — are properties
of how the game delivers text, not of the language you are translating into.

**No language pack is distributed from this repository.** The text is Square Enix's, and a
translation of it is a derivative work; the plugin itself reproduces none of it. Where a pack comes
from is its author's business, not the plugin's.

## The file

UTF-8, **no BOM**. Comments and trailing commas are tolerated by the loader, so a hand-edited file is
fine.

The plugin reads `corpus.json` from its config directory unless an explicit path is set in `/gubal`.
`es.json` in the same directory is also accepted, as a legacy name.

```json
{
  "schemaVersion": 2,
  "sourceLanguage": "en",
  "targetLanguage": "es",
  "gameVersion": "2026.07.16.0001.0000",
  "translationVersion": "2026-08-05 19:40",
  "npcNames": { "Alphinaud": "Alphinaud" },
  "entries": [
    {
      "gameKey": "quest/047/SubWil901_04779#12",
      "conversation": "quest/047/SubWil901_04779",
      "source": "Off you go now, <if(...)>.",
      "target": "Vete ya, <if(...)>."
    }
  ]
}
```

| Field | Required | Meaning |
|---|---|---|
| `schemaVersion` | yes | Must be `2`. **Any other value is refused and nothing is loaded** — a schema this build cannot read parses without error and yields zero entries, which on screen is indistinguishable from a broken plugin. |
| `sourceLanguage`, `targetLanguage` | no | Which languages the entries are in. Written to the load log; `sourceLanguage` is read and otherwise unused. |
| `gameVersion` | no | The patch the pack was built against. Written to the load log. |
| `translationVersion` | no | Which generation of the translation this is, stamped by the extractor. Written to the load log, which is the only way to tell a stale copy from a current one. |
| `npcNames` | no | Source → translated speaker names. Only used when **Translate speaker names** is on. |
| `entries[].source` | yes | The source line, **macros unresolved**. See below. |
| `entries[].target` | yes | The translation. Entries with either side empty are skipped at load. |
| `entries[].conversation` | no | Scopes the entry to one conversation, e.g. `quest/047/SubWil901_04779`. |
| `entries[].gameKey` | no | Provenance only. **Never used for lookup.** |

None of the header fields reach `/gubal status`, which reports counts and paths only. They are in the
load log, `/xllog`.

**The entry fields do not name their language.** Schema 1 called them `en` and `es`, which put the
same fact in two places and let them disagree — a file declaring `"targetLanguage": "it"` whose
entries still said `es`. The header is the single answer now, and an Italian pack is this same file
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

**`source` must carry macros unresolved** — `<if([gnum11<12],Good morning,Good evening)>`, not one
branch of it. Every entry goes through the game's own evaluator at load, which reproduces exactly
what the player sees. Flattening beforehand cannot: an extractor that deletes macros produces
`"Off you go now, ."` where the game says `"Off you go now, Mini."`, and that matches nothing.

There is no separate field for the macro form and no flag saying a line has one. Evaluating a string
that holds no macros returns the string, so the distinction buys nothing, and no cheap test for it is
correct anyway — searching for `<` catches an escaped `\<sigh>`, which is literal text.

Three consequences worth knowing:

- **Keys are per character.** Macros resolve against name, gender and Grand Company rank, so the
  index is rebuilt on login. A pack indexed for one character does not match another.
- **`gnum11` is the Eorzean hour.** Entries using it are re-keyed as the clock advances; an Eorzean
  hour is under three real minutes, so a key built once would stop matching almost immediately.
- **A `source` that will not evaluate drops the entry**, with a count in the load log. It is not
  indexed under its raw text instead: that is a string the game will never draw, so it could only
  match by accident, and an accident here means injecting the wrong translation over whatever it
  collided with. Leaving the game's own text on screen is the correct answer when the plugin cannot
  work out what the line says.

Translations may carry macro syntax too, and it is resolved at display time — both conditionals like
`<if(gnum4,cansada,cansado)>` and the game's formatting macros, which is how italics and colour reach
the screen: the injected value is written as SeString bytes, not as characters. One syntax serves both
sides, so there is no second format to define.

**A `target` that will not evaluate is not injected either.** The game's own line stays instead;
putting a visible `<if(gnum4,…)>` on screen is worse than the text it would replace. So is a
translation that resolves to nothing but whitespace, which would blank the dialogue box. `/gubal
status` counts every refusal and the log names the first twenty.

Do not put asterisks in `target`. The game draws them literally rather than as emphasis, so `*Orion*`
appears with the asterisks visible; use the game's own italic macro if you want emphasis. They are
stripped at load, with a count in the log.

## Duplicate English

Identical English in two different quests is common — in one measured pack, 4,407 of 5,378 repeated
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
   its keep; term drift across a pack this size is invisible without one.
3. **Merge.** Emit the runtime file: `source`/`target` only, translated entries only, compact. The
   editing pack and the runtime pack are not the same artifact, and shipping the former wastes most
   of the load.

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

Misses are deduplicated in memory, so a repeated line is written once, and writing stops after 5,000
distinct keys with a warning in the log. `/gubal clearmisses` deletes the file and resets the dedup
set.

Paste a recorded key into `corpus.json` as a `source` value and you have an entry guaranteed to match.

If a line never reaches the miss log at all, it is arriving through an addon the plugin does not
watch. `/gubal find <text>` listens to every addon in the game, by both delivery routes, and
reports which one carries that string.
