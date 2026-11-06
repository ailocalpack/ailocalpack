using System.Buffers.Binary;
using System.Text;

namespace AiLocalPack.TestKit;

/// <summary>
/// Synthetic game folders laid out like each engine's builds (Windows, Linux, macOS <c>.app</c>): marker files with
/// placeholder content, never real game files. Every method builds under <paramref name="root"/> (relative to the
/// <see cref="TempDir"/>, empty = its top) and returns the <see cref="TempDir"/> for chaining.
/// </summary>
public static class EngineFixture
{
    /// <summary>Start of a 64-bit ELF file: enough for "is this a Linux binary".</summary>
    public static byte[] Elf { get; } = [0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    public static TempDir UnityWindows(this TempDir dir, string root = "", string name = "Game", bool il2Cpp = false)
    {
        dir.File(P(root, $"{name}.exe"), "MZ")
            .File(P(root, "UnityPlayer.dll"), "MZ")
            .File(P(root, "UnityCrashHandler64.exe"), "MZ")
            .File(P(root, $"{name}_Data/globalgamemanagers"), "unity");
        return il2Cpp
            ? dir.File(P(root, "GameAssembly.dll"), "MZ").File(P(root, $"{name}_Data/il2cpp_data/Metadata/global-metadata.dat"), "x")
            : dir.File(P(root, $"{name}_Data/Managed/Assembly-CSharp.dll"), "MZ");
    }

    public static TempDir UnityLinux(this TempDir dir, string root = "", string name = "Game", bool il2Cpp = false)
    {
        dir.File(P(root, $"{name}.x86_64"), Elf)
            .File(P(root, "UnityPlayer.so"), Elf)
            .File(P(root, $"{name}_Data/globalgamemanagers"), "unity");
        return il2Cpp
            ? dir.File(P(root, "GameAssembly.so"), Elf).File(P(root, $"{name}_Data/il2cpp_data/Metadata/global-metadata.dat"), "x")
            : dir.File(P(root, $"{name}_Data/Managed/Assembly-CSharp.dll"), "MZ");
    }

    public static TempDir UnityMac(this TempDir dir, string root = "", string name = "Game", bool il2Cpp = false)
    {
        var app = P(root, $"{name}.app/Contents");
        dir.File($"{app}/MacOS/{name}", "macho")
            .File($"{app}/Frameworks/UnityPlayer.dylib", "macho")
            .File($"{app}/Resources/Data/globalgamemanagers", "unity");
        return il2Cpp
            ? dir.File($"{app}/Frameworks/GameAssembly.dylib", "macho").Dir($"{app}/Resources/Data/il2cpp_data")
            : dir.File($"{app}/Resources/Data/Managed/Assembly-CSharp.dll", "MZ");
    }

    /// <param name="branch">Build branch written into the shipping binary (UTF-16, like the engine does); <c>null</c> = none.</param>
    /// <param name="engineFolder">Whether the <c>Engine/</c> folder ships next to the project.</param>
    public static TempDir UnrealWindows(
        this TempDir dir, string root = "", string project = "Game", string? branch = "++UE5+Release-5.3", bool engineFolder = true)
    {
        dir.File(P(root, $"{project}.exe"), "MZ")
            .File(P(root, $"{project}/Binaries/Win64/{project}-Win64-Shipping.exe"), Binary("MZ", branch, Encoding.Unicode))
            .File(P(root, $"{project}/Content/Paks/{project}-Windows.pak"), "pak");
        return engineFolder ? dir.File(P(root, "Engine/Binaries/ThirdParty/readme.txt"), "x") : dir;
    }

    public static TempDir UnrealLinux(this TempDir dir, string root = "", string project = "Game", string? branch = "++UE4+Release-4.27")
    {
        return dir.File(P(root, $"{project}.sh"), "#!/bin/sh")
            .File(P(root, $"{project}/Binaries/Linux/{project}-Linux-Shipping"), Binary(Encoding.ASCII.GetString(Elf), branch, Encoding.Unicode))
            .File(P(root, $"{project}/Content/Paks/{project}-Linux.utoc"), "utoc")
            .File(P(root, "Engine/Content/readme.txt"), "x");
    }

    /// <param name="ue4">Unreal 4 stages into <c>Contents/UE4</c>, Unreal 5 into <c>Contents/UE</c>.</param>
    public static TempDir UnrealMac(this TempDir dir, string root = "", string project = "Game", bool ue4 = false)
    {
        var inner = P(root, $"{project}.app/Contents/{(ue4 ? "UE4" : "UE")}");
        return dir.File(P(root, $"{project}.app/Contents/MacOS/{project}"), "macho")
            .File($"{inner}/{project}/Content/Paks/{project}-Mac.pak", "pak")
            .File($"{inner}/Engine/Content/readme.txt", "x");
    }

    public static TempDir RpgMakerMv(this TempDir dir, string root = "") =>
        dir.File(P(root, "Game.exe"), "MZ").File(P(root, "www/js/rpg_core.js"), "//").File(P(root, "www/data/System.json"), "{}");

    public static TempDir RpgMakerMz(this TempDir dir, string root = "", string exe = "Game.exe") =>
        dir.File(P(root, exe), "MZ").File(P(root, "js/rmmz_core.js"), "//").File(P(root, "data/System.json"), "{}");

    /// <summary>
    /// A small but complete RPG Maker game with real data files (every kind of text the <c>rpgmaker</c> tool reads, and
    /// things it must leave alone), from <see cref="AiLocalPack.Toolkit.Recipes.RpgMaker.RpgMakerFixture"/>.
    /// </summary>
    /// <param name="mv">MV layout (<c>www/</c>) instead of MZ.</param>
    public static TempDir RpgMakerGame(this TempDir dir, string root = "", bool mv = false)
    {
        AiLocalPack.Toolkit.Recipes.RpgMaker.RpgMakerFixture.Create(root.Length == 0 ? dir.Path : dir.Combine(root), mv);
        return dir;
    }

    public static TempDir RpgMakerMac(this TempDir dir, string root = "", bool mz = true) =>
        dir.File(P(root, "Game.app/Contents/MacOS/nwjs"), "macho")
            .File(P(root, $"Game.app/Contents/Resources/app.nw/js/{(mz ? "rmmz_core.js" : "rpg_core.js")}"), "//");

    public static TempDir RenPy(this TempDir dir, string root = "", string name = "Game") =>
        dir.File(P(root, $"{name}.exe"), "MZ")
            .File(P(root, $"{name}-32.exe"), "MZ")
            .File(P(root, $"{name}.sh"), "#!/bin/sh")
            .File(P(root, $"{name}.py"), "# launcher")
            .File(P(root, "renpy/__init__.py"), "#")
            .File(P(root, "game/archive.rpa"), "RPA-3.0");

    public static TempDir RenPyMac(this TempDir dir, string root = "", string name = "Game") =>
        dir.File(P(root, $"{name}.app/Contents/MacOS/{name}"), "macho")
            .File(P(root, $"{name}.app/Contents/Resources/autorun/renpy/__init__.py"), "#")
            .File(P(root, $"{name}.app/Contents/Resources/autorun/game/script.rpyc"), "x");

    /// <summary>Windows export with a separate <c>&lt;name&gt;.pck</c>.</summary>
    public static TempDir GodotWindows(this TempDir dir, string root = "", string name = "Game", int major = 4) =>
        dir.File(P(root, $"{name}.exe"), "MZ").File(P(root, $"{name}.pck"), Pck(major));

    public static TempDir GodotLinux(this TempDir dir, string root = "", string name = "Game", int major = 3) =>
        dir.File(P(root, $"{name}.x86_64"), Elf).File(P(root, $"{name}.pck"), Pck(major));

    public static TempDir GodotMac(this TempDir dir, string root = "", string name = "Game", int major = 4) =>
        dir.File(P(root, $"{name}.app/Contents/MacOS/{name}"), "macho").File(P(root, $"{name}.app/Contents/Resources/{name}.pck"), Pck(major));

    /// <summary>Export with the pack embedded in the executable: binary, pack, pack size (u64), <c>GDPC</c>.</summary>
    public static TempDir GodotEmbedded(this TempDir dir, string root = "", string name = "Game.exe", int major = 4)
    {
        var pck = Pck(major);
        var trailer = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(trailer, (ulong)pck.Length);
        "GDPC"u8.CopyTo(trailer.AsSpan(8));
        return dir.File(P(root, name), [.. "MZ-godot-binary"u8, .. pck, .. trailer]);
    }

    /// <summary>Godot pack header: <c>GDPC</c>, pack format, major, minor, patch, then a little padding.</summary>
    public static byte[] Pck(int major)
    {
        var bytes = new byte[64];
        "GDPC"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), major >= 4 ? 2u : 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)major);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 2);
        return bytes;
    }

    private static byte[] Binary(string start, string? branch, Encoding encoding)
    {
        // Filler pushes the branch across the detector's 1 MiB read chunks in no particular place.
        var filler = new byte[(1 << 20) - 7];
        return [.. Encoding.ASCII.GetBytes(start), .. filler, .. branch is null ? [] : encoding.GetBytes(branch), .. filler];
    }

    private static string P(string root, string path) => root.Length == 0 ? path : $"{root}/{path}";
}
