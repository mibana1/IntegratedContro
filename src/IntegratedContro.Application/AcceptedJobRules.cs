using System.Security.Cryptography;
using System.Text;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;

namespace IntegratedContro.Application;

internal static class AcceptedJobRules
{
    internal static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static bool HoldsReservations(Job j) => j.Active;
    internal static Job FindJob(IEnumerable<Job> jobs, Guid id)
    {
        var job = jobs.SingleOrDefault(x => x.Id == id);
        Require(job is not null, "job_missing", "작업을 찾을 수 없습니다.", 404);
        return job!;
    }
    internal static string ExecutionMode(StepSnapshot[] steps)
    {
        var modes = steps.Select(s => s.Kind == ScenarioStepKind.DisplayLayout || s.ModelDefinition?.IsSimulation == false)
            .Distinct().ToArray();
        return modes.Length > 1 || steps.Any(s => s.Kind == ScenarioStepKind.DisplayLayout) ? "Mixed" : modes[0] ? "Physical" : "Virtual";
    }
    internal static string? RevalidateJob(Guid siteId, IEnumerable<ScenarioDefinition> scenarios, Job job, DateTimeOffset now)
    {
        if (job.Snapshot.SiteId != siteId || job.Snapshot.Mode is not ("Virtual" or "Mixed" or "Physical")) return "현장/실행 모드 불일치";
        if (now >= job.Snapshot.ExpiresAt) return "작업 만료";
        if (job.Snapshot.ScenarioId is { } scenarioId &&
            !scenarios.Any(x => x.Id == scenarioId && x.Version == job.Snapshot.ScenarioVersion)) return "시나리오 정의 변경";
        return null;
    }
    internal static void ValidateStep(ScenarioStep step)
    {
        Require(step is not null && Enum.IsDefined(step.Kind), "invalid_step", "단계 종류를 확인하세요.", 400);
        Require(step!.DelayBeforeMs is >= 0 and <= 3600000 && step.TimeoutMs >= 100 &&
            step.TimeoutMs <= (step.Kind == ScenarioStepKind.DeviceCommand ? 30000 : 3600000) && Enum.IsDefined(step.OnFailure),
            "invalid_timing", "단계 전 대기는 최대 1시간, 제한시간은 명령 0.1~30초 / 조건·표시 0.1초~1시간입니다.", 400);
    }
}
