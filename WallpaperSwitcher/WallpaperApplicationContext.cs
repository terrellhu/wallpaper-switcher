namespace WallpaperSwitcher;

// A hidden form owns the tray and hotkeys, without ever flashing a settings window at logon.
internal sealed class WallpaperApplicationContext : ApplicationContext
{
    public WallpaperApplicationContext(MainForm form, LaunchOptions options)
    {
        MainForm = form;
        _ = form.Handle; // Register a UI-thread handle without calling Show.
        form.BeginInvoke(async () =>
        {
            if (form.IsDisposed) return;
            if (!options.StartInTray) form.Show();
            await form.InitializeAsync(options.AutoStart);
        });
    }
}
