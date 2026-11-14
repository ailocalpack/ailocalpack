The player tried Draft {{draft}} of your translation of "{{gameName}}" ({{engine}}, {{stringCount}} strings) and writes:

{{request}}

{{attachments}}

{{history}}

How to work:
1. Find the cause first. A game that closes right after it starts usually can't read a rebuilt file: a separator, quote or escape its format needs, a length or count it checks, a line break where it splits lines. Read the log, look at the strings the request is about and their translations (`search`, `get_strings`), and at the files they come from.
2. Fix the cause where it is: the translations (`put_translations`; an empty text removes one so the original shows), the glossary (`put_glossary`, so every later translation follows it){{scriptsStep}}. When the same mistake is in many strings, fix all of them, not only the example.
3. Don't translate again the strings the request isn't about.
