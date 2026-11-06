using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiLocalPack.Toolkit.Recipes.RpgMaker;

/// <summary>A translatable string of one data file: JSON Pointer, text, note for the translator.</summary>
internal readonly record struct RpgMakerString(string Pointer, string Text, string Context);

/// <summary>
/// Which strings of an RPG Maker MV / MZ data file are player-visible text. Walks a parsed file defensively: an
/// unexpected shape gives fewer strings, never an error. Plugin parameters, notes, comments and script commands are
/// left out (code or plugin data, not text).
/// </summary>
internal static partial class RpgMakerStrings
{
    /// <summary>Database files and their text fields (entries are an array, index 0 is null).</summary>
    private static readonly Dictionary<string, (string Kind, (string Field, string Note)[] Fields)> Database = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Actors.json"] = ("Actor", [("name", "Actor name"), ("nickname", "Actor nickname"), ("profile", "Actor profile (up to 2 lines)")]),
        ["Classes.json"] = ("Class", [("name", "Class name")]),
        ["Items.json"] = ("Item", [("name", "Item name"), ("description", "Item description (up to 2 lines)")]),
        ["Weapons.json"] = ("Weapon", [("name", "Weapon name"), ("description", "Weapon description (up to 2 lines)")]),
        ["Armors.json"] = ("Armor", [("name", "Armor name"), ("description", "Armor description (up to 2 lines)")]),
        ["Skills.json"] =
        ("Skill", [
            ("name", "Skill name"),
            ("description", "Skill description (up to 2 lines)"),
            ("message1", "Battle message when the skill is used, shown after the user's name"),
            ("message2", "Second line of the battle message when the skill is used"),
        ]),
        ["States.json"] =
        ("State", [
            ("name", "State name"),
            ("message1", "Battle message when an actor gets the state, shown after the name"),
            ("message2", "Battle message when an enemy gets the state, shown after the name"),
            ("message3", "Battle message while the state lasts, shown after the name"),
            ("message4", "Battle message when the state ends, shown after the name"),
        ]),
        ["Enemies.json"] = ("Enemy", [("name", "Enemy name")]),
    };

    /// <summary>System.json arrays of names.</summary>
    private static readonly (string Field, string Note)[] SystemLists =
    [
        ("elements", "Element name"),
        ("skillTypes", "Skill type"),
        ("weaponTypes", "Weapon type"),
        ("armorTypes", "Armor type"),
        ("equipTypes", "Equipment slot"),
    ];

    private static readonly (string Field, string Note)[] TermLists =
    [
        ("basic", "Basic term (level, HP, MP, …)"),
        ("commands", "Menu or battle command"),
        ("params", "Parameter name"),
    ];

    /// <summary>Whether the tool reads this file of the <c>data</c> folder.</summary>
    public static bool IsTextFile(string fileName) =>
        Database.ContainsKey(fileName) || MapFile().IsMatch(fileName)
        || fileName.Equals("System.json", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("CommonEvents.json", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("Troops.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>The strings of a data file named <paramref name="fileName"/>, in document order.</summary>
    public static List<RpgMakerString> Extract(string fileName, JsonElement root)
    {
        var found = new List<RpgMakerString>();
        if (fileName.Equals("System.json", StringComparison.OrdinalIgnoreCase))
        {
            System(root, found);
        }
        else if (Database.TryGetValue(fileName, out var database))
        {
            foreach (var (index, entry) in Entries(root))
            {
                foreach (var (field, note) in database.Fields)
                {
                    Add(found, entry, field, $"/{index}/{field}", note);
                }
            }
        }
        else if (fileName.Equals("CommonEvents.json", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (index, entry) in Entries(root))
            {
                Commands(entry, $"/{index}", found);
            }
        }
        else if (fileName.Equals("Troops.json", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (index, entry) in Entries(root))
            {
                Pages(entry, $"/{index}", found);
            }
        }
        else if (MapFile().IsMatch(fileName) && root.ValueKind == JsonValueKind.Object)
        {
            Add(found, root, "displayName", "/displayName", "Map name shown on entering the map");
            if (root.TryGetProperty("events", out var events))
            {
                foreach (var (index, entry) in Entries(events))
                {
                    Pages(entry, $"/events/{index}", found);
                }
            }
        }

        return found;
    }

    /// <summary>RPG Maker escape codes (<c>\C[2]</c>, <c>\N[1]</c>, <c>\G</c>, <c>\{</c>, plugin codes like <c>\FS[24]</c> or <c>\pop&lt;1&gt;</c>) and <c>%1</c>-style fields, in order.</summary>
    public static IReadOnlyList<string> Placeholders(string text) =>
        [.. Placeholder().Matches(text).Select(m => m.Value)];

    private static void System(JsonElement root, List<RpgMakerString> found)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        Add(found, root, "gameTitle", "/gameTitle", "Game title");
        Add(found, root, "currencyUnit", "/currencyUnit", "Currency unit, shown after amounts");
        foreach (var (field, note) in SystemLists)
        {
            if (root.TryGetProperty(field, out var list))
            {
                Strings(list, $"/{field}", note, found);
            }
        }

        if (!root.TryGetProperty("terms", out var terms) || terms.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var (field, note) in TermLists)
        {
            if (terms.TryGetProperty(field, out var list))
            {
                Strings(list, $"/terms/{field}", note, found);
            }
        }

        if (terms.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Object)
        {
            foreach (var message in messages.EnumerateObject())
            {
                Add(found, messages, message.Name, $"/terms/messages/{JsonStrings.Escape(message.Name)}",
                    $"Battle or menu message '{message.Name}'; %1, %2 are filled in by the game");
            }
        }
    }

    private static void Pages(JsonElement owner, string pointer, List<RpgMakerString> found)
    {
        if (owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var page in pages.EnumerateArray())
            {
                Commands(page, $"{pointer}/pages/{index++}", found);
            }
        }
    }

    /// <summary>Event commands of <c>owner.list</c>: messages, choices, scrolling text, name changes.</summary>
    private static void Commands(JsonElement owner, string pointer, List<RpgMakerString> found)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        string? speaker = null;
        var index = -1;
        foreach (var command in list.EnumerateArray())
        {
            index++;
            if (command.ValueKind != JsonValueKind.Object
                || !command.TryGetProperty("code", out var codeElement) || !codeElement.TryGetInt32(out var code)
                || !command.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var at = $"{pointer}/list/{index}/parameters";
            switch (code)
            {
                case 101: // Show Text; MZ keeps the speaker name in parameter 4.
                    speaker = Parameter(parameters, 4) is { } name && !string.IsNullOrWhiteSpace(name) ? name : null;
                    AddParameter(found, parameters, 4, at, "Speaker name of the message window");
                    break;
                case 401:
                    AddParameter(found, parameters, 0, at, speaker is null ? "Message line" : $"Message line, speaker: {speaker}");
                    break;
                case 102: // Show Choices: parameter 0 is the list of choices.
                    if (parameters.GetArrayLength() > 0)
                    {
                        Strings(parameters[0], $"{at}/0", "Choice", found);
                    }

                    break;
                case 402:
                    AddParameter(found, parameters, 1, at, "Label of a choice branch (editor only); use the choice's translation");
                    break;
                case 405:
                    AddParameter(found, parameters, 0, at, "Scrolling text line");
                    break;
                case 320:
                    AddParameter(found, parameters, 1, at, "Actor name (Change Name)");
                    break;
                case 324:
                    AddParameter(found, parameters, 1, at, "Actor nickname (Change Nickname)");
                    break;
                case 325:
                    AddParameter(found, parameters, 1, at, "Actor profile (Change Profile, up to 2 lines)");
                    break;
            }

            if (code is not (101 or 401))
            {
                speaker = null;
            }
        }
    }

    private static IEnumerable<(int Index, JsonElement Entry)> Entries(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var index = 0;
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object)
            {
                yield return (index, entry);
            }

            index++;
        }
    }

    private static void Strings(JsonElement array, string pointer, string note, List<RpgMakerString> found)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            Text(found, item, $"{pointer}/{index++}", note);
        }
    }

    private static string? Parameter(JsonElement parameters, int index) =>
        parameters.GetArrayLength() > index && parameters[index].ValueKind == JsonValueKind.String ? parameters[index].GetString() : null;

    private static void AddParameter(List<RpgMakerString> found, JsonElement parameters, int index, string pointer, string note)
    {
        if (parameters.GetArrayLength() > index)
        {
            Text(found, parameters[index], $"{pointer}/{index.ToString(CultureInfo.InvariantCulture)}", note);
        }
    }

    private static void Add(List<RpgMakerString> found, JsonElement owner, string field, string pointer, string note)
    {
        if (owner.TryGetProperty(field, out var value))
        {
            Text(found, value, pointer, note);
        }
    }

    /// <summary>A non-blank string value; anything else (numbers, null, empty text) is not translatable.</summary>
    private static void Text(List<RpgMakerString> found, JsonElement value, string pointer, string note)
    {
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text))
        {
            found.Add(new RpgMakerString(pointer, text, note));
        }
    }

    [GeneratedRegex(@"^Map\d{3,}\.json$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MapFile();

    [GeneratedRegex(@"\\[A-Za-z]+(?:\[[^\]\r\n]*\]|<[^>\r\n]*>)?|\\[{}$.|!><^\\]|%\d+", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
