The test run of your translation of "{{gameName}}" ({{engine}}, {{stringCount}} strings) crashed. What the app found:

{{failure}}

The rebuilt files, the ones holding the longest translations (in UTF-8 bytes) first:

{{files}}

The full report is `{{reportFile}}` in your working folder (JSON: the run, the OS's crash record, where the translated game's output parted from the untranslated game's, every rebuilt file with its size before and after and its longest translations beside their originals). The output of the crashed run is `{{logFile}}`.

{{attempts}}

How to work:
1. Find the file the game was reading when it crashed. The line where the translated game's output stopped and what the untranslated game printed next usually name it; else the game's log, or the crash report's module and offset.
2. Work out what in that file's translations breaks the game. Common causes: a text longer in bytes than a fixed-size buffer the game copies it into (letters outside ASCII take two or three bytes in UTF-8, so a translation of the same length in letters can be twice as long in bytes), a character, quote or escape the format doesn't allow, a line break where the game splits lines, a count, size or checksum the game checks.
3. Test your guess with `test_run` before you change much. To see whether a file's strings are the cause, empty their translations for one run (`put_translations` with an empty text keeps the original), and halve the set when it helps; put back every translation you emptied only to test.
4. Fix the cause where it is: the translations (`put_translations`){{scriptsStep}}. When the same problem is in many strings (every text over a byte limit, every string with that character), fix all of them, not only the one that crashed, and keep their meaning: a shorter synonym or a looser phrasing, never dropped information.
5. Check your fix with `test_run`: the game keeps running past the point where it crashed. You have at most {{testRuns}} runs.
6. Only as a last resort, when you can't find a fix: leave the strings that crash the game untranslated (an empty translation) and say which and why.

End with a short reply: what was wrong, what you changed, what your test runs showed. Don't list every string you changed.
