using System.Diagnostics;

namespace PjskMetaBuilder.Core.Il2Cpp;

/// <summary>
/// 以外部进程调用 Il2CppDumper（固定 MetaMikuAI fork 4741d46，config.json 需 RequireAnyKey=false）。
/// 输出只写进日志文件，不打到标准输出。
/// </summary>
public sealed class Il2CppDumperRunner(string dumperDll, string? dotnet = null)
{
    public const string PinnedRevision = "4741d46";

    public string Run(string libil2cpp, string metadata, string outputDir, TimeSpan? timeout = null)
    {
        if (!File.Exists(dumperDll))
            throw new FileNotFoundException("找不到 Il2CppDumper.dll", dumperDll);
        Directory.CreateDirectory(outputDir);
        var log = Path.Combine(outputDir, "il2cppdumper.log");

        var psi = new ProcessStartInfo(dotnet ?? "dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(dumperDll);
        psi.ArgumentList.Add(libil2cpp);
        psi.ArgumentList.Add(metadata);
        psi.ArgumentList.Add(outputDir);
        psi.Environment["DOTNET_ROLL_FORWARD"] = "Major";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 Il2CppDumper");
        process.StandardInput.Close(); // Il2CppDumper 结束时会等按键
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout ?? TimeSpan.FromMinutes(20)))
        {
            process.Kill(true);
            throw new TimeoutException("Il2CppDumper 超时");
        }
        File.WriteAllText(log, stdout.Result + stderr.Result);

        var dll = Path.Combine(outputDir, "DummyDll", "Assembly-CSharp.dll");
        if (process.ExitCode != 0 || !File.Exists(dll))
            throw new DataException($"Il2CppDumper 失败（退出码 {process.ExitCode}），没有生成 Assembly-CSharp.dll，详见 {log}");
        return Path.GetDirectoryName(dll)!;
    }
}
