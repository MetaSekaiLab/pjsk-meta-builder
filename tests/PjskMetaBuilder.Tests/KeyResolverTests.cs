using System.Buffers.Binary;
using System.Text;
using PjskMetaBuilder.Core.Keys;

namespace PjskMetaBuilder.Tests;

/// <summary>最小 arm64 编码器，只覆盖 cctor 解码器需要识别的几条指令。</summary>
internal static class Arm64
{
    public static uint Adrp(int rd, ulong pc, ulong targetPage)
    {
        var imm = (long)(targetPage - (pc & ~0xFFFUL)) >> 12;   // 以 4KB 为单位
        var immlo = (uint)(imm & 3);
        var immhi = (uint)((imm >> 2) & 0x7FFFF);
        return 0x90000000u | (immlo << 29) | (immhi << 5) | (uint)rd;
    }

    public static uint LdrImm(int rt, int rn, uint byteOffset) =>
        0xF9400000u | ((byteOffset / 8) << 10) | ((uint)rn << 5) | (uint)rt;    // LDR Xt,[Xn,#off]

    public static uint Bl(ulong pc, ulong target) =>
        0x94000000u | ((uint)(((long)(target - pc)) >> 2) & 0x03FFFFFF);

    public static uint MovzW(int rd, ushort imm) => 0x52800000u | ((uint)imm << 5) | (uint)rd;
    public static uint Movz(int rd, ushort imm) => 0xD2800000u | ((uint)imm << 5) | (uint)rd;
    public const uint Ret = 0xD65F03C0;

    public static byte[] Assemble(params uint[] words)
    {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
        return bytes;
    }
}

public class Arm64CctorReaderTests
{
    // 模拟两段「new byte[16] + InitializeArray」：x19/x21 各从一个 slot 载入字段指针，再喂给 InitializeArray。
    [Fact]
    public void RecoversTwoInitializeArraySlotsInOrder()
    {
        const ulong baseVa = 0x1000;
        const ulong slotPage = 0x5000, typePage = 0x6000;
        const ulong szNew = 0x8000, init = 0x9000;
        var words = new uint[64];
        var i = 0;
        uint At(uint w) => words[i++] = w;
        ulong Pc() => baseVa + (ulong)(i * 4);
        // 预载两个 slot 指针寄存器
        At(Arm64.Adrp(19, Pc(), slotPage)); At(Arm64.LdrImm(19, 19, 0x8f8));   // x19 = *(slotPage+0x8f8)
        At(Arm64.Adrp(21, Pc(), slotPage)); At(Arm64.LdrImm(21, 21, 0x900));   // x21 = *(slotPage+0x900)
        At(Arm64.Adrp(22, Pc(), typePage)); At(Arm64.LdrImm(22, 22, 0x10));    // x22 = byte[] 类型
        // 第一段
        At(Arm64.LdrImm(0, 22, 0));  At(Arm64.MovzW(1, 16)); var p1 = Pc(); At(Arm64.Bl(p1, szNew));
        At(Arm64.LdrImm(1, 19, 0));  At(Arm64.Movz(2, 0));   var p2 = Pc(); At(Arm64.Bl(p2, init));
        // 第二段
        At(Arm64.LdrImm(0, 22, 0));  At(Arm64.MovzW(1, 16)); var p3 = Pc(); At(Arm64.Bl(p3, szNew));
        At(Arm64.LdrImm(1, 21, 0));  At(Arm64.Movz(2, 0));   var p4 = Pc(); At(Arm64.Bl(p4, init));
        At(Arm64.Ret);
        var code = Arm64.Assemble(words[..i]);

        var slots = Arm64CctorReader.DecodeSlots(code, baseVa);
        Assert.Equal([slotPage + 0x8f8, slotPage + 0x900], slots);
    }

    [Fact]
    public void IgnoresOtherByteArrayInitsWithoutSlotSource()
    {
        // 只有一个真正从 slot 取源的 InitializeArray：少于 2 个 → 认不出，返回空（交由调用方回退）
        const ulong baseVa = 0x1000;
        var words = new List<uint>();
        words.Add(Arm64.Adrp(19, baseVa, 0x5000));
        words.Add(Arm64.LdrImm(19, 19, 0x100));
        words.Add(Arm64.LdrImm(1, 19, 0));
        words.Add(Arm64.Bl(baseVa + (ulong)(words.Count * 4), 0x9000));
        words.Add(Arm64.Ret);
        Assert.Empty(Arm64CctorReader.DecodeSlots(Arm64.Assemble(words.ToArray()), baseVa));
    }
}

public class Il2CppScriptTests
{
    [Fact]
    public void ParsesCctorAddressAndPrivateImplFields()
    {
        var json = """
            {
              "ScriptMethod": [
                {"Address":"0x1234","Name":"Sekai.Other$$.ctor"},
                {"Address":"0x61809c8","Name":"Sekai.APIManager$$.cctor"}
              ],
              "ScriptString": [{"Address":1,"Value":"x"}],
              "ScriptMetadata": [
                {"Address":196171384,"Name":"Field$<PrivateImplementationDetails>.AABBCC","Signature":null},
                {"Address":196171392,"Name":"System.Action<int>_var","Signature":"Il2CppType*"}
              ],
              "Addresses": [1,2,3]
            }
            """;
        var path = Path.GetTempFileName();
        File.WriteAllText(path, json);
        var script = Il2CppScript.Load(path);

        Assert.Equal(0x61809c8UL, script.ApiManagerCctor);
        Assert.Equal("AABBCC", script.PrivateImplFields[196171384]);
        Assert.Single(script.PrivateImplFields);
    }
}

public class Elf64Tests
{
    // 构造一个最小 ELF：1 个 PT_LOAD + 1 个 PT_DYNAMIC（指向一张 RELA），含一条 R_AARCH64_RELATIVE。
    [Fact]
    public void ParsesSegmentsAndRelativeRelocations()
    {
        const ulong loadVa = 0x4000;
        var image = new byte[0x400];
        // ELF header
        image[0] = 0x7F; image[1] = (byte)'E'; image[2] = (byte)'L'; image[3] = (byte)'F';
        image[4] = 2; image[5] = 1;
        var phoff = 0x40;
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x20), (ulong)phoff);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x36), 56);   // e_phentsize
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x38), 2);    // e_phnum

        // 数据布局：RELA 表和 dynamic 都放在文件里，VA = loadVa + 文件偏移（整段一一映射）
        var relaOff = 0x100; var dynOff = 0x200;
        void Ph(int at, uint type, ulong off, ulong size)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at), type);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(at + 8), off);       // p_offset
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(at + 0x10), loadVa + off); // p_vaddr
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(at + 0x20), size);   // p_filesz
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(at + 0x28), size);   // p_memsz
        }
        Ph(phoff, 1, 0, (ulong)image.Length);          // PT_LOAD 覆盖整文件
        Ph(phoff + 56, 2, (ulong)dynOff, 0x80);        // PT_DYNAMIC

        // RELA：一条 R_AARCH64_RELATIVE，槽 VA = loadVa+0x300，addend = 0xBEEF
        var slotVa = loadVa + 0x300;
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(relaOff), slotVa);       // r_offset
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(relaOff + 8), 1027);     // r_info = R_AARCH64_RELATIVE
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(relaOff + 16), 0xBEEF);  // r_addend

        // dynamic：DT_RELA(7)=relaVa, DT_RELASZ(8)=24, DT_RELAENT(9)=24, DT_NULL(0)
        void Dyn(int slot, long tag, ulong val)
        {
            BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(dynOff + slot * 16), tag);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(dynOff + slot * 16 + 8), val);
        }
        Dyn(0, 7, loadVa + (ulong)relaOff);
        Dyn(1, 8, 24);
        Dyn(2, 9, 24);
        Dyn(3, 0, 0);

        var elf = new Elf64(image);
        Assert.Equal(relaOff, elf.VaToOffset(loadVa + (ulong)relaOff));
        Assert.Equal(0xBEEFUL, elf.RelativeTarget(slotVa));
        Assert.Null(elf.RelativeTarget(loadVa + 0x999));
    }
}
