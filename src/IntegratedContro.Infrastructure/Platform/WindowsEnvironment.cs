using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using IntegratedContro.Application;
using Microsoft.Win32;

namespace IntegratedContro.Infrastructure;

public sealed class WindowsEnvironment : IPlatformEnvironment
{
    public PlatformEnvironment Read()
    {
        var result = new PlatformEnvironment(OperatingSystem.IsWindows(), "", "", "",
            RuntimeInformation.OSArchitecture.ToString(), RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.OSVersion.Version.ToString());
        if (!OperatingSystem.IsWindows()) return result;
        return ReadWindows(result);
    }

    [SupportedOSPlatform("windows")]
    private static PlatformEnvironment ReadWindows(PlatformEnvironment result)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return result with {
                InstallationType = key?.GetValue("InstallationType") as string ?? "",
                DisplayVersion = key?.GetValue("DisplayVersion") as string ?? "",
                Edition = key?.GetValue("EditionID") as string ?? "",
                DetectionSucceeded = key is not null
            };
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException)
        { return result with { DetectionSucceeded = false }; }
    }
}
