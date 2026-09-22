using System.Diagnostics;
using System.IO;

namespace IntegratedContro.UiSmoke;

public static partial class Program
{
    // Each startup path gets a fresh process so static profile/startup environment is isolated.
    private static async Task RunSetupLifecycle(bool includeMedia = false, string? installedRoot = null)
    {
        var root = Path.Combine(Path.GetDirectoryName(IntegratedContro.App.ClientPreferences.ProfilePath)!,
            "setup-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var scenarios = includeMedia ? new[] { "media-setup", "local-media" } : new[] { "new", "decline", "unavailable", "remote", "existing", "broken" };
        foreach (var scenario in scenarios)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            foreach (var arg in new[] { "--setup-lifecycle-child", "--scenario", scenario,
                         "--profile-dir", Path.Combine(root, scenario), "--server-startup", Path.Combine(root, scenario, "server-startup.json") })
                start.ArgumentList.Add(arg);
            if (installedRoot is not null)
            {
                start.ArgumentList.Add("--installed-root"); start.ArgumentList.Add(installedRoot);
            }
            if (Environment.GetCommandLineArgs().Contains("--legacy-password-fixture")) start.ArgumentList.Add("--legacy-password-fixture");
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            try
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4));
                var evidence = await output + await error;
                await File.WriteAllTextAsync(Path.Combine(root, scenario + ".log"), evidence);
                Require(child.ExitCode == 0, $"Setup lifecycle {scenario} failed: {evidence}");
                Console.WriteLine("PASS: " + scenario); Console.WriteLine(evidence);
            }
            finally
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            }
        }
        Console.WriteLine("PASS: setup lifecycle; evidence: " + root);
    }
}
