namespace IntegratedContro.Application;

// Only validates a camera's optional reference against the current wall inventory.
internal interface ICameraContentCatalog
{
    void ValidateMapping(int configurationVersion, string? selector, string? value);
}
