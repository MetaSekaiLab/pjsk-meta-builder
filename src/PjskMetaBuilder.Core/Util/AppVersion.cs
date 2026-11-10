using System.Text.RegularExpressions;

namespace PjskMetaBuilder.Core.Util;

/// <summary>版本号按数字逐段比较：6.10.0 &gt; 6.9.1。</summary>
public static partial class AppVersion
{
    [GeneratedRegex(@"^\d+(\.\d+)+$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? version) => version is not null && Pattern().IsMatch(version);

    public static int Compare(string a, string b)
    {
        if (!IsValid(a) || !IsValid(b))
            throw new FormatException($"版本号格式不对：{a} / {b}");
        var x = a.Split('.').Select(long.Parse).ToArray();
        var y = b.Split('.').Select(long.Parse).ToArray();
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0)
                return c;
        }
        return 0;
    }
}
