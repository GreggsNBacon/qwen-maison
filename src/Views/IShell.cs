using Wpf.Ui.Controls;

namespace Maison.Views;

/// <summary>What the pages need from the main window.</summary>
public interface IShell
{
    void Log(string message);
    void Toast(string title, string message, ControlAppearance appearance = ControlAppearance.Secondary, SymbolRegular icon = SymbolRegular.Info24);
    Task<bool> ConfirmAsync(string title, string text, string yes, string no = "Cancel");
    string CurrentMainModel { get; }
    void UseModel(string name);
    Task OnModelsChangedAsync();
    void Navigate(string page);
    /// <summary>Runs a PowerShell command in a new in-app terminal tab (PATH refreshed) and shows it.</summary>
    Task RunInTerminalAsync(string title, string command);
    /// <summary>Model name → when it was last used (launched or loaded).</summary>
    IReadOnlyDictionary<string, DateTime> ModelLastUsed { get; }
    /// <summary>Refreshes the Dashboard's copyable commands after a start-command option changed.</summary>
    void UpdateCommands();
    /// <summary>The AI moved (this PC ↔ another PC): re-check health, setup and the simple view.</summary>
    Task OnAiLocationChangedAsync();
    /// <summary>Opens the advanced view at Settings → AI on another PC.</summary>
    void ShowAiSettings();
    /// <summary>The PowerShell maison would run for "claude", "qwen" or "chat" with the current selection and options.</summary>
    string StartCommandPreview(string kind);
}
