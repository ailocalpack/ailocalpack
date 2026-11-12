using System.Globalization;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Validation;

namespace AiLocalPack.Toolkit.Recipes.Unity;

/// <summary>Direct asset edits, independent of Mono/IL2CPP and the player's OS. Never loads game assemblies.</summary>
public sealed class UnityRecipe : IRecipeTool
{
    public string Id => "unity";
    public Version Version => new(1, 0, 0);
    public GameEngine Engine => GameEngine.Unity;

    public TextMarkup Markup => UnityText.Markup;

    public RecipeExtraction Extract(string gameDir, CancellationToken cancellationToken = default)
    {
        var strings = new List<ExtractedString>();
        var files = new List<string>();
        Visit(gameDir, cancellationToken, (path, _, texts) =>
        {
            if (texts.Count > 0)
            {
                files.Add(path);
            }

            strings.AddRange(texts.Select(t => new ExtractedString(t.Key, t.Original, t.Context, null, UnityText.Placeholders(t.Original))));
        });
        if (strings.Count == 0)
        {
            throw new RecipeException("No supported Unity texts found. This tool reads text/JSON/NGUI CSV TextAssets and text fields with embedded type trees or Mono metadata. Stripped IL2CPP objects, compiled script strings and encrypted resources need a different recipe.");
        }

        return new RecipeExtraction(Id, Version, [.. strings.OrderBy(s => s.Key, StringComparer.Ordinal)], [.. files.Order(StringComparer.Ordinal)]);
    }

    public RebuildResult Rebuild(string gameDir, string language, IReadOnlyDictionary<string, string> translations, IRebuildOutput output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(translations);
        ArgumentNullException.ThrowIfNull(output);
        var unknown = translations.Keys.ToHashSet(StringComparer.Ordinal);
        var changed = new List<string>();
        var applied = 0;
        Visit(gameDir, cancellationToken, (path, asset, texts) =>
        {
            var dirty = false;
            foreach (var text in texts)
            {
                unknown.Remove(text.Key);
                if (translations.TryGetValue(text.Key, out var value) && value.Length > 0 && value != text.Original)
                {
                    text.Replace(value);
                    applied++;
                    dirty = true;
                }
            }

            if (dirty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = output.Create(path);
                using var writer = new AssetsFileWriter(stream);
                asset.Save(writer);
                changed.Add(path);
            }
        });
        return new RebuildResult(changed, applied, [.. unknown.Order(StringComparer.Ordinal)]);
    }

    public IReadOnlyList<string> TouchedFiles(string gameDir, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var wanted = keys.ToHashSet(StringComparer.Ordinal);
        var files = new List<string>();
        Visit(gameDir, CancellationToken.None, (path, _, texts) =>
        {
            if (texts.Any(t => wanted.Contains(t.Key)))
            {
                files.Add(path);
            }
        });
        return files;
    }

    private static void Visit(string gameDir, CancellationToken ct, Action<string, UnityAsset, List<UnityString>> visit)
    {
        ArgumentException.ThrowIfNullOrEmpty(gameDir);
        var root = Path.GetFullPath(gameDir);
        if (new EngineDetector().Detect(root).Kind.Engine != GameEngine.Unity)
        {
            throw new RecipeException($"Not a Unity game: {root}");
        }

        try
        {
            // Recursive discovery also covers macOS bundles; links are never followed.
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            foreach (var full in Directory.EnumerateFiles(root, "*", options).Where(IsAssets).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.GetRelativePath(root, full).Replace('\\', '/');
                try
                {
                    using var asset = new UnityAsset(full);
                    visit(path, asset, asset.Strings(path, ct));
                }
                catch (Exception e) when (e is not OperationCanceledException and not RecipeException and not OutOfMemoryException)
                {
                    throw new RecipeException($"Can't read or rebuild Unity assets {path}: {e.Message}", path, e);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new RecipeException($"Can't read Unity game: {e.Message}", null, e);
        }
    }

    private static bool IsAssets(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".assets", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase) || name == "mainData"
            || name.StartsWith("level", StringComparison.Ordinal) && name.AsSpan(5).Length > 0 && name.AsSpan(5).IndexOfAnyExceptInRange('0', '9') < 0;
    }
}

internal sealed record UnityString(string Key, string Original, string Context, Action<string> Replace);

internal sealed class UnityAsset : IDisposable
{
    private readonly AssetsManager _manager = new() { UseTemplateFieldCache = false };
    private readonly AssetsFileInstance _instance;
    private readonly List<Action> _commit = [];
    private readonly BundleFileInstance? _bundle;
    private readonly List<UnityAsset> _children = [];
    private readonly bool _ownsManager = true;

    public UnityAsset(string path)
    {
        // Our own stream: AssetsTools leaves the one it opens itself unclosed when the file fails to parse, and Windows
        // then can't delete or replace the file until the GC runs.
        FileStream? stream = null;
        try
        {
            stream = File.OpenRead(path);
            Span<byte> signature = stackalloc byte[8];
            var bundle = stream.Read(signature) == signature.Length && signature.SequenceEqual("UnityFS\0"u8);
            stream.Position = 0;
            if (bundle)
            {
                _bundle = _manager.LoadBundleFile(stream, path, true);
                _instance = null!;
                return;
            }

            _instance = _manager.LoadAssetsFile(stream, path, false, null);
            var managed = Path.Combine(Path.GetDirectoryName(path)!, "Managed");
            if (!_instance.file.Metadata.TypeTreeEnabled && Directory.Exists(managed) && Directory.EnumerateFiles(managed, "*.dll").Any())
            {
                using var types = typeof(UnityAsset).Assembly.GetManifestResourceStream("AiLocalPack.Toolkit.Recipes.Unity.Files.classdata.tpk")!;
                _manager.LoadClassPackage(types);
                _manager.LoadClassDatabaseFromPackage(_instance.file.Metadata.UnityVersion);
                _manager.MonoTempGenerator = new MonoCecilTempGenerator(managed);
            }
        }
        catch
        {
            _manager.UnloadAll();
            stream?.Dispose();
            throw;
        }
    }

    private UnityAsset(AssetsManager manager, AssetsFileInstance instance)
    {
        _manager = manager;
        _instance = instance;
        _ownsManager = false;
    }

    public void Dispose()
    {
        if (_ownsManager)
        {
            _manager.UnloadAll();
        }
    }

    public void Save(AssetsFileWriter writer)
    {
        if (_bundle is not null)
        {
            foreach (var child in _children)
            {
                child.Commit();
            }

            _bundle.file.Write(writer);
            return;
        }

        Commit();
        _instance.file.Write(writer);
    }

    private void Commit()
    {
        foreach (var commit in _commit)
        {
            commit();
        }
    }

    public List<UnityString> Strings(string path, CancellationToken ct)
    {
        var result = new List<UnityString>();
        if (_bundle is not null)
        {
            for (var i = 0; i < _bundle.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = _bundle.file.BlockAndDirInfo.DirectoryInfos[i];
                if (!entry.IsSerialized)
                {
                    continue;
                }

                var instance = _manager.LoadAssetsFileFromBundle(_bundle, i, false)
                    ?? throw new InvalidDataException($"Can't read serialized bundle entry: {entry.Name}");
                var child = new UnityAsset(_manager, instance);
                _children.Add(child);
                result.AddRange(child.Strings("bundle/" + JsonStrings.Escape(path) + "/" + JsonStrings.Escape(entry.Name), ct));
                entry.SetNewData(instance.file);
            }

            return result;
        }

        foreach (var info in _instance.file.Metadata.AssetInfos)
        {
            ct.ThrowIfCancellationRequested();
            var prefix = path + ":" + info.PathId.ToString(CultureInfo.InvariantCulture);
            if (info.TypeId == (int)AssetClassID.TextAsset)
            {
                ReadTextAsset(info, prefix, result);
            }
            else if (info.TypeId == (int)AssetClassID.MonoBehaviour || info.TypeId < 0)
            {
                var type = _instance.file.Metadata.FindTypeTreeTypeByID(info.TypeId, info.GetScriptIndex(_instance.file));
                var embedded = _instance.file.Metadata.TypeTreeEnabled && type?.TypeBlob?.Nodes is { Count: > 0 };
                if (!embedded && _manager.MonoTempGenerator is null)
                {
                    continue;
                }

                if (info.ByteSize > 16 * 1024 * 1024)
                {
                    continue;
                }

                var reader = _instance.file.Reader;
                reader.Position = info.GetAbsoluteByteOffset(_instance.file);
                var original = reader.ReadBytes(checked((int)info.ByteSize));
                if (ReadBehaviour(info, original) is not { } read)
                {
                    continue;
                }

                var (field, tail) = read;
                var dirty = false;
                Walk(field, "", (node, pointer) =>
                {
                    if (node.TemplateField.ValueType == AssetValueType.String && UnityText.IsTextField(node.FieldName) && !string.IsNullOrWhiteSpace(node.AsString))
                    {
                        result.Add(new UnityString(prefix + pointer, node.AsString, $"Unity text field {pointer} in {path} (object {info.PathId})", value =>
                        {
                            node.AsString = value;
                            dirty = true;
                        }));
                    }
                });
                _commit.Add(() =>
                {
                    if (dirty)
                    {
                        info.SetNewData([.. field.WriteToByteArray(_instance.file.Header.Endianness), .. tail]);
                    }
                });
            }
        }

        return result;
    }

    /// <summary>
    /// The behaviour's fields and the bytes after them, or <c>null</c> when its layout can't be had (a script whose
    /// assembly or class is missing, a type the metadata doesn't describe): that object is left out, the same way
    /// on extract and rebuild, instead of failing the whole game for one plugin component.
    /// </summary>
    private (AssetTypeValueField Field, byte[] Tail)? ReadBehaviour(AssetFileInfo info, byte[] original)
    {
        try
        {
            var template = _manager.GetTemplateBaseField(_instance, info);
            using var buffer = new MemoryStream(original);
            using var bounded = new AssetsFileReader(buffer) { BigEndian = _instance.file.Header.Endianness };
            var field = template.MakeValue(bounded, 0, _manager.GetRefTypeManager(_instance));
            return (field, original.AsSpan(checked((int)buffer.Position)).ToArray());
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            return null;
        }
    }

    private static void Walk(AssetTypeValueField field, string pointer, Action<AssetTypeValueField, string> visit)
    {
        visit(field, pointer);
        for (var i = 0; i < field.Children.Count; i++)
        {
            var child = field.Children[i];
            var segment = field.TemplateField.IsArray ? i.ToString(CultureInfo.InvariantCulture) : JsonStrings.Escape(child.FieldName);
            Walk(child, pointer + "/" + segment, visit);
        }
    }

    private void ReadTextAsset(AssetFileInfo info, string prefix, List<UnityString> result)
    {
        // TextAsset's two aligned byte strings have a stable runtime layout, including files without type trees.
        // Bound every read by this object's size; binary TextAssets are ignored by UnityText.
        if (info.ByteSize > 16 * 1024 * 1024)
        {
            return;
        }

        var reader = _instance.file.Reader;
        reader.Position = info.GetAbsoluteByteOffset(_instance.file);
        var bytes = reader.ReadBytes(checked((int)info.ByteSize));
        using var stream = new MemoryStream(bytes);
        using var data = new AssetsFileReader(stream) { BigEndian = _instance.file.Header.Endianness };
        byte[] ReadString()
        {
            var size = data.ReadInt32();
            if (size < 0 || size > stream.Length - stream.Position)
            {
                throw new InvalidDataException("Invalid TextAsset string length.");
            }

            var value = data.ReadBytes(size);
            data.Align();
            return value;
        }

        var name = Encoding.UTF8.GetString(ReadString());
        var nameEnd = checked((int)stream.Position);
        var body = ReadString();
        var tail = bytes.AsSpan(checked((int)stream.Position)).ToArray();
        var edits = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in UnityText.Read(name, body))
        {
            result.Add(new UnityString(prefix + "/script" + part.Key, part.Value, $"TextAsset {name} in {prefix}", value => edits[part.Key] = value));
        }

        _commit.Add(() =>
        {
            if (edits.Count == 0)
            {
                return;
            }

            using var rewritten = new MemoryStream();
            using (var writer = new AssetsFileWriter(rewritten) { BigEndian = _instance.file.Header.Endianness })
            {
                writer.Write(bytes.AsSpan(0, nameEnd).ToArray());
                var replacement = UnityText.Replace(name, body, edits);
                writer.Write(replacement.Length);
                writer.Write(replacement);
                writer.Align();
                writer.Write(tail);
                info.SetNewData(rewritten.ToArray());
            }
        });
    }
}
