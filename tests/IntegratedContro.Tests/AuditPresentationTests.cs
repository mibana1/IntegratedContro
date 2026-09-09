using System.Text.Json;
using IntegratedContro.Application;
using IntegratedContro.Core;

namespace IntegratedContro.Tests;

public sealed class AuditPresentationTests
{
    [Fact]
    public async Task New_audit_captures_names_and_describes_dispatch_without_changing_raw_evidence()
    {
        using var r = new Rig(); var d = r.Device(name: "입구 조명");
        var job = r.Service.Submit(r.Admin.Token, r.Manual());
        await r.Service.DispatchNextAsync();
        var before = r.Service.GetState(r.Admin.Token).Audit.Single(a => a.Action == "JobAccepted");
        Assert.Equal("admin", before.UserName); Assert.Equal("작업 접수", before.EventName);
        Assert.Contains("입구 조명", before.Message); Assert.Contains("전원 ON", before.Message);
        r.Service.SaveDevice(r.Admin.Token, new(r.Generation, d.Id, d.PcId, d.PcName, "변경된 이름",
            d.ConnectionId, d.ModelId, ExpectedVersion: d.Version));
        var after = r.Service.GetState(r.Admin.Token).Audit.Single(a => a.Action == "JobAccepted");
        Assert.Equal(before, after); Assert.Contains($"job={job.Id}", after.Detail);
        var result = r.Service.GetState(r.Admin.Token).Audit.Single(a => a.Action == "DispatchResult");
        Assert.Equal("호스트", result.UserName); Assert.Contains("1단계", result.Message);
        Assert.Contains("가상 실행 완료", result.Message);
        Assert.DoesNotContain(Rig.Password, JsonSerializer.Serialize(after));
        r.Restart(); Assert.Equal(before, r.Service.GetState(r.Admin.Token).Audit.Single(a => a.Action == "JobAccepted"));
    }
    [Fact]
    public void Old_entries_gain_readable_labels_without_rewriting_original_fields()
    {
        var id = Guid.NewGuid();
        var state = new HostState { Accounts = [new() { Id = id, Name = "operator", PasswordHash = "unused" }] };
        var old = new AuditEntry(DateTimeOffset.UtcNow, id, "ReleasePreservingJobs", "session=original-id");
        var display = AuditPresentation.Enrich(old, state);
        Assert.Equal("사용 종료", display.EventName); Assert.Equal("operator", display.UserName);
        Assert.Contains("계속 처리", display.Message); Assert.Equal(old.Detail, display.Detail);
        Assert.Equal(old.At, display.At); Assert.Null(old.Message);
        var unknown = AuditPresentation.Enrich(old with { Action = "FutureEvent", Detail = "원본 값" }, state);
        Assert.Equal("원본 값", unknown.Message);
    }
}
