using System.Windows;

namespace MobileBridge;

public partial class LicenseWindow : Window
{
    public LicenseWindow(string licenseText)
    {
        InitializeComponent();
        LicenseBox.Text = licenseText;
    }

    void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    void Decline_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
