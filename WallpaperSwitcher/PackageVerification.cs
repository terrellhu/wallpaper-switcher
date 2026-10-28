using System.Diagnostics;
using System.Text.Json;

namespace WallpaperSwitcher;

// Non-interactive packaging check: never changes wallpaper, user settings or startup registration.
internal static class PackageVerification
{
    public static int VerifyWorker(string reportPath)
    {
        string data = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "worker-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "allow-worker-tests"), "test");
        try
        {
            using var service = new SafeWallpaperService(dataDirectory: data);
            service.RunTest("test-delay");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Success = true, WorkerProcessId = service.LastWorkerProcessId }));
            return 0;
        }
        catch (Exception ex)
        { File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Success = false, Error = ex.Message })); return 1; }
    }
    public static int Run(string reportPath)
    {
        try
        {
            using var icon = AppBrand.CreateIcon();
            using var logo = AppBrand.CreateLogo();
            using var wallpaper = new WindowsWallpaper();
            _ = WallpaperNative.GetImage();
            using var process = Process.GetCurrentProcess();
            var runtimeModule = process.Modules.Cast<ProcessModule>()
                .FirstOrDefault(module => module.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase));
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                Success = true,
                Runtime = Environment.Version.ToString(),
                // Windows single-file hosts can statically link CoreCLR into the executable.
                RuntimeModule = runtimeModule?.FileName ?? process.MainModule!.FileName,
                RuntimeLinkedIntoExecutable = runtimeModule is null,
                Executable = Environment.ProcessPath,
                IconWidth = icon.Width,
                LogoWidth = logo.Width,
                DesktopApiInitialized = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Success = false, Error = ex.ToString() }));
            return 1;
        }
    }
}
