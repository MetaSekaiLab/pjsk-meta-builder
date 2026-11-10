using PjskMetaBuilder.Core.Schema;

namespace PjskMetaBuilder.Tests;

public class SchemaTests
{
    [Theory]
    [InlineData("_assetbundleName", "assetbundleName")]
    [InlineData("Id", "id")]
    [InlineData("gameCharacterUnitId", "gameCharacterUnitId")]
    public void FieldNameMatchesHarukiRule(string input, string expected) =>
        Assert.Equal(expected, SchemaGenerator.FieldName(input));
}
