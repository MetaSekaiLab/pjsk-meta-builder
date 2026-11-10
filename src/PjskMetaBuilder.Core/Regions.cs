namespace PjskMetaBuilder.Core;

public sealed record RegionInfo(string Code, string Package, bool Nuverse, int? QooAppId, string? OfficialUrl);

public static class Regions
{
    public static readonly IReadOnlyDictionary<string, RegionInfo> All = new Dictionary<string, RegionInfo>
    {
        ["jp"] = new("jp", "com.sega.pjsekai", false, 9038, null),
        ["en"] = new("en", "com.sega.ColorfulStage.en", false, 18337, null),
        ["hmt"] = new("hmt", "com.hermes.mk.asia", true, 18298, null),
        ["kr"] = new("kr", "com.pjsekai.kr", true, 20082, null),
        ["cn"] = new("cn", "com.hermes.mk", true, null, "https://pjskyy.ugurl.cn/d9WHL"),
    };

    public static RegionInfo Get(string code) =>
        All.TryGetValue(code, out var region) ? region
            : throw new ArgumentException($"未知地区 {code}（可选：{string.Join(", ", All.Keys)}）");

    public static RegionInfo? ByPackage(string package) =>
        All.Values.FirstOrDefault(r => r.Package == package);
}
