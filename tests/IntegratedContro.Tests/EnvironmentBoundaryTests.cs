using System.Text.RegularExpressions;
using IntegratedContro.Application;
using IntegratedContro.Core;

namespace IntegratedContro.Tests;

// Executable architecture rules: reject new environment dependencies outside the adapter boundaries.
public sealed class EnvironmentBoundaryTests
{
    [Fact]
    public void Common_assemblies_do_not_reference_environment_implementations()
    {
        foreach (var assembly in new[] { typeof(HostState).Assembly, typeof(ControlService).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => Forbidden(reference.Name ?? ""));
    }

    private static bool Forbidden(string name) => new[] {
        "IntegratedContro.Infrastructure", "IntegratedContro.App", "Microsoft.Data.Sqlite",
        "Microsoft.Win32.Registry", "System.Security.Cryptography.ProtectedData", "LibVLCSharp",
        "PresentationFramework", "PresentationCore", "WindowsBase"
    }.Any(name.StartsWith);

    [Fact]
    public void Common_source_has_no_native_or_environment_access_and_camera_has_no_engine_reference()
    {
        foreach (var project in new[] { "IntegratedContro.Core", "IntegratedContro.Application" })
        {
            var directory = Path.Combine(Root(), "src", project);
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                               !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
            {
                var source = File.ReadAllText(file);
                Assert.False(Regex.IsMatch(source,
                    @"\b(using\s+(System\.Windows|Microsoft\.Win32|LibVLCSharp|Microsoft\.Data\.Sqlite)|DllImport|LibraryImport|ComImport|Marshal\.|ProtectedData\.|Registry\.|OperatingSystem\.|Environment\.GetFolderPath)"),
                    $"Environment dependency in {file}");
            }
        }
        foreach (var file in new[] { "CameraViewModel.cs", "CameraView.xaml", "CameraView.xaml.cs" })
        {
            var source = File.ReadAllText(Path.Combine(Root(), "src", "IntegratedContro.App", file));
            Assert.DoesNotContain("LibVLC", source);
            Assert.DoesNotContain("VlcVideo", source);
            Assert.DoesNotContain("NativePlayer", source);
        }
        foreach (var file in new[] { "Program.cs", "HostSetup.cs" })
        {
            var source = File.ReadAllText(Path.Combine(Root(), "src", "IntegratedContro.ControlHost", file));
            Assert.DoesNotContain("new Sqlite", source);
            Assert.DoesNotContain("ConnectionString", source);
            Assert.DoesNotContain("new WindowsCurrentUserSecretProtector", source);
        }
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "IntegratedContro.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root");
    }
}
