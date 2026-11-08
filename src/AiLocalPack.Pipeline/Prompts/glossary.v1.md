Build the glossary for translating the video game "{{gameName}}" ({{engine}}) into {{languageName}}.

The toolkit found {{stringCount}} translatable strings in {{fileCount}} files of the game. A later step translates them in chunks of about 50 strings, each chunk with the glossary entries it mentions. The glossary is what keeps names, terms and tone the same across the whole game, so it decides how consistent the translation reads.

Steps:

1. Call `get_glossary`. A glossary may already exist from an earlier run that was interrupted: keep its entries and complete it instead of starting over.
2. Read the strings with `extract_strings` (`limit` 200, then the next `offset`). With up to 3,000 strings, read them all. With more, read at least the first 3,000 and the interface texts, then use `search` to see how often a candidate term occurs and in which contexts. The context of a string says who speaks it and where it is shown.
3. Pick the entries that matter:
   - proper names: characters, places, factions, items, skills, enemies;
   - terms of the game's world and mechanics, and invented words;
   - interface words used across many strings (stats, menu commands, currency).
   Leave out ordinary words that need no special care. A typical game needs 20 to 150 entries.
4. For each entry:
   - `term`: exactly as the game writes it;
   - `translation`: how it is written in {{languageName}}, in its base form; follow the usual conventions of {{languageName}} for transliterating or translating names;
   - `keepAsIs`: true for names that stay as they are (brands, titles the game never translates);
   - `note`: who or what it is and anything a translator must know (for a character: gender and how they speak);
   - `partOfSpeech` and `gender`: when {{languageName}} inflects words or makes other words agree with them.
5. Decide the tone and form of address: how formal the game is, whether characters address the player and each other formally or informally (when {{languageName}} makes the difference), and the style of the interface texts. One or two sentences.
6. Save with `put_glossary`: the entries (up to 50 per call) and the `tone`.

Don't translate the strings themselves now: `put_translations` belongs to the later step. When the glossary is saved, reply with the number of entries and the tone you chose.
