using Microsoft.Win32;

namespace WallpaperSwitcher;

internal static class StartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SimpleWallpaperSwitcher";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        if (enabled)
        {
            string executable = Environment.ProcessPath ?? throw new IOException("无法获取程序路径。");
            if (!File.Exists(executable)) throw new FileNotFoundException("自启动程序不存在。", executable);
            key.SetValue(ValueName, LaunchOptions.StartupCommand(executable), RegistryValueKind.String);
        }
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
