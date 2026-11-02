using System.Text;

namespace AiLocalPack.Toolkit.Engines;

/// <summary>Marker rules for one engine. Run on the game folder, its subfolders, and the macOS <c>.app</c> bundles in them.</summary>
internal abstract class EngineRule
{
    /// <summary>Windows / Linux layout with <paramref name="folder"/> as the build's top folder.</summary>
    public abstract IEnumerable<EngineMatch> MatchFolder(FolderView folder);

    /// <summary>macOS layout inside the bundle <paramref name="app"/> (<c>Game.app</c>); its main executable is the bundle.</summary>
    public abstract IEnumerable<EngineMatch> MatchApp(FolderView app);

    protected static IEnumerable<EngineMatch> AsBundle(IEnumerable<EngineMatch> matches, FolderView app) =>
        matches.Select(m => m with { MainExecutable = BundleExecutable(app) });

    /// <summary>
    /// The bundle path; for the game folder itself (the player picked <c>Game.app</c>, or Steam installed a bare
    /// <c>Contents/</c>) the binary in <c>Contents/MacOS</c>.
    /// </summary>
    protected static string? BundleExecutable(FolderView app) =>
        app.Relative.Length > 0 ? app.Relative
        : app.At("Contents/MacOS") is { } macOs && macOs.FileNames.FirstOrDefault() is { } binary ? macOs.RelativeOf(binary)
        : null;
}

/// <summary>
/// Unity: <c>&lt;Name&gt;_Data/</c> with player data next to <c>&lt;Name&gt;.exe</c> / <c>.x86_64</c> and <c>UnityPlayer.dll</c> /
/// <c>.so</c>; macOS <c>Contents/Resources/Data</c> and <c>Contents/Frameworks/UnityPlayer.dylib</c>. IL2CPP when
/// <c>GameAssembly</c> or <c>il2cpp_data/</c> is there, Mono when <c>Managed/</c> is.
/// </summary>
internal sealed class UnityRule : EngineRule
{
    private static readonly string[] DataFiles = ["globalgamemanagers", "mainData", "data.unity3d"];
    private static readonly string[] DataDirs = ["il2cpp_data", "Managed"];

    public override IEnumerable<EngineMatch> MatchFolder(FolderView folder)
    {
        var player = folder.File("UnityPlayer.dll") ?? folder.File("UnityPlayer.so");
        var il2Cpp = folder.File("GameAssembly.dll") ?? folder.File("GameAssembly.so");
        var found = false;
        foreach (var name in folder.DirNames.Where(n => n.EndsWith("_Data", StringComparison.OrdinalIgnoreCase) && n.Length > 5))
        {
            var data = folder.Dir(name)!;
            var marker = DataMarker(data);
            if (marker is null && player is null)
            {
                continue;
            }

            var exe = Executables.Named(folder, name[..^5]);
            var confidence = marker is not null && (player is not null || exe is not null) ? EngineConfidence.High : EngineConfidence.Medium;
            found = true;
            yield return new EngineMatch(Kind(data, il2Cpp), confidence, marker ?? player!, exe);
        }

        if (!found && player is not null)
        {
            yield return new EngineMatch(new EngineKind(GameEngine.Unity, il2Cpp is null ? null : EngineVariants.Il2Cpp),
                EngineConfidence.Low, player, Executables.In(folder).FirstOrDefault());
        }
    }

    public override IEnumerable<EngineMatch> MatchApp(FolderView app)
    {
        var data = app.At("Contents/Resources/Data");
        var marker = data is null ? null : DataMarker(data);
        var player = app.FileAt("Contents/Frameworks/UnityPlayer.dylib");
        if (marker is null && player is null)
        {
            yield break;
        }

        var confidence = marker is not null && player is not null ? EngineConfidence.High : EngineConfidence.Medium;
        var kind = data is null
            ? new EngineKind(GameEngine.Unity)
            : Kind(data, app.FileAt("Contents/Frameworks/GameAssembly.dylib"));
        yield return new EngineMatch(kind, confidence, marker ?? player!, BundleExecutable(app));
    }

    private static string? DataMarker(FolderView data) =>
        DataFiles.Select(data.File).FirstOrDefault(f => f is not null)
        ?? DataDirs.Select(d => data.Dir(d)?.Relative).FirstOrDefault(d => d is not null);

    private static EngineKind Kind(FolderView data, string? gameAssembly)
    {
        var variant = gameAssembly is not null || data.Dir("il2cpp_data") is not null ? EngineVariants.Il2Cpp
            : data.Dir("Managed") is not null ? EngineVariants.Mono
            : null;
        return new EngineKind(GameEngine.Unity, variant);
    }
}

/// <summary>
/// Unreal: <c>&lt;Project&gt;/Content/Paks/*.pak|*.utoc</c>, with <c>Engine/</c> beside the project for full confidence.
/// Version from the macOS bundle folder (<c>Contents/UE4</c> vs <c>Contents/UE</c>) or the build branch compiled into the
/// shipping binary (<c>++UE5+Release-5.3</c>); games built from a custom engine branch get no version.
/// </summary>
internal sealed class UnrealRule : EngineRule
{
    private static readonly string[] Platforms = ["Win64", "Linux", "LinuxArm64", "Mac"];

    private static readonly (byte[] Pattern, string Variant)[] Branches =
    [
        .. new[] { ("+UE5+Release-", EngineVariants.Unreal5), ("+UE4+Release-", EngineVariants.Unreal4) }
            .SelectMany(b => new[] { (Encoding.Unicode.GetBytes(b.Item1), b.Item2), (Encoding.ASCII.GetBytes(b.Item1), b.Item2) }),
    ];

    public override IEnumerable<EngineMatch> MatchFolder(FolderView folder) => Match(folder, scanBinary: true);

    public override IEnumerable<EngineMatch> MatchApp(FolderView app)
    {
        foreach (var (name, variant) in new[] { ("UE4", EngineVariants.Unreal4), ("UE", EngineVariants.Unreal5) })
        {
            if (app.At($"Contents/{name}") is { } inner)
            {
                foreach (var match in AsBundle(Match(inner, scanBinary: false), app))
                {
                    yield return match with { Kind = new EngineKind(GameEngine.Unreal, variant) };
                }
            }
        }
    }

    private static IEnumerable<EngineMatch> Match(FolderView folder, bool scanBinary)
    {
        var engine = folder.Dir("Engine");
        foreach (var name in folder.DirNames.Where(n => !n.Equals("Engine", StringComparison.OrdinalIgnoreCase)))
        {
            var project = folder.Dir(name)!;
            var paks = project.At("Content/Paks");
            var pak = paks?.FileNames.FirstOrDefault(f =>
                f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase));
            if (pak is null)
            {
                continue;
            }

            var binaries = project.Dir("Binaries");
            var confidence = engine is not null ? EngineConfidence.High
                : binaries is not null ? EngineConfidence.Medium
                : EngineConfidence.Low;
            var shipping = binaries is null ? null : ShippingBinary(binaries);
            var exe = shipping?.Relative ?? Executables.Named(folder, name) ?? folder.File($"{name}.sh");
            var variant = scanBinary && shipping is { } binary ? BranchVariant(binary.FullPath) : null;
            yield return new EngineMatch(new EngineKind(GameEngine.Unreal, variant), confidence, paks!.RelativeOf(pak), exe);
        }
    }

    /// <summary><c>Binaries/&lt;Platform&gt;/&lt;Project&gt;-&lt;Platform&gt;-Shipping[.exe]</c>, Windows first.</summary>
    private static (string Relative, string FullPath)? ShippingBinary(FolderView binaries)
    {
        var platforms = binaries.DirNames
            .OrderBy(p => Array.FindIndex(Platforms, k => k.Equals(p, StringComparison.OrdinalIgnoreCase)) is var i && i >= 0 ? i : Platforms.Length)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase);
        foreach (var platform in platforms)
        {
            var dir = binaries.Dir(platform)!;
            var shipping = dir.FileNames
                .Where(f => f.Contains("-Shipping", StringComparison.OrdinalIgnoreCase)
                    && (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Executables.IsLinuxBinary(dir, f)))
                .OrderBy(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
            if (shipping is not null)
            {
                return (dir.RelativeOf(shipping), dir.FullPathOf(shipping));
            }
        }

        return null;
    }

    private static string? BranchVariant(string binaryPath)
    {
        var found = BinaryProbe.FindFirst(binaryPath, [.. Branches.Select(b => b.Pattern)]);
        return found < 0 ? null : Branches[found].Variant;
    }
}

/// <summary>RPG Maker MV (<c>www/js/rpg_core.js</c>) and MZ (<c>js/rmmz_core.js</c>); macOS keeps the same tree in <c>Contents/Resources/app.nw</c>.</summary>
internal sealed class RpgMakerRule : EngineRule
{
    private static readonly (string Path, string Variant)[] Cores =
    [
        ("js/rmmz_core.js", EngineVariants.RpgMakerMz),
        ("www/js/rmmz_core.js", EngineVariants.RpgMakerMz),
        ("www/js/rpg_core.js", EngineVariants.RpgMakerMv),
        ("js/rpg_core.js", EngineVariants.RpgMakerMv),
    ];

    public override IEnumerable<EngineMatch> MatchFolder(FolderView folder)
    {
        foreach (var (path, variant) in Cores)
        {
            if (folder.FileAt(path) is { } core)
            {
                yield return new EngineMatch(
                    new EngineKind(GameEngine.RpgMaker, variant), EngineConfidence.High, core, Executables.NamedOrFirst(folder, "Game"));
                yield break;
            }
        }
    }

    public override IEnumerable<EngineMatch> MatchApp(FolderView app) =>
        app.At("Contents/Resources/app.nw") is { } inner ? AsBundle(MatchFolder(inner), app) : [];
}

/// <summary>Ren'Py: <c>renpy/</c> and <c>game/</c> side by side; macOS keeps them in <c>Contents/Resources/autorun</c>.</summary>
internal sealed class RenPyRule : EngineRule
{
    private static readonly string[] EngineFiles = ["__init__.py", "__init__.pyc", "__init__.pyo", "bootstrap.py"];
    private static readonly string[] ScriptExtensions = [".rpa", ".rpyc", ".rpy"];

    public override IEnumerable<EngineMatch> MatchFolder(FolderView folder)
    {
        if (folder.Dir("renpy") is not { } renpy || folder.Dir("game") is not { } game)
        {
            yield break;
        }

        var engineFile = EngineFiles.Select(renpy.File).FirstOrDefault(f => f is not null);
        var hasScripts = game.FileNames.Any(f => ScriptExtensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)));
        var confidence = engineFile is not null && hasScripts ? EngineConfidence.High : EngineConfidence.Medium;
        yield return new EngineMatch(new EngineKind(GameEngine.RenPy), confidence, engineFile ?? renpy.Relative, Launcher(folder));
    }

    public override IEnumerable<EngineMatch> MatchApp(FolderView app) =>
        app.At("Contents/Resources/autorun") is { } inner ? AsBundle(MatchFolder(inner), app) : [];

    /// <summary>Ren'Py names its launchers after the <c>&lt;Name&gt;.py</c> script: <c>Name.exe</c>, <c>Name.sh</c>.</summary>
    private static string? Launcher(FolderView folder)
    {
        foreach (var script in folder.FileNames.Where(f => f.EndsWith(".py", StringComparison.OrdinalIgnoreCase)))
        {
            var stem = script[..^3];
            if ((folder.File($"{stem}.exe") ?? folder.File($"{stem}.sh")) is { } launcher)
            {
                return launcher;
            }
        }

        return Executables.In(folder).FirstOrDefault();
    }
}

/// <summary>
/// Godot: a <c>.pck</c> starting with the <c>GDPC</c> magic (Wwise sound banks share the extension, so the magic is
/// checked), or a pack embedded in the executable (file ends with the pack size and <c>GDPC</c>). Major version from the
/// pack header. macOS keeps the pack in <c>Contents/Resources</c>.
/// </summary>
internal sealed class GodotRule : EngineRule
{
    private const uint Magic = 0x43504447; // "GDPC"
    private const int HeaderSize = 20; // magic, pack format, major, minor, patch
    private const int TrailerSize = 12; // pack size (u64), magic

    public override IEnumerable<EngineMatch> MatchFolder(FolderView folder)
    {
        foreach (var pck in folder.FileNames.Where(f => f.EndsWith(".pck", StringComparison.OrdinalIgnoreCase)))
        {
            if (ReadHeader(folder.FullPathOf(pck), 0) is { } major)
            {
                yield return new EngineMatch(Kind(major), EngineConfidence.High, folder.RelativeOf(pck), Executables.NamedOrFirst(folder, pck[..^4]));
                yield break;
            }
        }

        foreach (var exe in Executables.In(folder))
        {
            if (ReadEmbedded(folder.FullPathOf(Path.GetFileName(exe))) is { } major)
            {
                yield return new EngineMatch(Kind(major), EngineConfidence.High, exe, exe);
                yield break;
            }
        }
    }

    public override IEnumerable<EngineMatch> MatchApp(FolderView app) =>
        app.At("Contents/Resources") is { } resources ? AsBundle(MatchFolder(resources), app) : [];

    private static EngineKind Kind(uint major) =>
        new(GameEngine.Godot, major is > 0 and < 100 ? major.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);

    /// <summary>Major version from a pack header at <paramref name="offset"/>; 0 = valid pack, version unreadable; <c>null</c> = not a pack.</summary>
    private static uint? ReadHeader(string path, long offset) =>
        BinaryProbe.ReadAt(path, offset, HeaderSize) is { } header && BinaryProbe.ReadUInt32(header, 0) == Magic
            ? BinaryProbe.ReadUInt32(header, 8)
            : null;

    private static uint? ReadEmbedded(string path)
    {
        if (BinaryProbe.Length(path) is not { } length
            || BinaryProbe.ReadAt(path, length - TrailerSize, TrailerSize) is not { } trailer
            || BinaryProbe.ReadUInt32(trailer, 8) != Magic)
        {
            return null;
        }

        var size = BinaryProbe.ReadUInt64(trailer, 0);
        var start = size < (ulong)length ? length - TrailerSize - (long)size : -1;
        return start >= 0 && ReadHeader(path, start) is { } major ? major : 0;
    }
}
