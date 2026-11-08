An earlier attempt at chunk {{chunkNumber}} of {{chunkCount}} of the video game "{{gameName}}" ({{engine}}) left {{stringCount}} strings without a valid translation into {{languageName}}. Each string below says why under `problem`. Translate them again.

Tone and form of address: {{tone}}

Glossary entries these strings mention (use these translations, inflected as {{languageName}} needs them):
{{glossary}}

Rules for every string:
- Translate its `original` naturally; its `context` says where the text is shown and who says it.
- Keep every token listed in `placeholders` exactly as written and as many times as the original has it. Don't add any the original doesn't have. Printf fields such as `%s` and `%d` keep their order.
- When `maxLength` is given, the drawn text (markup not counted) must not be longer: shorten the wording, use common abbreviations if you must.
- No spaces at the start or end the original doesn't have.

Store them with `put_translations` (key → translation). If it refuses some, fix them as its reason says and store them again. Then reply with the number of strings stored.

Strings (one JSON object per line):
{{strings}}
