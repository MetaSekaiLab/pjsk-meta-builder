namespace PjskMetaBuilder.Core.Keys;

/// <summary>cctor 反汇编得到的有序密钥字段哈希，以及据此取到的 KeySet。</summary>
public sealed record ResolvedKeys(IReadOnlyList<string> Hashes, KeySet Keys);

/// <summary>
/// 设计文档 §9 Q1 方案 1：不依赖硬编码哈希，而是反汇编 <c>APIManager..cctor</c>，
/// 顺着 <c>RuntimeHelpers.InitializeArray</c> 的数据源定位 <c>dummydata</c>/<c>stab</c>
/// （Nuverse 还有 <c>abdummydata</c>/<c>abstab</c>）对应的 <c>&lt;PrivateImplementationDetails&gt;</c>
/// 字段，再在 metadata 里按字段哈希取 16 字节。密钥一旦更换，这里会自动跟上。
///
/// 链路：cctor 的 InitializeArray 源槽 VA →（.rela.dyn 重定位 addend）→ 字段使用槽 VA →
///       （script.json 的 Field$ 映射）→ 字段名（即数据的 SHA-256）→ metadata 里取数据。
/// </summary>
public static class KeyResolver
{
    /// <summary>
    /// 用 Il2CppDumper 的输出解析密钥；<paramref name="libil2cppPath"/> 和 <paramref name="scriptJsonPath"/>
    /// 来自同一次 dump。认不出时抛 <see cref="DataException"/>，由调用方决定是否回退到已知哈希表。
    /// </summary>
    public static ResolvedKeys Resolve(string libil2cppPath, string scriptJsonPath, byte[] metadata, bool nuverse)
    {
        var elf = new Elf64(File.ReadAllBytes(libil2cppPath));
        var script = Il2CppScript.Load(scriptJsonPath);
        if (script.ApiManagerCctor is not { } cctor)
            throw new DataException("script.json 里找不到 APIManager..cctor");

        var slots = Arm64CctorReader.InitializeArraySlots(elf, cctor);
        var expected = nuverse ? 4 : 2;
        if (slots.Count != expected)
            throw new DataException($"cctor 里认出 {slots.Count} 个 InitializeArray 源，{(nuverse ? "Nuverse" : "CP")} 服应为 {expected}");

        var hashes = new List<string>(slots.Count);
        foreach (var slot in slots)
        {
            var addend = elf.RelativeTarget(slot)
                ?? throw new DataException($"InitializeArray 源槽 {slot:X} 没有对应的重定位");
            if (!script.PrivateImplFields.TryGetValue(addend, out var hash))
                throw new DataException($"重定位目标 {addend:X} 不是已知的 <PrivateImplementationDetails> 字段");
            hashes.Add(hash);
        }
        return new ResolvedKeys(hashes, KeyExtractor.FromHashes(metadata, hashes, nuverse));
    }
}
