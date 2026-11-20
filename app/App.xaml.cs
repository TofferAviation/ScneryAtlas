using System.Windows;
namespace SceneryAtlas.App;
public partial class App : Application
{
    private Mutex? mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        var preview = Environment.GetEnvironmentVariable("SCENERY_ATLAS_PREVIEW") == "1";
        mutex = new Mutex(true, preview ? "Local\\SceneryAtlas-Desktop-Preview" : "Local\\SceneryAtlas-Desktop-0.1", out var created);
        if (!created) { MessageBox.Show("Scenery Atlas is already open."); Shutdown(); return; }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e) { mutex?.Dispose(); base.OnExit(e); }
}
