using System.Diagnostics;
using MashiroLauncher.Core.Common;

namespace MashiroLauncher.Core.Launching;

public class LaunchFailedException(string message, int exitCode) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

public sealed class ProcessLauncher
{
    public async Task<int> LaunchAsync(LaunchPlan plan, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = plan.JavaExecutable,
            WorkingDirectory = plan.GameDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in plan.JvmArgs) psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add(plan.MainClass);
        foreach (var arg in plan.GameArgs) psi.ArgumentList.Add(arg);

        // We used to mirror the JVM's stdout/stderr into data/logs/minecraft.log,
        // but that file was opened with append:false + single name, so a second
        // concurrent launch would IOException on the file lock. Minecraft's own
        // Log4j config already writes the same content into
        // <gameDir>/logs/latest.log per instance — that's what the in-app log
        // viewer shows. The Console.WriteLine calls below stay for the dev
        // console / debugger output channel; they're free of any file lock.

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Console.WriteLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Error.WriteLine(e.Data);
            Console.ForegroundColor = prev;
        };

        Log.Step($"Spawning JVM: {Path.GetFileName(plan.JavaExecutable)}");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using var reg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
        });

        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }
}
