using Wpf.Ui.Controls;

namespace QwenLauncher.Views;

/// <summary>What the pages need from the main window.</summary>
public interface IShell
{
    void Log(string message);
    void Toast(string title, string message, ControlAppearance appearance = ControlAppearance.Secondary, SymbolRegular icon = SymbolRegular.Info24);
    Task<bool> ConfirmAsync(string title, string text, string yes);
    string CurrentMainModel { get; }
    void UseModel(string name);
    Task OnModelsChangedAsync();
    void Navigate(string page);
}
