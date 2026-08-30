# Building a language pack

Gubal Library ships with no text. This document specifies how to produce a language pack within Gubal Library.

The plugin has no notion of any particular language. `language` is a field in the manifest, shown in
the settings window and otherwise unused.

## What a pack is

The game stores its text in Excel sheets inside its archives: one `.exh` header per sheet, and `.exd`
pages holding the rows. A pack is those pages, rebuilt with substituted text, in a folder mirroring
the archive, plus a manifest.

```
ffxiv-language-pack-es.zip/
  gubal-manifest.json
  exd/
    Balloon_0_en.exd
    quest/041/AktKba101_04102_0_en.exd
```

The plugin walks the folder for `*.exd` and derives each file's game path from its path relative to
the root. `exd/Balloon_0_en.exd` in the folder answers reads of `exd/Balloon_0_en.exd` in the
archive. No index or metadata file nedds to be transalted or edited.

A zip of the folder's contents, not of the folder itself, is a distributable pack. e.g. ffxiv-language-pack-es.zip

Two constraints follow from the approach:

**The game has no slot for most languages.** It supports `ja`, `en`, `de` and `fr`. A pack overwrites
one of them, conventionally `en`. Untranslated rows retain their original text, so partial coverage
requires no fallback logic.

**A page is all-or-nothing.** An incorrectly rebuilt page corrupts the entire sheet. Step 4 exists
for this reason and precedes any substitution.

The samples use C# and [Lumina](https://github.com/NotAdam/Lumina), the game-data library Dalamud
uses. The file format is independent of both.

## Step 1: read the sheet header

The `.exh` provides the sheet's page list, its column definitions, and the width of the fixed part of
a row.

```csharp
using var game = new GameData(@"C:\...\FINAL FANTASY XIV\game\sqpack");
var exh = game.GetFile<ExcelHeaderFile>("exd/balloon.exh");

var variant    = exh.Header.Variant;       // Default, or a subrow variant
var dataOffset = exh.Header.DataOffset;    // bytes of fixed columns per row
var columns    = exh.ColumnDefinitions;    // type and byte offset of each column
var pages      = exh.DataPages;            // StartId and RowCount per page
var localised  = exh.Languages.Any(l => l != Language.None);
```

Sheet names are lower-cased in archive paths. `exd/root.exl`, read as `ExcelListFile`, enumerates
every sheet in the game.

Only columns of type `ExcelColumnDataType.String` are translatable. All others are numeric or flag
data and must be copied through unmodified.

## Step 2: read the pages

One `.exd` per entry in `exh.DataPages`, named for the sheet, the page's first row id and the
language.

```csharp
var path  = localised ? $"exd/balloon_{page.StartId}_en.exd"
                      : $"exd/balloon_{page.StartId}.exd";
var bytes = game.GetFile<FileResource>(path)!.Data;
```

A sheet without languages contains no translatable text.

## Step 3: parse a page

The format is a 32-byte header, an index, then the rows. All integers are **big-endian**.

```
45 58 44 46   "EXDF"
00 02         version
00 00         padding
00 01 4A 20   index size    84512
00 08 9B 44   data size    564036     32 + 84512 + 564036 = the 648580-byte file
00 x16        padding
```

The index holds `indexSize / 8` entries of `{ rowId: u32, offset: u32 }`. Each offset addresses
`{ dataSize: u32, rowCount: u16 }` followed by `dataSize` bytes: `dataOffset` bytes of fixed columns,
then a blob of null-terminated strings. A string column holds a u32 offset into that blob rather than
the text.

```csharp
var entry    = 32 + (i * 8);
var rowId    = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(entry, 4));
var offset   = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(entry + 4, 4));
var dataSize = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));

var data      = bytes.AsSpan(offset + 6, dataSize);
var fixedPart = data[..dataOffset];
var blob      = data[dataOffset..];

var at    = columns[c].Offset;
var start = (int)BinaryPrimitives.ReadUInt32BigEndian(fixedPart.Slice(at, 4));
var end   = start;
while (end < blob.Length && blob[end] != 0) end++;

var value = blob[start..end];   // raw SeString bytes
```

**Subrow sheets require a different row walk.** Where `exh.Header.Variant` is not `Default`, a row
contains several subrows of `{ subrowId: u16, fixed }`. Reject those sheets rather than parsing them
partially.

## Step 4: verify the writer before substituting

Rebuild every row from its parsed parts, substituting nothing, and require the output to match the
input byte for byte. Run this on every sheet to be shipped, and treat any difference as fatal.

```csharp
if (!Rebuild(rows).SequenceEqual(original))
    throw new InvalidDataException("writer does not reproduce the game's own bytes");
```

A failed round trip indicates an error in the padding rule, the blob ordering or the offset
arithmetic. The result is not a crash but a sheet whose untouched rows carry the wrong text.

## Step 5: substitute

A string column holds SeString bytes, not text. It may contain payloads the game interprets at draw
time: the player's name, a gendered branch, a colour change, a line break, an item reference. These
lie outside the printable range and must be preserved.

```csharp
row.Strings[slot] = translatedBytes;   // bytes in, bytes out
row.Substituted[slot] = true;
```

Decoding a column to a string and re-encoding it discards or corrupts those payloads. Either parse
the SeString and rewrite only its text runs, or source the translation from a store that recorded the
payloads and can reinsert them.

A line is identified by sheet name, row id and column index.

## Step 6: write the page

Rebuild each row: emit the string blob, patch the offsets into the fixed part, then pad.

**Padding is computed over the row header, not the row data.** A row occupies
`align4(6 + dataOffset + blobLength)` bytes, and the `dataSize` field is that value minus the six
header bytes. An 8-byte fixed part with a 20-byte string yields 30, not 28. This rule is the most
common cause of a step 4 failure.

```csharp
var padded   = (6 + dataOffset + blob.Length + 3) & ~3;
var dataSize = padded - 6;

var body = new byte[6 + dataSize];
BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), (uint)dataSize);
BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(4, 2), 1);
fixedPart.CopyTo(body.AsSpan(6));
blob.CopyTo(body.AsSpan(6 + dataOffset));
```

Three requirements govern the result:

**Emit blob entries in original offset order**, sharing an entry between two columns only where they
shared one in the source. The game stores identical text at separate offsets in some rows;
deduplicating by content rewrites them and fails the round trip. A substituted column leaves its
shared group and requires its own entry.

**Recompute the index.** Row offsets move as rows change size, so the header's `dataSize` and every
index offset derive from the rebuilt bodies.

**Emit only modified pages.** An unmodified page overriding itself adds no value and widens the
surface a game patch invalidates.

## Step 7: write the folder

Each page is written at its game path under the pack root; the manifest sits beside `exd/`.

```csharp
var full = Path.Combine(root, gamePath.Replace('/', Path.DirectorySeparatorChar));
Directory.CreateDirectory(Path.GetDirectoryName(full)!);
File.WriteAllBytes(full, built);
```

**Delete pages a later build no longer emits.** The plugin serves every `.exd` present, so a stale
page from an earlier run continues to be served.

## The manifest

`gubal-manifest.json` at the root of the pack, UTF-8.

```json
{
  "name": "FFXIV Language Pack (ES)",
  "language": "es",
  "languageName": "Español (España)",
  "author": "ashdam",
  "updateUrl": "https://example.org/packs/es/latest.json",
  "issuesUrl": "https://github.com/you/your-pack/issues",
  "translationVersion": "2026.08.08.1756",
  "gameVersion": "2026.08.05.0000.0000"
}
```

| Field | Required | What it does |
|---|---|---|
| `gameVersion` | **yes** | The patch the pages were built from. **The plugin refuses to serve the pack when this does not match the running client.** |
| `translationVersion` | yes | Which generation of the translation this is. Compared as text, so any format that sorts correctly works; a stamp such as `yyyy.MM.dd.HHmm` is sufficient. |
| `name`, `language`, `languageName`, `author` | no | Shown in the settings window. |
| `updateUrl` | no | Where to fetch a copy of the newest manifest. See below. |
| `issuesUrl` | no | Where the settings window directs a report of a mistranslation. |

Unrecognised fields are ignored. **There is deliberately no coverage figure**: the proportion of a
pack that is translated is for its author to publish where the denominator can be stated.

`gameVersion` is read from `ffxivgame.ver`, beside the game executable. The plugin compares against
the same file.

**`gameVersion` is a refusal, not a warning.** Row ids shift between patches. Serving pages built
against the previous patch to a client running the next places text on the wrong rows silently, since
every line remains well-formed. The plugin declines instead, which makes publishing the next patch's
pack in advance safe.

## Testing a pack

`/gubal` → **Language** → *a pack of your own* → select the build output folder → **Use this folder**
→ restart the client.

The folder is served in place. Nothing is copied into the plugin's directory, so the build output and
the served pack are the same files. Press **Use this folder** again after each rebuild.

The manifest is read as soon as the path changes, before the button is pressed. The window reports
the pack name and `translationVersion`, or refuses the folder: no `gubal-manifest.json`, or a
`gameVersion` other than the running client's.

The client reads its text once, seconds into startup. Changes take effect at the next restart.

## Reporting a mistranslation

`issuesUrl` is the translation's own tracker. The settings window offers it as **Report a
mistranslation** beside the language chooser, and never merges it with the plugin's own tracker.

Where the field is absent, a pack listed in the chooser falls back to the address recorded there; a
pack the plugin does not know receives no link. The manifest takes precedence where both exist, so
relocating a tracker between releases carries existing readers with it.

## Updates

`updateUrl` addresses a copy of the newest pack's `gubal-manifest.json`, at a stable address. The
plugin fetches it in the background, compares `translationVersion` and offers the newer pack. Nothing
is downloaded unless requested, either through **Install new version** or the startup fetch.

That fetch reaches only packs published at a URL. Reinstallation draws from the source the pack was
installed from, so a pack distributed as a manually downloaded file can be checked but never updated
automatically.

There is deliberately no field naming the archive's location: the user supplied it at install time
and an update re-downloads from there. Publish successive versions at a stable address. Because the
update address travels inside the pack, relocating hosting between releases carries existing users
with it.

Omitting `updateUrl` is supported. The plugin then makes no network requests, and the settings window
states that the pack cannot update itself.

## Listing a pack

The settings window's language list belongs to the plugin. A published pack is added to it by
[opening an issue](https://github.com/ashdam/gubal-library/issues). No manifest field controls it.

## Out of scope for a pack author

Users may elect to be served only part of a pack, under *Translated parts* in the settings window,
commonly to leave the interface and log messages in English for compatibility with other plugins. No
declaration is required. The grouping is derived from the game's own sheet names, so it covers packs
that do not yet exist, and a sheet the plugin cannot name is listed under that name rather than
omitted.
