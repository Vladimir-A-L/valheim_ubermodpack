using System;
using System.Diagnostics;
using System.IO;

// GUI launcher: CreateNoWindow предотвращает запуск терминала и перехват фокуса.
internal static class CaptureHost
{
    private static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + args[0] +
                "\" -OutputDirectory \"" + args[1] + "\""
        };
        using (var child = Process.Start(start))
        {
            if (child.WaitForExit(40000)) return child.ExitCode;
            child.Kill();
            return 3;
        }
    }
}
