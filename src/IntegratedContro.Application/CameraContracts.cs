using System.Text.Json;
using IntegratedContro.Core;
using static IntegratedContro.Application.Validation;
using static IntegratedContro.Application.ControlAuthorization;
using static IntegratedContro.Application.AcceptedJobRules;

namespace IntegratedContro.Application;

// Only validates a camera's optional reference against the current wall inventory.
internal interface ICameraContentCatalog
{
    void ValidateMapping(int configurationVersion, string? selector, string? value);
}
