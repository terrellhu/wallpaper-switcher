namespace WallpaperSwitcher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 2 && args[0] == "--wallpaper-worker") return WallpaperWorker.Run(args[1]);
        if (args.Length == 2 && args[0] == "--verify-package")
            return PackageVerification.Run(args[1]);
        if (args.Length == 2 && args[0] == "--verify-worker") return PackageVerification.VerifyWorker(args[1]);
        if (args.Length == 2 && args[0] == "--verify-session") return MainForm.VerifySession(args[1]);
        if (args.Length == 2 && args[0] == "--test-ui")
        {
            if (!Path.IsPathFullyQualified(args[1]) || !File.Exists(Path.Combine(args[1], "allow-ui-tests"))) return 3;
            SettingsStore.TestDataDirectory = args[1];
            args = ["--settings"];
        }
        var options = LaunchOptions.FromArgs(args, SettingsStore.Load());
        using var mutex = new Mutex(true, @"Local\SimpleWallpaperSwitcher", out bool first);
        if (!first)
        {
            if (!args.Contains("--startup", StringComparer.OrdinalIgnoreCase))
                MessageBox.Show("壁纸轮播已经打开，请从系统托盘打开设置窗口。", "壁纸轮播");
            return 0;
        }
        try
        {
            using var wallpaper = new SafeWallpaperService();
            using var form = new MainForm(wallpaper);
            using var context = new WallpaperApplicationContext(form, options);
            Application.Run(context);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "壁纸轮播未能启动", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        return 0;
    }
}
