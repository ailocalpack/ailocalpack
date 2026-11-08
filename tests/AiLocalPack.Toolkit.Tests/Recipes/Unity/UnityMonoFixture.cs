using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Mono.Cecil;
using AiLocalPack.Toolkit.Recipes.Unity;

namespace AiLocalPack.Toolkit.Tests.Recipes.Unity;

/// <summary>
/// Generated metadata and serialized values, never a compiled or copied game assembly: a stripped assets file with a
/// <c>Fixtures.Dialogue</c> behaviour (object 1) and one whose script names a class the assembly lacks (object 3).
/// </summary>
internal static class UnityMonoFixture
{
    public static void Create(string root)
    {
        UnityFixture.Create(root, typeTrees: false);
        var managed = Path.Combine(root, "Game_Data/Managed");
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Assembly-CSharp", new Version(1, 0)), "Assembly-CSharp", ModuleKind.Dll))
        {
            var module = assembly.MainModule;
            var mono = new TypeDefinition("UnityEngine", "MonoBehaviour", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
            module.Types.Add(mono);
            var dialogue = new TypeDefinition("Fixtures", "Dialogue", TypeAttributes.Public | TypeAttributes.Class, mono);
            dialogue.Fields.Add(new FieldDefinition("m_Text", FieldAttributes.Public, module.TypeSystem.String));
            dialogue.Fields.Add(new FieldDefinition("identifier", FieldAttributes.Public, module.TypeSystem.String));
            dialogue.Fields.Add(new FieldDefinition("score", FieldAttributes.Public, module.TypeSystem.Int32));
            module.Types.Add(dialogue);
            assembly.Write(Path.Combine(managed, "Assembly-CSharp.dll"));
        }

        var manager = new AssetsManager();
        try
        {
            using var types = typeof(UnityRecipe).Assembly.GetManifestResourceStream("AiLocalPack.Toolkit.Recipes.Unity.Files.classdata.tpk")!;
            manager.LoadClassPackage(types);
            var database = manager.LoadClassDatabaseFromPackage("2019.4.40f1");
            var assets = new AssetsFile
            {
                Header = new AssetsFileHeader { Version = 17 },
                Metadata = new AssetsFileMetadata
                {
                    UnityVersion = "2019.4.40f1", TargetPlatform = 19, TypeTreeEnabled = false,
                    TypeTreeTypes = [
                        new TypeTreeType { TypeId = 114, ScriptTypeIndex = 0, ScriptIdHash = Hash128.NewBlankHash(), TypeHash = Hash128.NewBlankHash() },
                        new TypeTreeType { TypeId = 115, ScriptTypeIndex = ushort.MaxValue, TypeHash = Hash128.NewBlankHash() },
                    ], AssetInfos = [], ScriptTypes = [], Externals = [], RefTypes = [], UserInformation = "synthetic Mono metadata fixture",
                },
            };
            AssetTypeTemplateField Template(int type)
            {
                var template = new AssetTypeTemplateField();
                template.FromClassDatabase(database, database.FindAssetClassByID(type));
                return template;
            }

            var generator = new MonoCecilTempGenerator(managed);
            manager.MonoTempGenerator = generator;
            var generated = generator.GetTemplateField(Template(114), "Assembly-CSharp", "Fixtures", "Dialogue", new UnityVersion("2019.4.40f1"));
            var field = ValueBuilder.DefaultValueFieldFromTemplate(generated);
            field["m_Script"]["m_PathID"].AsLong = 2;
            field["m_Text"].AsString = "Hello from stripped Mono!";
            field["identifier"].AsString = "keep-id";
            field["score"].AsInt = 42;
            var info = new AssetFileInfo { PathId = 1, TypeIdOrIndex = 0, TypeId = 114 };
            info.SetNewData(field);
            assets.Metadata.AddAssetInfo(info);

            var script = ValueBuilder.DefaultValueFieldFromTemplate(Template(115));
            script["m_Name"].AsString = "Dialogue";
            script["m_ClassName"].AsString = "Dialogue";
            script["m_Namespace"].AsString = "Fixtures";
            script["m_AssemblyName"].AsString = "Assembly-CSharp.dll";
            var scriptInfo = new AssetFileInfo { PathId = 2, TypeIdOrIndex = 1, TypeId = 115 };
            scriptInfo.SetNewData(script);
            assets.Metadata.AddAssetInfo(scriptInfo);
            // A behaviour whose script names a class the assembly doesn't have (a removed or plugin component).
            var orphan = ValueBuilder.DefaultValueFieldFromTemplate(generated);
            orphan["m_Script"]["m_PathID"].AsLong = 4;
            orphan["m_Text"].AsString = "Orphan text";
            var orphanInfo = new AssetFileInfo { PathId = 3, TypeIdOrIndex = 0, TypeId = 114 };
            orphanInfo.SetNewData(orphan);
            assets.Metadata.AddAssetInfo(orphanInfo);
            var missing = ValueBuilder.DefaultValueFieldFromTemplate(Template(115));
            missing["m_Name"].AsString = "Missing";
            missing["m_ClassName"].AsString = "Missing";
            missing["m_Namespace"].AsString = "Fixtures";
            missing["m_AssemblyName"].AsString = "Assembly-CSharp.dll";
            var missingInfo = new AssetFileInfo { PathId = 4, TypeIdOrIndex = 1, TypeId = 115 };
            missingInfo.SetNewData(missing);
            assets.Metadata.AddAssetInfo(missingInfo);

            using var writer = new AssetsFileWriter(Path.Combine(root, "Game_Data/resources.assets"));
            assets.Write(writer);
        }
        finally
        {
            manager.UnloadAll();
        }
    }
}
