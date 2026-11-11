using System.Text.Json;
using System.Text.RegularExpressions;
using PjskMetaBuilder.Core.Util;

namespace PjskMetaBuilder.Core.Sources;

public sealed record GooglePlayInfo(string VersionName, DateTimeOffset? UpdatedAt);

/// <summary>
/// Google Play 网页（设计文档 §7.5）：只用来提前发现新版本，不下载。
/// <c>play.google.com/store/apps/details?id=…</c> 里内嵌的 <c>AF_initDataCallback({key: 'ds:5', …})</c>：
/// versionName 在 <c>[1][2][140][0][0][0]</c>，最后更新时间（unix 秒）在 <c>[1][2][145][0][1][0]</c>；没有 versionCode。
/// 这是 Google 的内部数据格式，随时可能变：解析失败抛 <see cref="DataException"/>，调用方只记告警。
/// 各服要用自己所在的商店地区（hmt、kr 在美区页面上没有数据）；cn 不在 Google Play 上。2026-11-12 实测四服都能取到。
/// </summary>
public sealed partial class GooglePlayPage(HttpClient? http = null)
{
    /// <summary>各服在 Google Play 上的商店地区和语言。</summary>
    public static readonly IReadOnlyDictionary<string, (string Country, string Language)> Storefronts =
        new Dictionary<string, (string, string)>
        {
            ["jp"] = ("JP", "ja"),
            ["en"] = ("US", "en"),
            ["hmt"] = ("TW", "zh-TW"),
            ["kr"] = ("KR", "ko"),
        };

    private readonly Downloader _downloader = new(http ?? Downloader.CreateClient());

    public GooglePlayInfo Probe(RegionInfo region)
    {
        if (!Storefronts.TryGetValue(region.Code, out var store))
            throw new ArgumentException($"{region.Code} 不在 Google Play 上");
        var uri = new Uri($"https://play.google.com/store/apps/details?id={Uri.EscapeDataString(region.Package)}&hl={store.Language}&gl={store.Country}");
        using var resp = _downloader.Send(u =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, u);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
            req.Headers.AcceptLanguage.ParseAdd(store.Language);
            return req;
        }, uri, HttpCompletionOption.ResponseContentRead);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Google Play {region.Code}：HTTP {(int)resp.StatusCode}");
        using var reader = new StreamReader(resp.Content.ReadAsStream());
        return Parse(reader.ReadToEnd());
    }

    [GeneratedRegex(@"AF_initDataCallback\(\{key: 'ds:5', hash: '\d+', data:(.*?), sideChannel: \{\}\}\);</script>", RegexOptions.Singleline)]
    private static partial Regex Ds5();

    public static GooglePlayInfo Parse(string html)
    {
        var m = Ds5().Match(html);
        if (!m.Success)
            throw new DataException("Google Play 页面里没有 ds:5 数据（页面格式变了，或这个商店地区没有上架）");
        try
        {
            using var doc = JsonDocument.Parse(m.Groups[1].Value);
            var app = doc.RootElement[1][2];
            var versionName = app[140][0][0][0].GetString();
            if (!AppVersion.IsValid(versionName))
                throw new DataException($"Google Play 上的 versionName 格式不对：{versionName}");
            DateTimeOffset? updated = null;
            try
            {
                updated = DateTimeOffset.FromUnixTimeSeconds(app[145][0][1][0].GetInt64());
            }
            catch (Exception e) when (e is InvalidOperationException or IndexOutOfRangeException or FormatException or ArgumentOutOfRangeException)
            {
                // 更新时间只用于日志
            }
            return new GooglePlayInfo(versionName!, updated);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or IndexOutOfRangeException or KeyNotFoundException)
        {
            throw new DataException($"Google Play 页面的 ds:5 结构变了：{e.Message}");
        }
    }
}
