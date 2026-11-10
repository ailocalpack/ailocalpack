Your extractor and rebuilder for the video game "{{gameName}}" are in `scripts/` of the workspace (`scripts/README.md` says how to run them). The app has written {{translationCount}} translations into {{languageName}} to `translations.json` in the workspace. Make the translated game files with your rebuild script.

Steps:
1. Empty the `out/` folder of the workspace (make it when it isn't there).
2. Run the rebuild script on the game at `{{gamePath}}` with `translations.json`, writing into `out/`.
3. Check the result: every file in `out/` must be a game file the strings come from, at the same relative path. Copy those game files into `check/game/` (emptied first), lay the files from `out/` over them, run your extractor on `check/game/` and compare with `translations.json`: every translated key must read back exactly as translated, and every other text unchanged.
4. If a check fails, fix the scripts (keep `scripts/README.md` up to date) and start again from step 1.

Don't change `translations.json` and don't call `put_translations`: the translations are final. Reply with the files in `out/`.
