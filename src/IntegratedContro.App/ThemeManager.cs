using System.Windows;

namespace IntegratedContro.App;

/// <summary>App-wide appearance only; changing it never requests host or device operations.</summary>
public sealed class ThemeManager : Bindable
{
    public static ThemeManager Current { get; } = new();
    private bool _isDark;
    private string _saveError = "";
    public bool IsDark => _isDark;
    public string ToggleLabel => IsDark ? "라이트 모드" : "다크 모드";
    public string Description => $"현재 {(IsDark ? "다크" : "라이트")} 모드 · {ToggleLabel}로 전환";
    public string SaveError { get => _saveError; private set => Set(ref _saveError, value); }
    public AsyncCommand ToggleCommand { get; }

    private ThemeManager()
    {
        ToggleCommand = new AsyncCommand(() =>
        {
            Apply(!IsDark);
            try { new AppearancePreferences(IsDark).Save(); SaveError = ""; }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
            { SaveError = "화면 모드를 저장하지 못했습니다. 이번 실행에만 적용됩니다."; }
            return Task.CompletedTask;
        });
        Reload();
    }

    public void Reload() => Apply(AppearancePreferences.Load().IsDark);

    private void Apply(bool dark)
    {
        var application = System.Windows.Application.Current;
        application.Dispatcher.VerifyAccess();
        var palette = new ResourceDictionary
        {
            Source = new Uri($"/IntegratedContro.App;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative)
        };
        // DynamicResource references update existing controls as well as newly opened views.
        foreach (var key in palette.Keys) application.Resources[key] = palette[key];
        _isDark = dark;
        Changed(nameof(IsDark)); Changed(nameof(ToggleLabel)); Changed(nameof(Description));
    }
}
