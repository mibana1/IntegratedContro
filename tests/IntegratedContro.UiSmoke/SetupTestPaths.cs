using System.IO;

namespace IntegratedContro.UiSmoke;

internal static class SetupTestPaths
{
    private static string? Installation
    {
        get
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "--installed-root");
            if (index < 0) return null;
            var path = Path.GetFullPath(args[index + 1]);
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "IntegratedContro.sln"))) root = root.Parent;
            if (root is null || !path.StartsWith(Path.Combine(root.FullName, "artifacts", "installer-smoke") + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only the isolated installer smoke installation may be tested.");
            return path;
        }
    }
    public static string Host(string fallback) => Installation is { } p ? Path.Combine(p, "ControlHost", "IntegratedContro.ControlHost.exe") : fallback;
    public static string Media(string fallback) => Installation is { } p ? Path.Combine(p, "MediaMTX", "mediamtx.exe") : fallback;
    public static string App(string fallback) => Installation is { } p ? Path.Combine(p, "App") : fallback;
}
