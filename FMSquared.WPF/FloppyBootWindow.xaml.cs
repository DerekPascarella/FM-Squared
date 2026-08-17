using System.Windows;

namespace FMSquared;

// Explains the floppy boot entry feature and asks for confirmation.
public partial class FloppyBootWindow : Window
{
    public FloppyBootWindow()
    {
        InitializeComponent();
    }

    private void ButtonInsert_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void ButtonCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
