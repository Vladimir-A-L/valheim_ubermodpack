using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System.Security.Cryptography;
using System.Text.Json;

// Ограниченный эксперимент. Устраняет первые блокирующие ошибки, а не всю несовместимость.
if (args.Length != 4 || args[0] is not ("audit" or "patch"))
    throw new ArgumentException("Использование: audit|patch GAME_MANAGED INPUT_DLL OUTPUT_DIRECTORY");

var hashes = new Dictionary<string, string>
{
    ["WarfareFireAndIce.dll"] = "71d458a05a7b45aef21aaffd91ba41174f0eb1ac5f20b4354bb133668d746dcd",
    ["MonstrumDeepNorth.dll"] = "6007db1ebf45c7bd44d4786dfaaa262f245e68ff3d70893bf2787de86d810834",
    ["MonstrumAshlands.dll"] = "090f60dfcc2239f45035cbb33b053e6a016c1e8d0cb84a647b6486b3cc9f9c6b"
};
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetFullPath(args[1]));
var parameters = new ReaderParameters { AssemblyResolver = resolver };
using var game = ModuleDefinition.ReadModule(Path.Combine(args[1], "assembly_valheim.dll"), parameters);
using var utils = ModuleDefinition.ReadModule(Path.Combine(args[1], "assembly_utils.dll"), parameters);
using var mod = ModuleDefinition.ReadModule(args[2], parameters);
string name = Path.GetFileName(args[2]);
string originalHash = HashFile(args[2]);
var changes = new List<string>();
var before = Audit(mod);
var after = before;
string output = Path.Combine(Path.GetFullPath(args[3]), name);

if (args[0] == "patch")
{
    if (HashFile(Path.Combine(args[1], "assembly_valheim.dll")) !=
        "c36b81c1733c48d00dd567e41010e6dc95f8dac877e0701cdb6b8c213ade7812" ||
        HashFile(Path.Combine(args[1], "assembly_utils.dll")) !=
        "110bca79b8c31908b5ab7c74a7867dad5a9ba44bd6942a390775c65b3d6a9591")
        throw new InvalidDataException("Бинарники игры отличаются от исследованного build");
    if (!hashes.TryGetValue(name, out var expected) || expected != originalHash)
        throw new InvalidDataException("SHA256 исходного мода не соответствует закреплённой версии");
    if (File.Exists(output) || output == Path.GetFullPath(args[2]))
        throw new IOException("Выходной файл уже существует; оригинал не перезаписывается");
    if (name == "WarfareFireAndIce.dll") PatchHeightmap();
    else PatchZones();
    if (name == "MonstrumAshlands.dll") PatchCategoryOverload();
    Directory.CreateDirectory(args[3]);
    mod.Write(output);
    using var written = ModuleDefinition.ReadModule(output, parameters);
    if (!Resources(mod).SequenceEqual(Resources(written)))
        throw new InvalidDataException("Ресурсы изменились при записи; кандидат непригоден");
    after = Audit(written);
}
else Directory.CreateDirectory(args[3]);

var report = new
{
    schema_version = 1,
    captured_at_utc = DateTimeOffset.UtcNow,
    status = args[0] == "patch" ? "experimental-partial-patch-not-accepted" : "static-audit-only",
    file = name,
    input_sha256 = originalHash,
    output_sha256 = args[0] == "patch" ? HashFile(output) : null,
    game_sha256 = HashFile(Path.Combine(args[1], "assembly_valheim.dll")),
    utils_sha256 = HashFile(Path.Combine(args[1], "assembly_utils.dll")),
    changes,
    unresolved_before = before,
    unresolved_after = after,
    resources = Resources(mod)
};
string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(args[3], name + ".json"), json + "\n");
Console.WriteLine(json);

static string HashFile(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

static string[] Resources(ModuleDefinition module) => module.Resources.OfType<EmbeddedResource>()
    .Select(r => r.Name + ":" + Convert.ToHexString(SHA256.HashData(r.GetResourceStream())).ToLowerInvariant())
    .Order().ToArray();

static string[] Audit(ModuleDefinition module)
{
    var missing = new List<string>();
    foreach (var member in module.GetMemberReferences())
    {
        if (member.DeclaringType.Scope.Name != "assembly_valheim") continue;
        try { if (member.Resolve() == null) missing.Add(member.FullName); }
        catch (AssemblyResolutionException) { missing.Add(member.FullName + " [dependency unresolved]"); }
    }
    return missing.Distinct().Order().ToArray();
}

static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
{
    return module.Types.SelectMany(Flatten);
    static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) =>
        new[] { type }.Concat(type.NestedTypes.SelectMany(Flatten));
}

void PatchHeightmap()
{
    var type = mod.GetType("WarfareFireAndIce.HeightmapBuilderPatch");
    var method = type.Methods.Single(m => m.Name == "Postfix");
    var sector = game.GetType("BiomeSector");
    var biome = sector.Fields.Single(f => f.Name == "Biome");
    var corners = game.GetType("HeightmapBuilder").NestedTypes.Single(t => t.Name == "HMBuildData")
        .Fields.Single(f => f.Name == "m_cornerBiomes");
    if (corners.FieldType.FullName != "BiomeSector[]")
        throw new InvalidDataException("Неожиданный формат HMBuildData");

    // Копия сектора принадлежит углу heightmap. Глобальный сектор мира изменять нельзя.
    var setter = new MethodDefinition("SetCompatCornerBiome",
        MethodAttributes.Private | MethodAttributes.Static, mod.TypeSystem.Void);
    setter.Parameters.Add(new ParameterDefinition(mod.ImportReference(corners.FieldType)));
    setter.Parameters.Add(new ParameterDefinition(mod.TypeSystem.Int32));
    setter.Parameters.Add(new ParameterDefinition(mod.ImportReference(biome.FieldType)));
    type.Methods.Add(setter);
    setter.Body.InitLocals = true;
    var original = new VariableDefinition(mod.ImportReference(sector));
    var copy = new VariableDefinition(mod.ImportReference(sector));
    setter.Body.Variables.Add(original);
    setter.Body.Variables.Add(copy);
    var il = setter.Body.GetILProcessor();
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Ldarg_1);
    il.Emit(OpCodes.Ldelem_Ref);
    il.Emit(OpCodes.Stloc, original);
    il.Emit(OpCodes.Ldnull);
    il.Emit(OpCodes.Ldarg_2);
    il.Emit(OpCodes.Newobj, mod.ImportReference(sector.Methods.Single(m => m.IsConstructor && !m.IsStatic)));
    il.Emit(OpCodes.Stloc, copy);
    foreach (var field in sector.Fields.Where(f => !f.IsStatic && f.Name is not ("Biome" or "BiomeType")))
    {
        il.Emit(OpCodes.Ldloc, copy);
        il.Emit(OpCodes.Ldloc, original);
        il.Emit(OpCodes.Ldfld, mod.ImportReference(field));
        il.Emit(OpCodes.Stfld, mod.ImportReference(field));
    }
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Ldarg_1);
    il.Emit(OpCodes.Ldloc, copy);
    il.Emit(OpCodes.Stelem_Ref);
    il.Emit(OpCodes.Ret);

    method.Body.SimplifyMacros();
    var processor = method.Body.GetILProcessor();
    int reads = 0, writes = 0, fields = 0;
    foreach (var instruction in method.Body.Instructions.ToArray())
    {
        if (instruction.Operand is FieldReference f && f.Name == "m_cornerBiomes")
        {
            instruction.Operand = mod.ImportReference(corners);
            fields++;
        }
        else if (instruction.OpCode == OpCodes.Ldelem_I4)
        {
            instruction.OpCode = OpCodes.Ldelem_Ref;
            processor.InsertAfter(instruction, Instruction.Create(OpCodes.Ldfld, mod.ImportReference(biome)));
            reads++;
        }
        else if (instruction.OpCode == OpCodes.Stelem_I4)
        {
            instruction.OpCode = OpCodes.Call;
            instruction.Operand = setter;
            writes++;
        }
    }
    if (reads != 4 || writes != 4 || fields != 8)
        throw new InvalidDataException("Структура патча высот отличается от ожидаемой");
    method.Body.OptimizeMacros();
    changes.Add("Heightmap: 4 чтения и 4 записи перенесены на отдельные BiomeSector");
}

void PatchZones()
{
    var vector = utils.GetType("Vector2s");
    // Harmony хранит типы аргументов отдельно в сериализованном атрибуте.
    foreach (var t in AllTypes(mod))
        foreach (var attribute in t.CustomAttributes.Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"))
            for (int i = 0; i < attribute.ConstructorArguments.Count; i++)
                attribute.ConstructorArguments[i] = Rewrite(attribute.ConstructorArguments[i]);

    CustomAttributeArgument Rewrite(CustomAttributeArgument argument)
    {
        object value = argument.Value;
        if (value is TypeReference tr && tr.FullName == "Vector2i") value = mod.ImportReference(vector);
        else if (value is CustomAttributeArgument[] items) value = items.Select(Rewrite).ToArray();
        return new CustomAttributeArgument(argument.Type, value);
    }
    // В этих двух закреплённых DLL Vector2i используется для координат зон.
    var references = mod.GetTypeReferences().Where(t => t.FullName == "Vector2i").ToArray();
    if (references.Length != 1) throw new InvalidDataException("Неожиданные ссылки Vector2i");
    references[0].Name = "Vector2s";
    foreach (var field in mod.GetMemberReferences().OfType<FieldReference>())
        if (field.DeclaringType.FullName == "Vector2s" && field.Name is "x" or "y")
            field.FieldType = mod.TypeSystem.Int16;

    var postfix = mod.Types.Single(t => t.Name.StartsWith("FixGetRandomPointInZone"))
        .Methods.Single(m => m.Name == "Postfix");
    postfix.Parameters.Remove(postfix.Parameters.Single(p => p.Name == "__instance"));
    postfix.Body = new MethodBody(postfix);
    var il = postfix.Body.GetILProcessor();
    var center = Instruction.Create(OpCodes.Ldarg_1);
    il.Emit(OpCodes.Ldarg_2);
    il.Emit(OpCodes.Ldc_R4, 32f);
    il.Emit(OpCodes.Bge_Un, center);
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Ret);
    il.Append(center);
    il.Emit(OpCodes.Call, mod.ImportReference(game.GetType("ZoneSystem").Methods
        .Single(m => m.Name == "GetZonePos" && m.Parameters[0].ParameterType.FullName == "Vector2s")));
    il.Emit(OpCodes.Ret);
    foreach (var t in AllTypes(mod))
        foreach (var m in t.Methods.Where(m => m.HasBody))
            m.Body.SimplifyMacros();
    changes.Add("ZoneSystem: Vector2s, поля Int16, статический GetZonePos без __instance");
}

void PatchCategoryOverload()
{
    var ctor = mod.GetType("PieceManager.PiecePrefabManager").Methods.Single(m => m.Name == ".cctor");
    var instructions = ctor.Body.Instructions;
    var load = instructions.Single(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "SetCategory");
    var nullTypes = load.Next;
    if (nullTypes.OpCode != OpCodes.Ldnull || nullTypes.Next.OpCode != OpCodes.Ldnull)
        throw new InvalidDataException("Неожиданный поиск SetCategory");
    var getType = (MethodReference)load.Previous.Operand;
    var processor = ctor.Body.GetILProcessor();
    nullTypes.OpCode = OpCodes.Ldc_I4_1;
    var sequence = new[]
    {
        Instruction.Create(OpCodes.Newarr, mod.ImportReference(getType.ReturnType)),
        Instruction.Create(OpCodes.Dup),
        Instruction.Create(OpCodes.Ldc_I4_0),
        Instruction.Create(OpCodes.Ldtoken, mod.TypeSystem.Int32),
        Instruction.Create(OpCodes.Call, getType),
        Instruction.Create(OpCodes.Stelem_Ref)
    };
    var previous = nullTypes;
    foreach (var instruction in sequence) { processor.InsertAfter(previous, instruction); previous = instruction; }
    changes.Add("PieceManager: явный выбор PieceTable.SetCategory(Int32)");
}
