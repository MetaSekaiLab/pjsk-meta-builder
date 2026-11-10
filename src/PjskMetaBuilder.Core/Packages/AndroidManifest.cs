using System.Buffers.Binary;
using System.Text;

namespace PjskMetaBuilder.Core.Packages;

public sealed record ManifestInfo(string Package, string? VersionName, long? VersionCode, string? Split);

/// <summary>
/// 解析二进制 AndroidManifest.xml（AXML），只取 &lt;manifest&gt; 元素的 package、versionName、versionCode、split。
/// 不依赖 aapt2。
/// </summary>
public static class AndroidManifest
{
    private const ushort ResXmlType = 0x0003;
    private const ushort ResStringPoolType = 0x0001;
    private const ushort ResXmlResourceMapType = 0x0180;
    private const ushort ResXmlStartElementType = 0x0102;

    private const uint AttrVersionCode = 0x0101021B;
    private const uint AttrVersionName = 0x0101021C;

    private const byte TypeString = 0x03;
    private const byte TypeIntDec = 0x10;
    private const byte TypeIntHex = 0x11;

    public static ManifestInfo Parse(byte[] data)
    {
        var span = data.AsSpan();
        if (span.Length < 8 || BinaryPrimitives.ReadUInt16LittleEndian(span) != ResXmlType)
            throw new DataException("AndroidManifest.xml 不是二进制 XML");

        string[] strings = [];
        uint[] resourceIds = [];
        int pos = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
        while (pos + 8 <= span.Length)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(span[pos..]);
            var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(span[(pos + 2)..]);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 4)..]);
            if (size <= 0 || pos + size > span.Length)
                throw new DataException("AndroidManifest.xml 块长度越界");
            var chunk = span.Slice(pos, size);
            switch (type)
            {
                case ResStringPoolType:
                    strings = ReadStringPool(chunk);
                    break;
                case ResXmlResourceMapType:
                    resourceIds = new uint[(size - headerSize) / 4];
                    for (var i = 0; i < resourceIds.Length; i++)
                        resourceIds[i] = BinaryPrimitives.ReadUInt32LittleEndian(chunk[(headerSize + i * 4)..]);
                    break;
                case ResXmlStartElementType:
                    var element = ReadStartElement(chunk, headerSize, strings, resourceIds);
                    if (element is not null)
                        return element;
                    break;
            }
            pos += size;
        }
        throw new DataException("AndroidManifest.xml 里没有 <manifest> 元素");
    }

    private static ManifestInfo? ReadStartElement(ReadOnlySpan<byte> chunk, int headerSize, string[] strings, uint[] resourceIds)
    {
        // ResXMLTree_attrExt：ns(4) name(4) attributeStart(2) attributeSize(2) attributeCount(2) id/class/style(2*3)
        var ext = chunk[headerSize..];
        var name = Str(strings, BinaryPrimitives.ReadInt32LittleEndian(ext[4..]));
        if (name != "manifest")
            return null;
        var attrStart = BinaryPrimitives.ReadUInt16LittleEndian(ext[8..]);
        var attrSize = BinaryPrimitives.ReadUInt16LittleEndian(ext[10..]);
        var attrCount = BinaryPrimitives.ReadUInt16LittleEndian(ext[12..]);

        string? package = null, versionName = null, split = null;
        long? versionCode = null;
        for (var i = 0; i < attrCount; i++)
        {
            var a = ext[(attrStart + i * attrSize)..];
            var nameIndex = BinaryPrimitives.ReadInt32LittleEndian(a[4..]);
            var rawIndex = BinaryPrimitives.ReadInt32LittleEndian(a[8..]);
            var dataType = a[15];
            var dataValue = BinaryPrimitives.ReadUInt32LittleEndian(a[16..]);
            var attrName = Str(strings, nameIndex);
            var resId = nameIndex >= 0 && nameIndex < resourceIds.Length ? resourceIds[nameIndex] : 0u;

            string? text = rawIndex >= 0 ? Str(strings, rawIndex)
                : dataType == TypeString ? Str(strings, (int)dataValue) : null;

            if (attrName == "package")
                package = text;
            else if (attrName == "split")
                split = text;
            else if (resId == AttrVersionName || attrName == "versionName")
                versionName = text ?? dataValue.ToString();
            else if (resId == AttrVersionCode || attrName == "versionCode")
                versionCode = dataType is TypeIntDec or TypeIntHex ? dataValue
                    : long.TryParse(text, out var parsed) ? parsed : null;
        }
        if (string.IsNullOrEmpty(package))
            throw new DataException("<manifest> 没有 package 属性");
        return new ManifestInfo(package, versionName, versionCode, split);
    }

    private static string? Str(string[] strings, int index) =>
        index >= 0 && index < strings.Length ? strings[index] : null;

    private static string[] ReadStringPool(ReadOnlySpan<byte> chunk)
    {
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk[8..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(chunk[16..]);
        var stringsStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk[20..]);
        var utf8 = (flags & 0x100) != 0;
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]);
        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk[(headerSize + i * 4)..]);
            var p = stringsStart + offset;
            if (utf8)
            {
                ReadUtf8Len(chunk, ref p); // UTF-16 字符数，跳过
                var byteLen = ReadUtf8Len(chunk, ref p);
                result[i] = Encoding.UTF8.GetString(chunk.Slice(p, byteLen));
            }
            else
            {
                int len = BinaryPrimitives.ReadUInt16LittleEndian(chunk[p..]);
                p += 2;
                if ((len & 0x8000) != 0)
                {
                    len = ((len & 0x7FFF) << 16) | BinaryPrimitives.ReadUInt16LittleEndian(chunk[p..]);
                    p += 2;
                }
                result[i] = Encoding.Unicode.GetString(chunk.Slice(p, len * 2));
            }
        }
        return result;
    }

    private static int ReadUtf8Len(ReadOnlySpan<byte> chunk, ref int p)
    {
        int len = chunk[p++];
        if ((len & 0x80) != 0)
            len = ((len & 0x7F) << 8) | chunk[p++];
        return len;
    }
}
