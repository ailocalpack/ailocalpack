using System.Collections;

namespace AiLocalPack.Core.Steam;

/// <summary>A value in a Valve KeyValues (VDF/ACF) document: a <see cref="VdfString"/> or a nested <see cref="VdfObject"/>.</summary>
public abstract class VdfValue
{
    private protected VdfValue()
    {
    }
}

/// <summary>A string value. VDF has no other scalar types: numbers are strings too.</summary>
public sealed class VdfString(string value) : VdfValue
{
    public string Value { get; } = value ?? throw new ArgumentNullException(nameof(value));

    public override string ToString() => Value;
}

/// <summary>One <c>"key" value</c> pair of a <see cref="VdfObject"/>.</summary>
public readonly record struct VdfProperty(string Key, VdfValue Value);

/// <summary>
/// A block of properties in file order. Keys are looked up case-insensitively (Steam writes both <c>"LibraryFolders"</c>
/// and <c>"libraryfolders"</c>); VDF allows duplicate keys, and a lookup returns the first one.
/// </summary>
public sealed class VdfObject : VdfValue, IReadOnlyList<VdfProperty>
{
    private readonly VdfProperty[] _properties;
    private readonly Dictionary<string, VdfValue> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public VdfObject(IEnumerable<VdfProperty> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        _properties = [.. properties];
        foreach (var (key, value) in _properties)
        {
            _byKey.TryAdd(key, value);
        }
    }

    public int Count => _properties.Length;

    public VdfProperty this[int index] => _properties[index];

    /// <summary>The first value under <paramref name="key"/> (any case), or <c>null</c>.</summary>
    public VdfValue? this[string key] => _byKey.GetValueOrDefault(key);

    /// <summary>The string under <paramref name="key"/>, or <c>null</c> when missing or an object.</summary>
    public string? GetString(string key) => (this[key] as VdfString)?.Value;

    /// <summary>The object under <paramref name="key"/>, or <c>null</c> when missing or a string.</summary>
    public VdfObject? GetObject(string key) => this[key] as VdfObject;

    public IEnumerator<VdfProperty> GetEnumerator() => ((IEnumerable<VdfProperty>)_properties).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
