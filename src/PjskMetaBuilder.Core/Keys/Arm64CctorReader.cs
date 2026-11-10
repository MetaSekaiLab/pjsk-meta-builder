namespace PjskMetaBuilder.Core.Keys;

/// <summary>
/// 一个只认 cctor 里那几种指令的最小 arm64 解码器。目标：找出 APIManager..cctor 中每次
/// <c>RuntimeHelpers.InitializeArray</c> 调用的「数据源」槽 VA，按调用顺序返回。
///
/// 编译器生成的 cctor 形如（每个 byte[] 常量字段一段）：
///   adrp xR, page ; ldr xR, [xR, #disp]      ; xR = *(slot)，slot = page+disp，指向 FieldInfo 指针
///   ...
///   ldr  x0, [xType] ; mov w1, #16 ; bl SZArrayNew
///   ldr  x1, [xR]    ; mov x2, xzr ; bl InitializeArray   ; x1 = *(xR)，即该字段
/// 因此每个 InitializeArray 调用点的 x1 来自某个「持有 slot 指针的寄存器」，其 slot VA 唯一确定了字段。
/// InitializeArray 本身不写死地址：它是「所有调用点都带非空且互不相同的源槽」的那个 bl 目标。
/// </summary>
public static class Arm64CctorReader
{
    private const int MaxBytes = 0x800;

    /// <summary>按调用顺序返回 InitializeArray 各次调用的源槽 VA；认不出就返回空。</summary>
    public static IReadOnlyList<ulong> InitializeArraySlots(Elf64 elf, ulong cctorVa) =>
        DecodeSlots(elf.At(cctorVa, MaxBytes), cctorVa);

    /// <summary>反汇编 <paramref name="code"/>（从 <paramref name="baseVa"/> 起）并提取 InitializeArray 源槽 VA。</summary>
    internal static IReadOnlyList<ulong> DecodeSlots(ReadOnlySpan<byte> code, ulong baseVa)
    {
        var page = new Dictionary<int, ulong>();      // reg -> adrp 页基址
        var regSlot = new Dictionary<int, ulong>();   // reg -> 它持有的 slot VA（reg = *(slot)）
        ulong? pendingX1 = null;                      // 最近一次 ldr x1,[regWithSlot] 对应的 slot VA
        var calls = new List<(ulong Target, ulong? Slot)>();

        void Invalidate(int reg)
        {
            page.Remove(reg);
            regSlot.Remove(reg);
            if (reg == 1) pendingX1 = null;
        }

        for (var i = 0; i + 4 <= code.Length; i += 4)
        {
            var pc = baseVa + (ulong)i;
            var instr = (uint)(code[i] | (code[i + 1] << 8) | (code[i + 2] << 16) | (code[i + 3] << 24));

            if ((instr & 0x9F000000) == 0x90000000) // ADRP
            {
                var rd = (int)(instr & 0x1F);
                var immlo = (instr >> 29) & 3;
                var immhi = (instr >> 5) & 0x7FFFF;
                var imm = SignExtend((immhi << 2) | immlo, 21) << 12;
                page[rd] = (pc & ~0xFFFUL) + (ulong)imm;
                regSlot.Remove(rd);
                if (rd == 1) pendingX1 = null;
                continue;
            }
            if ((instr & 0xFFC00000) == 0xF9400000) // LDR (unsigned offset, 64-bit)
            {
                var rt = (int)(instr & 0x1F);
                var rn = (int)((instr >> 5) & 0x1F);
                var imm12 = (instr >> 10) & 0xFFF;
                if (page.TryGetValue(rn, out var p)) // 第一层：rt = *(page+disp)，记下 slot
                {
                    regSlot[rt] = p + imm12 * 8;
                    page.Remove(rt);
                    if (rt == 1) pendingX1 = null;
                }
                else if (regSlot.TryGetValue(rn, out var slot)) // 第二层：rt = *(rt 持有的 slot)
                {
                    if (rt == 1) pendingX1 = slot;
                    else Invalidate(rt);
                }
                else
                {
                    if (rt == 1) pendingX1 = null;
                    Invalidate(rt);
                }
                continue;
            }
            if ((instr & 0xFC000000) == 0x94000000) // BL
            {
                var off = SignExtend(instr & 0x03FFFFFF, 26) << 2;
                var target = pc + (ulong)off;
                calls.Add((target, pendingX1));
                pendingX1 = null;
                continue;
            }
            if (instr == 0xD65F03C0) // RET
                break;
            // 其余指令：分支/比较分支/存储不写我们跟踪的通用寄存器；其他按 Rd=bits[4:0] 失效
            if (IsBranch(instr) || IsStore(instr))
                continue;
            var dst = (int)(instr & 0x1F);
            Invalidate(dst);
            if (IsLoadPair(instr))
                Invalidate((int)((instr >> 10) & 0x1F)); // LDP 的第二个目标寄存器
        }

        // InitializeArray：源槽全部非空、互不相同、至少两次的 bl 目标；取命中最多的那个
        return calls.GroupBy(c => c.Target)
            .Where(g => g.Count() >= 2 && g.All(c => c.Slot is not null) && g.Select(c => c.Slot).Distinct().Count() == g.Count())
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()
            ?.Select(c => c.Slot!.Value).ToList()
            ?? (IReadOnlyList<ulong>)[];
    }

    private static bool IsBranch(uint instr) =>
        (instr & 0xFC000000) == 0x14000000 ||   // B
        (instr & 0xFF000010) == 0x54000000 ||   // B.cond
        (instr & 0x7E000000) == 0x34000000 ||   // CBZ/CBNZ
        (instr & 0x7E000000) == 0x36000000 ||   // TBZ/TBNZ
        (instr & 0xFC000000) == 0x94000000 ||   // BL（已在上面处理，这里兜底）
        (instr & 0xFFFFFC1F) == 0xD63F0000 ||   // BLR
        (instr & 0xFFFFFC1F) == 0xD61F0000;     // BR

    private static bool IsStore(uint instr) =>
        (instr & 0xBFC00000) == 0xB9000000 ||   // STR (unsigned imm, 32/64)
        (instr & 0xBFE00000) == 0xB8000000 ||   // STR (pre/post/unscaled)
        (instr & 0x3BC00000) == 0x39000000 ||   // STRB/STRH (unsigned imm)
        (instr & 0x7FC00000) == 0x29000000 ||   // STP (signed offset)
        (instr & 0x7FC00000) == 0x28800000 ||   // STP (post-index)
        (instr & 0x7FC00000) == 0x29800000;     // STP (pre-index)

    private static bool IsLoadPair(uint instr) =>
        (instr & 0x7FC00000) == 0x29400000 ||   // LDP (signed offset)
        (instr & 0x7FC00000) == 0x28C00000 ||   // LDP (post-index)
        (instr & 0x7FC00000) == 0x29C00000;     // LDP (pre-index)

    private static long SignExtend(uint value, int bits)
    {
        var shift = 64 - bits;
        return (long)((ulong)value << shift) >> shift;
    }
}
