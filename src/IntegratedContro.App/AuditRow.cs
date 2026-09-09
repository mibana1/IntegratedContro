using IntegratedContro.Core;

namespace IntegratedContro.App;

public sealed record AuditRow(AuditEntry Entry)
{
    public string Time => Entry.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string Actor => Entry.UserName ?? (Entry.UserId is null ? "호스트" : "계정 확인 필요");
    public string Event => Entry.EventName ?? Entry.Action;
    public string Summary => Entry.Message ?? Entry.Detail;
    public string Raw => $"시각: {Entry.At:O}\n사용자 ID: {Entry.UserId}\n이벤트 코드: {Entry.Action}\n원본 내용: {Entry.Detail}";
}
