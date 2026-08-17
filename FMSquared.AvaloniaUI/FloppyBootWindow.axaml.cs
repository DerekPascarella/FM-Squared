using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace FMSquared;

// Explains the floppy boot entry feature and asks for confirmation.
public partial class FloppyBootWindow : Window
{
    public bool UserConfirmed { get; private set; }

    public FloppyBootWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void ButtonInsert_Click(object? sender, RoutedEventArgs e)
    {
        UserConfirmed = true;
        Close();
    }

    private void ButtonCancel_Click(object? sender, RoutedEventArgs e)
    {
        UserConfirmed = false;
        Close();
    }
}
