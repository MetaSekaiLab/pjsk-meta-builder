using System.Buffers.Binary;
using System.Text;

namespace PjskMetaBuilder.Core.Unity;

/// <summary>按 Unity 序列化规则读取一个对象的原始字节（小端；字符串和 bool 之后按 4 字节对齐）。</summary>
public ref struct ObjectReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _pos = 0;

    public int Position => _pos;

    public int ReadInt32()
    {
        var v = BinaryPrimitives.ReadInt32LittleEndian(_data[_pos..]);
        _pos += 4;
        return v;
    }

    public long ReadInt64()
    {
        var v = BinaryPrimitives.ReadInt64LittleEndian(_data[_pos..]);
        _pos += 8;
        return v;
    }

    public byte ReadByte() => _data[_pos++];

    public void Align4() => _pos = (_pos + 3) & ~3;

    public string ReadAlignedString()
    {
        var length = ReadInt32();
        if (length < 0 || _pos + length > _data.Length)
            throw new DataException($"字符串长度不合法：{length}");
        var s = Encoding.UTF8.GetString(_data.Slice(_pos, length));
        _pos += length;
        Align4();
        return s;
    }

    /// <summary>PPtr&lt;T&gt;：m_FileID(int32) + m_PathID(int64)（序列化格式版本 ≥ 14）。</summary>
    public (int FileId, long PathId) ReadPPtr() => (ReadInt32(), ReadInt64());
}
