// 由 pjsk-reverse-workflow scripts/sekai_metadata.py 的常量生成（该文件又改编自 MetaSekaiLab/sssekai 07778264）。
namespace PjskMetaBuilder.Core.Metadata;

internal static class MetadataConstants
{
    /// <summary>il2cpp global-metadata 魔数。</summary>
    public static readonly byte[] Magic = [0xAF, 0x1B, 0xB1, 0xFA];

    /// <summary>Nuverse（cn/tw/kr）格式开头的 8 字节哨兵值。</summary>
    public static readonly byte[] NuverseSentinel = [0x56, 0xBE, 0xF0, 0x89, 0xC2, 0x53, 0x89, 0xF0];

    /// <summary>jp/en 格式：XOR 密钥紧跟在 libil2cpp.so 里这个 NDK 字符串之后。</summary>
    public static readonly byte[] JpKeySignature = [0x44, 0x57, 0x5F, 0x4F, 0x50, 0x5F, 0x70, 0x69, 0x65, 0x63, 0x65, 0x20, 0x6E, 0x6F, 0x74, 0x20, 0x69, 0x6D, 0x70, 0x6C, 0x65, 0x6D, 0x65, 0x6E, 0x74, 0x65, 0x64, 0x00, 0x78, 0x35, 0x00, 0x78, 0x31, 0x30, 0x00];

    /// <summary>Nuverse 共享 metadata 密钥的 CRC32 原像，Base85（RFC 1924 字母表，同 Python base64.b85encode）。</summary>
    public const string NuverseKeyTableBase85 =
        "4{ka2D9U8<D3B0mEYp;8J7Qd;wH`O7{VwF%dD5L>zd^_G49^q|*IVv2^^*<5M}i3p08F2`$|<5cSKBjt" +
        "-c#QNC!#le@Z?nJYuTl$l+vFYbd^<?0r0X(mE7iYKOz-(4CiGk0PyzZ`T&ch^t{OvNM0VUEk!0|xMsl1" +
        "x(3lH1&wmvgSNc^z#r+KDY9Kl6yH6}6sD0la@!K@Q1(C=DHE6OlmptC;it&&>O#YQASqu%0KHu2#Tw_F" +
        "Ik<=nB~|T}#pX``lt%iYtL)MJw2F~%n5@6_#Ay=x{XXx<SxUAN<|<wU1~!28b9kLj7!Q2MDHXwztHC72" +
        "c<$7=!u;|w`=D&oegJ;3P}kR^NbgTuXSp)ZX_OvN^t7Cb-1Prh2SDW*y<fy4ei^@1{BzScsl&X%z@K9q" +
        "kUHR5rICl7ENcozikTt`@tf%%K?v;BkR{x_CM_rqEDQYKZgJ+)3004HLavSYu!|w6_O>gP#&`golJFnc" +
        "7XbA+yX9R05e%fk)u%Q!G{%2VfHX%N+3MRH*?6vY7p7Udu@irL@8%{Na%5|m&v-{Dd4}PjtsDX;Skyst" +
        "&>wtjmTj>t@;Vp{lfSPpC5ibtfDfCWr3R81Mcb+?Si8AuGeT6#Oa-hjr}=DC-5<Qlhoc_HEgV5ZNQ+l_";
}
