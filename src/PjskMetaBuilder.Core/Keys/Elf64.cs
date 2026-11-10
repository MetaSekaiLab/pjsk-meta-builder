using System.Buffers.Binary;

namespace PjskMetaBuilder.Core.Keys;

/// <summary>
/// 只读解析 arm64 的 libil2cpp.so，够用就行：PT_LOAD 段（VA↔文件偏移）、
/// PT_DYNAMIC 里的 RELA 重定位表。il2cpp 把「元数据使用」指针槽留在 .data.rel.ro，
/// 文件里是 0，真实目标在 R_AARCH64_RELATIVE 重定位的 addend 里（槽 VA → 目标 VA）。
/// </summary>
public sealed class Elf64
{
    private const int RAarch64Relative = 1027; // R_AARCH64_RELATIVE

    private readonly byte[] _data;
    private readonly List<(ulong Va, ulong Off, ulong FileSz)> _loads = new();
    private readonly Dictionary<ulong, ulong> _relativeAddends = new();

    public Elf64(byte[] data)
    {
        _data = data;
        if (data.Length < 0x40 || data[0] != 0x7F || data[1] != (byte)'E' || data[2] != (byte)'L' || data[3] != (byte)'F')
            throw new DataException("不是 ELF 文件");
        if (data[4] != 2 || data[5] != 1)
            throw new DataException("libil2cpp.so 不是 64 位小端");

        var phoff = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x20));
        var phentsize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x36));
        var phnum = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x38));
        ulong dynOff = 0, dynSize = 0;
        for (var i = 0; i < phnum; i++)
        {
            var o = (int)phoff + i * phentsize;
            var type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o));
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(o + 8));
            var vaddr = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(o + 0x10));
            var filesz = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(o + 0x20));
            switch (type)
            {
                case 1: _loads.Add((vaddr, offset, filesz)); break;          // PT_LOAD
                case 2: dynOff = offset; dynSize = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(o + 0x28)); break; // PT_DYNAMIC
            }
        }
        if (dynOff != 0)
            ParseRelocations(dynOff, dynSize);
    }

    /// <summary>VA → 文件偏移；不在任何已加载段内返回 null。</summary>
    public int? VaToOffset(ulong va)
    {
        foreach (var (segVa, off, fileSz) in _loads)
            if (va >= segVa && va < segVa + fileSz)
                return (int)(off + (va - segVa));
        return null;
    }

    public ReadOnlySpan<byte> At(ulong va, int length)
    {
        var off = VaToOffset(va) ?? throw new DataException($"VA {va:X} 不在已加载段内");
        if (off + length > _data.Length)
            throw new DataException($"VA {va:X} 越界");
        return _data.AsSpan(off, length);
    }

    /// <summary>R_AARCH64_RELATIVE 重定位：槽 VA → addend（目标 VA）。</summary>
    public ulong? RelativeTarget(ulong slotVa) =>
        _relativeAddends.TryGetValue(slotVa, out var v) ? v : null;

    private void ParseRelocations(ulong dynOff, ulong dynSize)
    {
        ulong rela = 0, relasz = 0, relaent = 24;
        for (var o = (int)dynOff; o + 16 <= _data.Length; o += 16)
        {
            var tag = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(o));
            var val = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(o + 8));
            if (tag == 0) break;               // DT_NULL
            switch (tag)
            {
                case 7: rela = val; break;      // DT_RELA
                case 8: relasz = val; break;    // DT_RELASZ
                case 9: relaent = val; break;   // DT_RELAENT
            }
            if (dynSize != 0 && (ulong)o >= dynOff + dynSize) break;
        }
        if (rela == 0 || relasz == 0 || relaent == 0)
            return;
        var baseOff = VaToOffset(rela);
        if (baseOff is null)
            return;
        var count = relasz / relaent;
        for (ulong i = 0; i < count; i++)
        {
            var o = baseOff.Value + (int)(i * relaent);
            if (o + 24 > _data.Length)
                break;
            var rOffset = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(o));
            var rInfo = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(o + 8));
            var rAddend = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(o + 16));
            if ((rInfo & 0xffffffff) == RAarch64Relative)
                _relativeAddends[rOffset] = rAddend;
        }
    }
}
