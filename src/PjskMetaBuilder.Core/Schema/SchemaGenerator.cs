using System.Text.Json;
using System.Text.Json.Nodes;
using Mono.Cecil;

namespace PjskMetaBuilder.Core.Schema;

/// <summary>带 [Key] 的序列化成员：字段或属性（SuiteMaster 里有些表是自动属性）。</summary>
internal sealed record KeyedMember(string Name, TypeReference Type, object Key);

public sealed record SchemaResult(JsonObject Schema, int Tables, int Classes, IReadOnlyList<string> Warnings);

/// <summary>
/// 从 DummyDll 生成 schema.json（设计文档 §4.4）。
/// 表名 ↔ 类取自 Sekai.SuiteMaster：每个字段的 [Key("表名")] 就是 master 顶层键（注意不一定等于字段名，
/// 如字段 bondLive2ds 的 Key 是 "bondsLive2ds"）；元素类型就是行的类。compact* 表也是 SuiteMaster 的字段，
/// 类型是按列存放的类（每个字段一列，__ENUM__ 指向取值表）。
/// </summary>
public sealed class SchemaGenerator
{
    public const int FormatVersion = 1;
    public const string SuiteMasterType = "Sekai.SuiteMaster";
    private const string KeyAttribute = "MessagePack.KeyAttribute";
    private const string IgnoreAttribute = "MessagePack.IgnoreMemberAttribute";

    private readonly ModuleDefinition _module;
    private readonly Dictionary<string, JsonObject> _classes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visiting = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = [];

    private SchemaGenerator(ModuleDefinition module) => _module = module;

    public static SchemaResult Generate(string dummyDllDir, string appVersion)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(dummyDllDir);
        using var assembly = AssemblyDefinition.ReadAssembly(
            Path.Combine(dummyDllDir, "Assembly-CSharp.dll"),
            new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Deferred });
        return new SchemaGenerator(assembly.MainModule).Run(appVersion);
    }

    private SchemaResult Run(string appVersion)
    {
        var suite = _module.GetType(SuiteMasterType)
            ?? throw new DataException($"DummyDll 里找不到 {SuiteMasterType}");

        var tables = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var field in KeyedMembers(suite))
        {
            if (field.Key is not string tableName)
            {
                _warnings.Add($"SuiteMaster.{field.Name} 的 Key 不是字符串，跳过");
                continue;
            }
            var (container, element) = Container(field.Type);
            var cls = Resolve(element);
            if (cls is null || cls.IsEnum || cls.IsPrimitive)
            {
                _warnings.Add($"表 {tableName} 的元素类型 {element.FullName} 不是类，跳过");
                continue;
            }
            Include(cls);
            var table = new JsonObject { ["class"] = cls.FullName, ["container"] = container };
            if (field.Name != tableName)
                table["field"] = field.Name;
            if (tableName.StartsWith("compact", StringComparison.Ordinal) && container == "object")
                table["compact"] = true;
            if (!tables.TryAdd(tableName, table))
                _warnings.Add($"表名 {tableName} 在 SuiteMaster 里出现了多次，保留第一个");
        }

        var tableNode = new JsonObject();
        foreach (var (name, table) in tables)
            tableNode[name] = table;
        var classNode = new JsonObject();
        foreach (var (name, cls) in _classes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            classNode[name] = cls;

        var schema = new JsonObject
        {
            ["schema"] = FormatVersion,
            ["source"] = new JsonObject
            {
                ["appVersion"] = appVersion,
                ["assembly"] = _module.Assembly.Name.Name,
                ["suiteMaster"] = SuiteMasterType,
            },
            ["tables"] = tableNode,
            ["classes"] = classNode,
        };
        return new SchemaResult(schema, tables.Count, _classes.Count, _warnings);
    }

    /// <summary>T[] / List&lt;T&gt; → array；Dictionary&lt;K,V&gt; → map（元素取 V）；其他 → object。</summary>
    private static (string Container, TypeReference Element) Container(TypeReference type)
    {
        if (type is ArrayType array)
            return ("array", array.ElementType);
        if (type is GenericInstanceType g)
        {
            var name = g.ElementType.FullName;
            if (IsListType(name))
                return ("array", g.GenericArguments[0]);
            if (IsDictionaryType(name))
                return ("map", g.GenericArguments[1]);
        }
        return ("object", type);
    }

    private void Include(TypeDefinition type)
    {
        if (_classes.ContainsKey(type.FullName) || !_visiting.Add(type.FullName))
            return;
        var fields = new JsonArray();
        var cls = new JsonObject { ["fields"] = fields };
        _classes[type.FullName] = cls; // 先占位，防止递归引用时重复进入
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in KeyedMembers(type))
        {
            var key = field.Key;
            if (!seenKeys.Add(key.ToString()!))
                _warnings.Add($"{type.FullName} 的 Key {key} 重复");
            var entry = new JsonObject
            {
                ["key"] = key is int i ? JsonValue.Create(i) : JsonValue.Create((string)key),
                ["name"] = FieldName(field.Name),
                ["type"] = TypeOf(field.Type, $"{type.FullName}.{field.Name}"),
            };
            if (IsNullable(field.Type))
                entry["nullable"] = true;
            fields.Add(entry);
        }
        _visiting.Remove(type.FullName);
    }

    /// <summary>去掉开头的下划线并转 camelCase（与 Haruki 生成器一致：_assetbundleName → assetbundleName）。</summary>
    public static string FieldName(string name)
    {
        var trimmed = name.TrimStart('_');
        return string.IsNullOrEmpty(trimmed) ? name : JsonNamingPolicy.CamelCase.ConvertName(trimmed);
    }

    private JsonNode TypeOf(TypeReference type, string where)
    {
        switch (type)
        {
            case ArrayType { ElementType.FullName: "System.Byte" }:
                return JsonValue.Create("bytes");
            case ArrayType array:
                return new JsonObject { ["array"] = TypeOf(array.ElementType, where) };
            case GenericInstanceType g:
                var name = g.ElementType.FullName;
                if (name == "System.Nullable`1")
                    return TypeOf(g.GenericArguments[0], where);
                if (IsListType(name))
                    return new JsonObject { ["array"] = TypeOf(g.GenericArguments[0], where) };
                if (IsDictionaryType(name))
                    return new JsonObject { ["map"] = new JsonArray(TypeOf(g.GenericArguments[0], where), TypeOf(g.GenericArguments[1], where)) };
                if (name == "System.Collections.Generic.KeyValuePair`2")
                    return new JsonObject { ["pair"] = new JsonArray(TypeOf(g.GenericArguments[0], where), TypeOf(g.GenericArguments[1], where)) };
                break;
        }

        var primitive = type.FullName switch
        {
            "System.Boolean" => "bool",
            "System.Byte" or "System.SByte" or "System.Int16" or "System.UInt16" or "System.Int32" => "int",
            "System.UInt32" or "System.Int64" or "System.UInt64" => "long",
            "System.Single" => "float",
            "System.Double" => "double",
            "System.String" => "string",
            "System.DateTime" => "long",
            _ => null,
        };
        if (primitive is not null)
            return JsonValue.Create(primitive);

        var resolved = Resolve(type);
        if (resolved is { IsEnum: true })
            return EnumOf(resolved);
        if (resolved is not null && resolved.Module == _module && !resolved.IsPrimitive)
        {
            Include(resolved);
            return new JsonObject { ["class"] = resolved.FullName };
        }
        _warnings.Add($"{where} 的类型 {type.FullName} 无法描述，记为 unknown");
        return new JsonObject { ["unknown"] = type.FullName };
    }

    private static JsonObject EnumOf(TypeDefinition type)
    {
        var valueField = type.Fields.First(f => !f.IsStatic);
        var values = new JsonObject();
        foreach (var f in type.Fields.Where(f => f.IsStatic && f.HasConstant))
            values[f.Name] = JsonValue.Create(Convert.ToInt64(f.Constant));
        return new JsonObject
        {
            ["enum"] = type.FullName,
            ["underlying"] = valueField.FieldType.FullName switch
            {
                "System.Int64" or "System.UInt64" or "System.UInt32" => "long",
                _ => "int",
            },
            ["values"] = values,
        };
    }

    private static bool IsNullable(TypeReference type) =>
        !type.IsValueType || (type is GenericInstanceType g && g.ElementType.FullName == "System.Nullable`1");

    private static bool IsListType(string name) => name is "System.Collections.Generic.List`1"
        or "System.Collections.Generic.IList`1" or "System.Collections.Generic.IReadOnlyList`1"
        or "System.Collections.ObjectModel.ReadOnlyCollection`1";

    private static bool IsDictionaryType(string name) => name is "System.Collections.Generic.Dictionary`2"
        or "System.Collections.Generic.IDictionary`2" or "System.Collections.Generic.IReadOnlyDictionary`2";

    private static IEnumerable<KeyedMember> KeyedMembers(TypeDefinition type)
    {
        // 基类里的 [Key] 成员也属于序列化内容
        var chain = new List<TypeDefinition>();
        for (var t = type; t is not null; t = SafeResolve(t.BaseType))
        {
            chain.Insert(0, t);
            if (t.BaseType is null || t.BaseType.FullName == "System.Object")
                break;
        }
        var members = new List<KeyedMember>();
        foreach (var t in chain)
        {
            foreach (var f in t.Fields.Where(f => !f.IsStatic && !Ignored(f.CustomAttributes)))
                if (KeyOf(f.CustomAttributes) is { } key)
                    members.Add(new KeyedMember(f.Name, f.FieldType, key));
            foreach (var p in t.Properties.Where(p => !(p.GetMethod ?? p.SetMethod)!.IsStatic && !Ignored(p.CustomAttributes)))
                if (KeyOf(p.CustomAttributes) is { } key)
                    members.Add(new KeyedMember(p.Name, p.PropertyType, key));
        }
        return members
            .OrderBy(m => m.Key is int i ? i : int.MaxValue)
            .ThenBy(m => m.Key as string ?? m.Name, StringComparer.Ordinal);
    }

    private static bool Ignored(IEnumerable<CustomAttribute> attributes) =>
        attributes.Any(a => a.AttributeType.FullName == IgnoreAttribute);

    private static object? KeyOf(IEnumerable<CustomAttribute> attributes)
    {
        var attr = attributes.FirstOrDefault(a => a.AttributeType.FullName == KeyAttribute);
        if (attr is null || attr.ConstructorArguments.Count == 0)
            return null;
        return attr.ConstructorArguments[0].Value switch
        {
            int i => i,
            string s => s,
            _ => null,
        };
    }

    private static TypeDefinition? SafeResolve(TypeReference? type)
    {
        if (type is null)
            return null;
        try
        {
            return type.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
    }

    private TypeDefinition? Resolve(TypeReference type) => SafeResolve(type) ?? _module.GetType(type.FullName);

    /// <summary>规范化：键排序、紧凑、无末尾换行；同一个 DummyDll 每次输出的 sha256 相同。</summary>
    public static byte[] Serialize(JsonObject schema)
    {
        var sorted = SortKeys(schema);
        return JsonSerializer.SerializeToUtf8Bytes(sorted, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static JsonNode? SortKeys(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, SortKeys(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(SortKeys).ToArray()),
        null => null,
        _ => JsonNode.Parse(node.ToJsonString()),
    };
}
