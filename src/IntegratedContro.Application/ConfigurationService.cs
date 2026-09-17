using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;
using static IntegratedContro.Application.ControlAuthorization;
using static IntegratedContro.Application.AcceptedJobRules;

namespace IntegratedContro.Application;

public sealed partial class ControlService
{
    public AccountView CreateAccount(string token, CreateAccountRequest request) => _host.CreateAccount(token, request);
    public bool UpdateAccount(string token, UpdateAccountRequest request) => _host.UpdateAccount(token, request);
    public ScenarioDefinition SaveScenario(string token, ScenarioRequest request) => _scenarios.SaveScenario(token, request);
    public bool DeleteScenario(string token, DeleteScenarioRequest request) => _scenarios.DeleteScenario(token, request);
}
