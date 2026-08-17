using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Crimson.Avalonia;

public sealed partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);
}
