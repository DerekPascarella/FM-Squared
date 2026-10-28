using System.Windows;
using FMSquared.Core.Models;

namespace FMSquared;

public partial class DiscImageOptionsWindow : Window
{
    public DiscConversionFormat ConversionFormat { get; private set; }
    public bool Saved { get; private set; }

    public DiscImageOptionsWindow(DiscConversionFormat conversionFormat)
    {
        InitializeComponent();
        ConversionFormat = conversionFormat;
        CdiRadioButton.IsChecked = conversionFormat == DiscConversionFormat.Cdi;
        CcdRadioButton.IsChecked = !CdiRadioButton.IsChecked;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ConversionFormat = CdiRadioButton.IsChecked == true ? DiscConversionFormat.Cdi : DiscConversionFormat.Ccd;
        Saved = true;
        Close();
    }
}
