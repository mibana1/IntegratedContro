using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.AcceptedJobRules;

namespace IntegratedContro.Application;

internal sealed partial class HiperwallService
{
    internal RecoveryContribution DescribeRecovery(StateContext context)
    {
        var state = _host.For(context);
        var edits = JsonDefaults.Copy(state.HiperwallEdits.Where(r => r.Active ||
            r.Steps.Any(s => s.State == HiperwallSendState.Unknown)).ToArray());
        var displays = JsonDefaults.Copy(state.HiperwallDisplays.Where(j => j.Outstanding).ToArray());
        // Retry scheduling alone does not stale a review; changed transmission/cleanup evidence does.
        var fingerprint = Digest(JsonSerializer.Serialize(new {
            Displays = displays.Select(j => new {
                j.Request, j.Requester, j.CloseAt, j.StopRequested, j.StoppedBy,
                Targets = j.Targets.Select(t => new { t.Command, t.OpenState, t.CleanupState, t.OpenAttempted, t.CleanupAttempts, t.Message })
            }).ToArray(), Edits = edits
        }, JsonDefaults.Options));
        return new(fingerprint, review => review with { HiperwallEdits = edits, HiperwallDisplays = displays });
    }
}
