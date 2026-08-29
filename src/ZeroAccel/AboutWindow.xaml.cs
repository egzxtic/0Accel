using System.Windows;

namespace ZeroAccel;

public partial class AboutWindow : Window
{
    internal AboutWindow(App app, RawAccelStatus? driver = null)
    {
        InitializeComponent();
        Icon = app.WindowIconImage;
        VersionLabel.Text = "0Accel " + typeof(App).Assembly.GetName().Version?.ToString(3);
        EngineVersionLabel.Text = "Raw Accel "+RawAccelProtocol.Release+" · MIT · RawAccelOfficial";
        if (driver is not null) DriverVersionLabel.Text = "Raw Accel driver "+RawAccelProtocol.DriverVersion;
    }

    private void DismissClicked(object sender, RoutedEventArgs e) => Close();
}
