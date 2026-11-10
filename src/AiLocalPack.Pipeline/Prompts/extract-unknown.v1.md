The toolkit can't read the texts of the video game "{{gameName}}" ({{engine}}). Write your own extractor and rebuilder for its files. The copy of the game is at `{{gamePath}}` ({{fileCount}} files).

Steps:
1. Explore the game files: `detect_engine`, `list_files`, `read_file_snippet` and shell commands. Find where the texts the player sees are kept: dialogue, menus, item and skill descriptions, interface. Leave out what the player never sees (internal IDs, file names, code, debug texts).
2. Write `scripts/extract.<ext>`: it takes the game folder and an output file, and writes the output as UTF-8 JSON `{"strings": [{"key", "original", "file", "context", "maxLength", "placeholders"}]}`:
   - `key`: unique, and the same on every run: built from the file and the text's place or ID in it (e.g. `data/text.bin:42`), never from the text itself.
   - `original`: the text as the player sees it, decoded (no length prefixes or terminators).
   - `file`: the game file it is in, relative to the game folder, with `/` between folders.
   - `context`: a few English words on where the text is shown and who says it (optional).
   - `maxLength`: only when the format fixes the text's size, in characters (optional).
   - `placeholders`: every token in the original the game replaces or interprets (variables, control codes, formatting tags), exactly as written in `original` (optional).
3. Write `scripts/rebuild.<ext>`: it takes the game folder, a translations file `{"language": "…", "strings": {key: translation}}` and an output folder. For every game file a translation changes, it writes the whole file again into the output folder at the same relative path, in the encoding the game reads (keep the original encoding unless the format can hold UTF-8), with every length, offset, count or checksum the format keeps updated. A key without a translation keeps its original text; a file with no translated text is not written. It never writes into the game folder.
4. Write `scripts/README.md`: the file format as you understood it, and the exact commands that run both scripts from the workspace, with `<game>` standing for the game folder.
5. Run the extractor on `{{gamePath}}` into `strings.json` in the workspace. Then call `extract_strings` with `refresh` true: it reads `strings.json` as the app will; fix whatever it refuses and run again.
6. Check the round trip in `check/`:
   - Rebuild with no translations (`{"language": "{{languageTag}}", "strings": {}}`): no file may be written.
   - Rebuild with a few translations made longer and written in {{languageName}} letters (and one with each kind of placeholder kept). Copy the game files your strings come from into `check/game/`, lay the rebuilt files over them, run the extractor on `check/game/` and compare: every key the same, the test translations read back exactly, every other text unchanged.
   - Fix the scripts until both checks pass.
7. Don't translate the game yet: don't call `put_translations` or `put_glossary`.

Reply with the number of strings and files your extractor found and the language the scripts are written in.
