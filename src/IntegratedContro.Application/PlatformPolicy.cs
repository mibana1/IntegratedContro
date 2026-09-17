namespace IntegratedContro.Application;

public static class PlatformPolicy
{
    public static PlatformSupport Evaluate(PlatformEnvironment environment)
    {
        var supported = environment.DetectionSucceeded && environment.IsWindows &&
            environment.InstallationType == "Client" && environment.DisplayVersion == "25H2" &&
            environment.Edition is "Professional" or "Enterprise" &&
            environment.OsArchitecture == "X64" && environment.ProcessArchitecture == "X64";
        var detail = !environment.DetectionSucceeded ? "OS 정보 조회 실패" : !environment.IsWindows ? "Windows 외 OS" :
            $"Windows {environment.DisplayVersion} {environment.Edition}; OS {environment.OsArchitecture} / 프로세스 {environment.ProcessArchitecture}; build {environment.Build}";
        return new(supported, $"{(supported ? "공식 지원 대상" : "공식 지원 범위 외")}: {detail}. 기능/실장비 검증과 별개.");
    }
}
