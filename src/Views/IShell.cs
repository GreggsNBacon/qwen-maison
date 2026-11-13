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
    /// <summary>The AI moved (this PC ↔ the AI pool): re-check health, setup and the simple view.</summary>
    Task OnAiLocationChangedAsync();
    /// <summary>Opens the advanced view at the Pool page.</summary>
    void ShowAiSettings();
    /// <summary>Opens the advanced view at the Pool page.</summary>
    void ShowPool();
    /// <summary>Looks for a newer maison on GitHub; shows the title-bar button if there is one. Returns a status line.</summary>
    Task<string> CheckForUpdatesAsync(bool manual);
    /// <summary>Switches this PC's agents to the AI pool (or back to this PC's own AI).</summary>
    Task SetUsePoolAsync(bool on);
    /// <summary>Puts this PC's own Ollama in the pool (or takes it out), and updates every switch that shows it.</summary>
    Task SetIncludeThisPcAsync(bool on);
    /// <summary>The PowerShell maison would run for "claude", "qwen" or "chat" with the current selection and options.</summary>
    string StartCommandPreview(string kind);
}
