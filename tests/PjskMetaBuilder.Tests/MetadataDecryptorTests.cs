using PjskMetaBuilder.Core;
using PjskMetaBuilder.Core.Metadata;

namespace PjskMetaBuilder.Tests;

/// <summary>
/// Fixtures/ 下的数据由 pjsk-reverse-workflow scripts/sekai_metadata.py 的 decrypt_metadata 生成（随机内容，不含真实密钥），
/// 保证两边算法逐字节一致。
/// </summary>
public class MetadataDecryptorTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void DecryptsNuverseFormat()
    {
        var plain = MetadataDecryptor.Decrypt(Fixture("nuverse-metadata.enc"), []);
        Assert.Equal(Fixture("nuverse-metadata.plain"), plain);
    }

    [Fact]
    public void DecryptsJpFormatWithKeyFromLibrary()
    {
        var plain = MetadataDecryptor.Decrypt(Fixture("jp-metadata.enc"), Fixture("jp-libil2cpp.so"));
        Assert.Equal(Fixture("jp-metadata.plain"), plain);
    }

    [Fact]
    public void RejectsPlainMetadata()
    {
        Assert.Throws<DataException>(() => MetadataDecryptor.Decrypt(Fixture("jp-metadata.plain"), Fixture("jp-libil2cpp.so")));
    }

    [Fact]
    public void RejectsLibraryWithoutSignature()
    {
        Assert.Throws<DataException>(() => MetadataDecryptor.Decrypt(Fixture("jp-metadata.enc"), new byte[64]));
    }

    [Fact]
    public void RejectsLibraryWithTwoSignatures()
    {
        var so = Fixture("jp-libil2cpp.so");
        Assert.Throws<DataException>(() => MetadataDecryptor.Decrypt(Fixture("jp-metadata.enc"), so.Concat(so).ToArray()));
    }
}
