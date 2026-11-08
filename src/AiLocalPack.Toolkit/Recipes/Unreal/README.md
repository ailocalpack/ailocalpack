# Unreal toolkit: pak localization, IoStore games and cultures (task 37)

`unreal` recipe 1.0.0 reads standard unencrypted UE4/UE5 pak files through
[CUE4Parse](https://github.com/FabianFG/CUE4Parse) (Apache-2.0, NuGet
1.2.2.202611). It parses game `.locres` and `.locmeta` with bounded managed
readers and writes a new uncompressed pak v3 into
`<Project>/Content/Paks/~mods/ailocalpack_P.pak`. Windows/Linux project layouts
and macOS `.app/Contents/UE4` layouts use the engine detector's marker.

The tool reads only the game's top-level archives. `Engine/` localization and
installed mods are excluded. Native culture comes from `.locmeta`, or `en` if
metadata is absent; no language is guessed from the largest table. Keys contain
the escaped resource path, namespace and key. Source hashes and (versions 2/3)
namespace/key hashes are retained when writing. Placeholder notes preserve
format fields and rich-text tags. Empty/equal translations write nothing.

Rebuild writes the table's language into the game's own culture of it when the
game ships one (`UnrealCultures.Pick`: the same name, then the names games use
for our tags such as `zh-CN` for `zh-Hans` or `es-MX` for `es-419`, a regional
tag's bare language, a bare tag's main region; never another variant of the
language). That culture keeps its untranslated entries; an entry missing there
or with another source hash fails before output. When the game has no such
culture, the native culture is overlaid and `RebuildResult.InGameLanguage`
names it: the app's install toast and log tell the player to keep the game in
that language. No language is added to the game menu: a culture of our own
would need the game's config and menu to list it, which a pak can't promise.
Compatibility tracks all containers holding a target's cultures and metadata.
The original paks are never written; the common installer adds/removes the mod
pak and supports reapply. Rebuild ignores installed mods to keep source keys
stable.

## Formats and references

- Locres 0 (Legacy), 1 (Compact), 2 (Optimized CRC32), 3 (Optimized CityHash64
  UTF16), with ANSI/UTF-8 and UTF-16 strings. `INDEX_NONE` empty lookup tables
  are accepted. Future versions, corrupt offsets/counts/indices, duplicate keys,
  invalid strings and resources over 32 MiB are rejected.
- Locmeta 0–2: native culture/resource, compiled cultures, UGC flag.
- Uncompressed pak v3: full index, `../../../` mount point, per-payload and
  index SHA-1. No signing, compression, encryption or native writer needed.
- Format references (Apache-2.0): CUE4Parse
  [locres](https://github.com/FabianFG/CUE4Parse/blob/master/CUE4Parse/UE4/Localization/FTextLocalizationResource.cs),
  [locmeta](https://github.com/FabianFG/CUE4Parse/blob/master/CUE4Parse/UE4/Localization/FTextLocalizationMetaDataResource.cs),
  [pak entry](https://github.com/FabianFG/CUE4Parse/blob/master/CUE4Parse/UE4/Pak/Objects/FPakEntry.cs),
  [pak footer](https://github.com/FabianFG/CUE4Parse/blob/master/CUE4Parse/UE4/Pak/Objects/FPakInfo.cs).
  Own writers are independently checked by CUE4Parse in tests.
- Microsoft.Bcl.Memory is pinned to 9.0.14: CUE4Parse's transitive 9.0.0 has
  advisory GHSA-73j8-2gch-69rq. Restore keeps vulnerability auditing enabled.

## Spike and runtime check (2026-11-07)

On the maintainer's macOS/arm64, CUE4Parse mounted all six EVERSPACE game paks
(24,421 virtual files); production extraction found 4,758 game strings in four
localization targets. Engine/plugin strings were excluded. A one-string
rebuild of the English main menu replaced `OPTIONS` with `TOOLKIT TEST` in a
separate APFS copy of the game. The running game displayed `TOOLKIT TEST`:
`/private/tmp/ailocal-everspace-translated-menu.png`. The original Steam install
was not modified. With an isolated home and test Steam library pointing to the
copy, `dev apply-files` added one pak and `dev uninstall` removed it.

CUE4Parse also mounted its upstream synthetic UE5.8 uncompressed IoStore
fixture: 111 files. That fixture contains no `.locres`/`.locmeta`; mounting it
proves container enumeration, not localization extraction or runtime overlay.
No upstream fixture or real game file was copied into this repository.

## Remaining work (37b)

`toolkit extract` now reads indexed, unsigned, non-on-demand IoStore TOC
versions 3–8, including mixed pak/IoStore layouts and partitioned `.ucas`.
Own synthetic fixtures contain English/French locres and v2 locmeta, unlike the
upstream cooked-asset fixture. Both our bounded directory walker and independent
CUE4Parse directory traversal read them. Tests cover None, Zlib and Gzip. These
are reader tests on macOS/arm64, not runtime loading or Windows/Linux validation.

`UnrealIoStore` bounds one in-memory TOC snapshot before handing those same
bytes to CUE4Parse (including its compression-name stack allocation and the
complete chunk-metadata extent; v8 records have three padding bytes). It walks
directories with cycle/path/index checks, accepts unrelated root-level files,
checks all data partitions and block extents, and exposes raw
`ExternalFile` chunks only for localization. Unknown/future TOCs, missing data,
AES with no configured key, unavailable localization compression, signed and
on-demand containers fail explicitly. No game DLL supplies a codec. Duplicate
virtual paths across pak/IoStore (including case variants) are refused rather
than guessing precedence. Already opened partitions are closed when any later
open or constructor step fails; cancellation remains an OperationCanceledException.
Metadata parsing is independently exercised with CUE4Parse, in addition to its
directory traversal.
Compatibility includes the TOC and every partition of a localization container.
Orphan data partitions fail even alongside an otherwise readable pak.

No exact UE minor is guessed from the presence of a utoc. The raw v3–8 container
layout is driven by the TOC version; CUE4Parse uses `GAME_UE5_0` for its UE5
external-file discriminator/header branch. Cooked package/FText parsing is not
attempted. `global.utoc` is intentionally not opened: global script/package
metadata is unnecessary for external locres/locmeta. A missing global file is
therefore fine for this path, and says nothing about cooked-asset support.
Format references (Apache-2.0): CUE4Parse
[TOC header](https://github.com/FabianFG/CUE4Parse/blob/master/CUE4Parse/UE4/IO/Objects/FIoStoreTocHeader.cs),
[TOC resource](https://github.com/FabianFG/CUE4Parse/blob/master/CUE4Parse/UE4/IO/Objects/FIoStoreTocResource.cs),
[reader/directory index](https://github.com/FabianFG/CUE4Parse/blob/master/CUE4Parse/UE4/IO/IoStoreReader.cs).
The synthetic writer in TestKit is not an Unreal-runtime mod writer.

**Rebuild of mixed layouts** (localization in a pak next to IoStore
containers, the usual UE5 layout) writes the same mod pak plus an empty
`ailocalpack_P.utoc` (TOC header only, no chunks, the game's TOC version) and an
empty `ailocalpack_P.ucas`. UE5 mounts a pak only together with its utoc/ucas:
on Golfing in Aether (UE 5.3+, macOS, 2026-11-08) the pak alone was never
opened, neither in `~mods` nor in `Paks`; with the empty pair the mod's locres
replaced the game's (English overlay and the game's French culture, screenshots
in the session notes), re-apply and uninstall restored the exact original files.
Localization stored inside IoStore is still refused on rebuild ("can read it but
can't write a mod for it yet"). CUE4Parse can't parse a chunk-less TOC past its
header; the engine accepts it.

Culture behavior: an existing culture is matched with regional and alias
fallbacks (above); otherwise the native culture is overlaid and the player is
told which language to keep in game. Cooked assets/Blueprints, custom
localization formats and font replacement remain unsupported.

## Synthetic CLI check

```sh
ailocalpack dev fixture unreal /tmp/unreal-fixture --locres-version 3
ailocalpack toolkit extract /tmp/unreal-fixture --out /tmp/unreal.json --lang ru
# Edit one value under strings in /tmp/unreal.json.
ailocalpack toolkit rebuild /tmp/unreal-fixture --strings /tmp/unreal.json --out /tmp/unreal-staging
```

The fixture also accepts `--mac-bundle`. Format, failure-path, culture, CLI and
install/reapply/uninstall tests use synthetic data only.
