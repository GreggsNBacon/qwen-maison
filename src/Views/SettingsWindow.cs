using System.Windows;
using Wpf.Ui.Controls;

namespace Maison.Views;

/// <summary>
/// Settings in their own window, for the simple view (its terminal is WebView2, which would draw over anything shown
/// inside the main window). It borrows the main window's one Settings page; the main window takes it back on close.
/// </summary>
public sealed class SettingsWindow : FluentWindow
{
    private readonly System.Windows.Controls.Grid _root = new();
    private readonly UIElement _page;

    public SettingsWindow(UIElement page)
    {
        _page = page;
        Title = "Settings · maison";
        Width = 980;
        Height = 820;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        WindowCornerPreference = WindowCornerPreference.Round;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/brand/maison.ico"));

        _root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition());
        _root.Children.Add(new TitleBar { Title = Title });
        System.Windows.Controls.Grid.SetRow(page, 1);
        _root.Children.Add(page);
        Content = _root;
    }

    /// <summary>Lets go of the Settings page so the main window can have it back.</summary>
    public void Release()
    {
        _root.Children.Remove(_page);
        System.Windows.Controls.Grid.SetRow(_page, 0);
    }
}
