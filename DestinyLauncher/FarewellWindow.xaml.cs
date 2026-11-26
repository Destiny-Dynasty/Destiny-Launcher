using System.Windows;

namespace DestinyLauncher;

public partial class FarewellWindow : Window
{
    public FarewellWindow() { InitializeComponent(); PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); }; }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
