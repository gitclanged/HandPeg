namespace HandPegApp;

public static class Program
{
    /// <summary>
    /// The entry point, written out by hand (App.xaml is built as a Page, so none is generated) because
    /// Velopack's bootstrap has to be the very first thing that runs: when the installer or updater starts
    /// the program to finish an install, update or uninstall, it does that here and exits, before any of
    /// WPF has been touched.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        // A self-contained copy (portable.txt, --portable) is not an installed one: it has no installer to answer,
        // and nothing from an earlier build elsewhere on the computer to bring over. It stays in its folder.
        if (!Services.AppPaths.IsSelfContained)
        {
            Velopack.VelopackApp.Build().Run();

            // Before anything reads a setting: what an earlier build kept beside the program is brought over.
            Services.DataMigration.Run();
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
