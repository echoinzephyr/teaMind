using Microsoft.UI.Xaml;

namespace TeaMind.App;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            try { Directory.CreateDirectory(AppSettings.DataDirectory); File.AppendAllText(Path.Combine(AppSettings.DataDirectory, "error.log"), DateTime.Now + " " + args.Message + "\n" + args.Exception + Environment.NewLine); }
            catch { }
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window = new MainWindow(); Window.Activate();
    }
}
