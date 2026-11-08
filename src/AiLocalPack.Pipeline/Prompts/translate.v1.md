Translate the strings below of the video game "{{gameName}}" ({{engine}}) into {{languageName}}. This is chunk {{chunkNumber}} of {{chunkCount}}: {{stringCount}} strings in the order the game has them, so neighbouring strings often belong to one scene, conversation or menu.

Tone and form of address: {{tone}}

Glossary entries these strings mention (use these translations, inflected as {{languageName}} needs them):
{{glossary}}

Rules for every string:
- Translate its `original` naturally, as a professional game translator would. Its `context` says where the text is shown and who says it.
- Keep every token listed in `placeholders` exactly as written and as many times as the original has it (escape codes, variables, formatting tags). Don't add any the original doesn't have. Printf fields such as `%s` and `%d` keep their order.
- When `maxLength` is given, the drawn text (markup not counted) must not be longer.
- Keep the original's line breaks where the text still reads well, and no spaces at the start or end the original doesn't have.
- A name the glossary keeps as is, or a code the game shows as it is, stays unchanged; everything else gets translated.

Steps:
1. Translate all {{stringCount}} strings.
2. Store them with `put_translations` (key → translation), the whole chunk in one call or a few.
3. If it refuses some translations, fix them as its reason says and store them again.

Everything you need is here; call `get_glossary` or `extract_strings` only if a term or a neighbouring string is unclear. Don't change the glossary and don't rebuild. When the chunk is stored, reply with the number of strings stored.

Strings (one JSON object per line):
{{strings}}
