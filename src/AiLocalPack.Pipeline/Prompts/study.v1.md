Study the video game "{{gameName}}" ({{engine}}) before it is translated into {{languageName}}, and write the game brief.

The toolkit found {{stringCount}} translatable strings in {{fileCount}} files of the game. Later steps build a glossary and translate the strings in chunks of about 50; every one of them gets your brief, and it is all they know about the game beyond their chunk. The brief is what lets a chunk in the middle of the game sound like the rest of it, so it decides how good the translation reads.

Steps:

1. Read every string with `extract_strings` (`limit` 200, then the next `offset`), in order: the order and the context of each string tell who speaks, to whom and in which scene. With more than 20,000 strings, read the first 20,000 and use `search` for the rest.
{{webStep}}
3. Write the brief in English, in Markdown, under these headings:
   - `## Setting`: the world, the era, the genre and the mood; the style of the writing (short or long sentences, slang, jargon, archaic words, humour).
   - `## Characters`: one line per character who speaks or is spoken about often: who they are, gender, age when it shows, how they talk, and how they address the player and the other characters (formal or informal, nicknames).
   - `## Relationships`: who is formal or informal with whom, and where that changes in the story.
   - `## The player`: how the game addresses the player, and whether the player's gender or name is chosen (strings that must work for any choice).
   - `## Recurring lines`: catchphrases, running jokes, wordplay, songs, rhymes and lines quoted again later, which must be translated the same way every time; and how to handle the wordplay in {{languageName}}.
   - `## Interface`: the tone of menus, hints and item texts.
   - `## Notes for {{languageName}}`: anything a translator into {{languageName}} must decide once for the whole game.
4. Quote at most a few words of the original text where an example is needed; describe instead of copying.

Don't build the glossary and don't translate anything now: those are the next steps. Your final reply is the brief itself, starting with `## Setting` and nothing before it.
