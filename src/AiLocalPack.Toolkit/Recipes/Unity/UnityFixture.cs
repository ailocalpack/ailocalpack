using System.Text;
using AssetsTools.NET;

namespace AiLocalPack.Toolkit.Recipes.Unity;

/// <summary>Synthetic serialized assets only: no Unity runtime or game files.</summary>
public static class UnityFixture
{
    public static IReadOnlyList<string> Create(string root, bool il2Cpp = false, bool macBundle = false, bool typeTrees = true, bool bigEndian = false, bool bundled = false)
    {
        var data = macBundle ? "Game.app/Contents/Resources/Data" : "Game_Data";
        Directory.CreateDirectory(Path.Combine(root, data, il2Cpp ? "il2cpp_data" : "Managed"));
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [data + "/globalgamemanagers"] = [],
            [macBundle ? "Game.app/Contents/MacOS/Game" : "Game.exe"] = [0],
            [data + (bundled ? "/StreamingAssets/text.bundle" : "/resources.assets")] = bundled ? Bundle(Assets(typeTrees, bigEndian)) : Assets(typeTrees, bigEndian),
        };
        foreach (var (path, bytes) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path))!);
            File.WriteAllBytes(Path.Combine(root, path), bytes);
        }

        return [.. files.Keys.Order(StringComparer.Ordinal)];
    }

    internal static byte[] Bundle(byte[] assets)
    {
        var entry = AssetBundleDirectoryInfo.Create("CAB-synthetic", true);
        entry.SetNewData(assets);
        var opaque = AssetBundleDirectoryInfo.Create("opaque.resource", false);
        opaque.SetNewData([1, 2, 3, 4, 5]);
        var bundle = new AssetBundleFile
        {
            Header = new AssetBundleHeader
            {
                Signature = "UnityFS", Version = 6, GenerationVersion = "5.x.x", EngineVersion = "2019.4.40f1",
                FileStreamHeader = new AssetBundleFSHeader { Flags = AssetBundleFSHeaderFlags.HasDirectoryInfo },
            },
            BlockAndDirInfo = new AssetBundleBlockAndDirInfo { Hash = Hash128.NewBlankHash(), BlockInfos = [], DirectoryInfos = [entry, opaque] },
        };
        using var buffer = new MemoryStream();
        using var writer = new AssetsFileWriter(buffer);
        bundle.Write(writer);
        return buffer.ToArray();
    }

    internal static byte[] Assets(bool typeTrees, bool bigEndian)
    {
        var file = new AssetsFile
        {
            Header = new AssetsFileHeader { Version = 17, Endianness = bigEndian },
            Metadata = new AssetsFileMetadata
            {
                UnityVersion = "2019.4.40f1", TargetPlatform = 19, TypeTreeEnabled = typeTrees,
                TypeTreeTypes = [Type(49, ["m_Name", "m_Script"]), Type(114, ["m_Name", "m_Text", "m_Localized", "identifier"])],
                AssetInfos = [], ScriptTypes = [], Externals = [], RefTypes = [], UserInformation = "ailocalpack synthetic fixture",
            },
        };
        byte[] Strings(params string[] values)
        {
            using var buffer = new MemoryStream();
            using var writer = new AssetsFileWriter(buffer) { BigEndian = bigEndian };
            foreach (var value in values)
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                writer.Write(bytes.Length);
                writer.Write(bytes);
                writer.Align();
            }

            return buffer.ToArray();
        }

        var bodies = new[]
        {
            Strings("dialogue.txt", "Hello, {name}!\r\n\r\nGoodbye.\r\n"),
            Strings("dialogue.json", "{\"id\":\"stable-id\", \"text\":\"Welcome!\", \"count\":1.00}"),
            Strings("Menu", "<b>New game</b>", "Continue", "keep-this-id"),
            Strings("code.cs", "public class Example {}"),
        };
        for (var i = 0; i < bodies.Length; i++)
        {
            var info = new AssetFileInfo { PathId = i + 1, TypeIdOrIndex = i == 2 ? 1 : 0, TypeId = i == 2 ? 114 : 49, ScriptTypeIndex = ushort.MaxValue };
            info.SetNewData(bodies[i]);
            file.Metadata.AddAssetInfo(info);
        }

        using var output = new MemoryStream();
        using var assetsWriter = new AssetsFileWriter(output);
        file.Write(assetsWriter);
        return output.ToArray();
    }

    private static TypeTreeType Type(int id, string[] fields)
    {
        using var strings = new MemoryStream();
        uint Add(string text)
        {
            var offset = checked((uint)strings.Position);
            strings.Write(Encoding.UTF8.GetBytes(text + '\0'));
            return offset;
        }

        var nodes = new List<TypeTreeNode>();
        void Node(string type, string name, byte level, int size = -1, bool array = false, uint flags = 0)
        {
            nodes.Add(new TypeTreeNode
            {
                Version = 1, Level = level, TypeStrOffset = Add(type), NameStrOffset = Add(name), ByteSize = size,
                TypeFlags = array ? TypeTreeNodeFlags.Array : 0, MetaFlags = flags, Index = checked((uint)nodes.Count),
            });
        }

        Node(id == 49 ? "TextAsset" : "MonoBehaviour", "Base", 0);
        foreach (var field in fields)
        {
            Node("string", field, 1);
            Node("Array", "Array", 2, array: true, flags: 0x4000);
            Node("int", "size", 3, 4);
            Node("char", "data", 3, 1);
        }

        return new TypeTreeType
        {
            TypeId = id, ScriptTypeIndex = id == 114 ? (ushort)0 : ushort.MaxValue,
            ScriptIdHash = Hash128.NewBlankHash(), TypeHash = Hash128.NewBlankHash(),
            TypeBlobIsDefinition = true, TypeBlob = new TypeTreeBlob { Nodes = nodes, StringBufferBytes = strings.ToArray() },
            TypeDependencies = [],
        };
    }
}
