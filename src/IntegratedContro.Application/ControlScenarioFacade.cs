using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;
using static IntegratedContro.Application.ControlAuthorization;
using static IntegratedContro.Application.AcceptedJobRules;

namespace IntegratedContro.Application;

public sealed partial class ControlService
{
    public Job Submit(string token, SubmitRequest request) => _scenarios.Submit(token, request);
    public Job Cancel(string token, JobActionRequest request) => _scenarios.Cancel(token, request);
    public Job BeginManualSwitch(string token, JobActionRequest request) => _scenarios.BeginManualSwitch(token, request);
    public Job SubmitLightBatch(string token, LightBatchRequest request) => _scenarios.SubmitLightBatch(token, request);
    public Task<bool> DispatchNextAsync(CancellationToken hostStopping = default) => _scenarios.DispatchNextAsync(hostStopping);
}
