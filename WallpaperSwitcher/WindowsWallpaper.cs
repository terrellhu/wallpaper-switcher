using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WallpaperSwitcher;

public sealed class WindowsWallpaper : IWallpaperService, IDisposable
{
    // The shell COM service can stop responding after a session/desktop change.
    // A single screen needs only the documented User32 wallpaper API, with read-back verification.
    private IDesktopWallpaper? desktopInstance;
    private IDesktopWallpaper desktop => desktopInstance ??= (IDesktopWallpaper)Activator.CreateInstance(
        Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"))!)!;
    private static bool SingleScreen => Screen.AllScreens.Length == 1;
    private readonly string data;
    public WindowsWallpaper(string? dataDirectory = null)
    {
        data = dataDirectory ?? SettingsStore.DataDirectory;
        try
        {
            var saved = JsonSerializer.Deserialize<FrameCache>(File.ReadAllText(Path.Combine(data, "frame-cache.json")));
            if (saved?.Frames is { Length: 2 } && saved.ActiveSlot is >= -1 and <= 1)
            { frames = saved.Frames; activeSlot = saved.ActiveSlot; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
    private string SnapshotPath => Path.Combine(data, "restore.json");
    private sealed record CachedFrame(string Key, string Path, long Length, DateTime Modified);
    private sealed record FrameCache(CachedFrame?[] Frames, int ActiveSlot);
    private void SaveCache() => SettingsStore.WriteAtomic(Path.Combine(data, "frame-cache.json"), new FrameCache(frames, activeSlot));
    private readonly CachedFrame?[] frames = new CachedFrame?[2];
    private int activeSlot = -1;
    public bool HasPendingRestore => File.Exists(SnapshotPath);
    public WallpaperFit FitMode { get; private set; } = WallpaperFit.Fill;
    public bool AutoCollage { get; set; } = true;
    public int CollageCount { get; set; }
    private IReadOnlyList<string> displayedPaths = [];
    public static Size TargetSize(WallpaperFit mode) => mode == WallpaperFit.Span
        ? SystemInformation.VirtualScreen.Size : Screen.PrimaryScreen?.Bounds.Size ?? new Size(1920, 1080);

    public IReadOnlyList<string> PlanFrame(IReadOnlyList<string> candidates) =>
        CollageRenderer.PlanFrame(candidates, TargetSize(FitMode), AutoCollage, CollageCount);

    public void ApplyFrame(IReadOnlyList<string> paths)
    {
        if (paths.Count == 1) { Apply(paths[0]); return; }
        var target = TargetSize(FitMode);
        var mode = FitMode == WallpaperFit.Span ? WallpaperFit.Span : WallpaperFit.Fill;
        string key = "collage:" + target.Width + "x" + target.Height + "\0" + SourceKey(paths);
        ApplyGeneratedFrame(key, mode, () => CollageRenderer.Render(paths, target));
        displayedPaths = paths.ToArray();
    }

    public void SetFitMode(WallpaperFit mode, bool updateDesktop = false)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        FitMode = mode;
        if (updateDesktop)
        {
            if (displayedPaths.Count > 1) ApplyFrame(displayedPaths);
            else if (SingleScreen)
            {
                WallpaperNative.SetPosition(mode);
                WallpaperNative.SetImage(WallpaperNative.GetImage());
            }
            else desktop.SetPosition((int)mode);
        }
    }

    public void Capture()
    {
        if (HasPendingRestore) throw new IOException("原背景记录尚未恢复，禁止覆盖。");
        Directory.CreateDirectory(data);
        if (SingleScreen)
        {
            string original = WallpaperNative.GetImage();
            if (WallpaperNative.GetBackgroundType() == 1) original = "";
            RejectOwnCache(original);
            string backup = "";
            if (!string.IsNullOrEmpty(original))
            {
                backup = Path.Combine(data, "original-0" + Path.GetExtension(original));
                if (!string.Equals(original, backup, StringComparison.OrdinalIgnoreCase)) File.Copy(original, backup, true);
            }
            SettingsStore.WriteAtomic(SnapshotPath, new DesktopSnapshot((int)WallpaperNative.GetPosition(),
                WallpaperNative.GetBackgroundColor(), [new("single-screen", original, backup)], !string.IsNullOrEmpty(original)));
            return;
        }
        bool wallpaperEnabled = (desktop.GetStatus() & 1) != 0;
        var monitors = new List<MonitorSnapshot>();
        uint count = desktop.GetMonitorDevicePathCount();
        for (uint i = 0; i < count; i++)
        {
            string id = desktop.GetMonitorDevicePathAt(i);
            string original = desktop.GetWallpaper(id);
            RejectOwnCache(original);
            string backup = "";
            if (wallpaperEnabled && !string.IsNullOrEmpty(original))
            {
                // Keep copies after restoring: Windows may continue referring to them.
                backup = Path.Combine(data, $"original-{i}{Path.GetExtension(original)}");
                if (!string.Equals(original, backup, StringComparison.OrdinalIgnoreCase)) File.Copy(original, backup, true);
            }
            monitors.Add(new(id, original, backup));
        }
        if (monitors.Count == 0) throw new IOException("未找到可设置壁纸的显示器。");
        SettingsStore.WriteAtomic(SnapshotPath, new DesktopSnapshot(
            desktop.GetPosition(), desktop.GetBackgroundColor(), monitors, wallpaperEnabled));
    }

    public void Apply(string path)
    {
        // Cover the largest monitor on each axis; a smaller primary monitor must not lower a secondary's quality.
        Size target = FitMode == WallpaperFit.Span ? TargetSize(FitMode)
            : new Size(Screen.AllScreens.Max(s => s.Bounds.Width), Screen.AllScreens.Max(s => s.Bounds.Height));
        Size output = SingleRenderSize(CollageRenderer.ReadSize(path), target, FitMode);
        string key = "single:v2:" + output.Width + "x" + output.Height + "\0" + SourceKey([path]);
        ApplyGeneratedFrame(key, FitMode, () => RenderSingle(path, output));
        displayedPaths = [path];
    }

    internal static Size SingleRenderSize(Size source, Size target, WallpaperFit mode)
    {
        if (source.Width <= 0 || source.Height <= 0 || target.Width <= 0 || target.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(source));
        // Center/Tile deliberately use original pixels. Scaling these would change the visible layout.
        if (mode is WallpaperFit.Center or WallpaperFit.Tile) return source;
        double horizontal = (double)target.Width / source.Width, vertical = (double)target.Height / source.Height;
        double scale = Math.Min(1, mode == WallpaperFit.Fit ? Math.Min(horizontal, vertical) : Math.Max(horizontal, vertical));
        scale = Math.Min(scale, 8192.0 / Math.Max(source.Width, source.Height));
        scale = Math.Min(scale, Math.Sqrt(16777216.0 / ((double)source.Width * source.Height)));
        return new Size(Math.Max(1, (int)Math.Floor(source.Width * scale)), Math.Max(1, (int)Math.Floor(source.Height * scale)));
    }

    internal static Bitmap RenderSingle(string path, Size output)
    {
        // Decode first; GIF and TIFF use the first frame.
        using var image = CollageRenderer.LoadImage(path);
        var bitmap = new Bitmap(output.Width, output.Height, PixelFormat.Format24bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Black);
            if (output == image.Size) graphics.DrawImage(image, 0, 0, image.Width, image.Height);
            else
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                using var attributes = new ImageAttributes();
                attributes.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
                graphics.DrawImage(image, new Rectangle(Point.Empty, output), 0, 0, image.Width, image.Height,
                    GraphicsUnit.Pixel, attributes);
            }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static string SourceKey(IReadOnlyList<string> paths) => string.Join("\0", paths.Select(path =>
    {
        var info = new FileInfo(path);
        return path + "\0" + info.Length + "\0" + info.LastWriteTimeUtc.Ticks;
    }));

    private void RejectOwnCache(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        string full = Path.GetFullPath(path);
        if (string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(data), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(full).StartsWith("current-", StringComparison.OrdinalIgnoreCase))
            throw new IOException("当前仍是软件缓存壁纸，不能把它当成原背景。请先在 Windows 设置中恢复背景。");
    }

    private void ApplyGeneratedFrame(string key, WallpaperFit mode, Func<Bitmap> render)
    {
        // Only two disk entries and tiny metadata remain resident; no decoded image cache.
        for (int i = 0; i < frames.Length; i++)
        {
            var entry = frames[i];
            if (entry?.Key != key) continue;
            var file = new FileInfo(entry.Path);
            if (!file.Exists || file.Length != entry.Length || file.LastWriteTimeUtc != entry.Modified) continue;
            ApplyCachedBitmap(entry.Path, mode);
            bool changed = activeSlot != i;
            activeSlot = i;
            if (changed) SaveCache();
            return;
        }
        // Never overwrite the currently displayed file; publish a completed JPEG atomically.
        int slot = activeSlot == 0 ? 1 : 0;
        string cache = Path.Combine(data, $"current-{slot}.jpg");
        using (var bitmap = render()) WallpaperEncoding.SaveJpeg(bitmap, cache);
        var info = new FileInfo(cache);
        frames[slot] = new(key, cache, info.Length, info.LastWriteTimeUtc);
        ApplyCachedBitmap(cache, mode);
        activeSlot = slot;
        SaveCache();
    }

    private void ApplyCachedBitmap(string cache, WallpaperFit mode)
    {
        if (SingleScreen)
        {
            WallpaperNative.SetBackgroundColor(0);
            WallpaperNative.SetPosition(mode);
            WallpaperNative.SetBackgroundType(0);
            WallpaperNative.SetImage(cache);
            if (!string.Equals(WallpaperNative.GetImage(), cache, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Windows 未应用壁纸，请重试。");
            return;
        }
        desktop.SetBackgroundColor(0); // Use black letterboxing; original color is in the restore snapshot.
        desktop.SetPosition((int)mode);
        desktop.SetWallpaper(null, cache);
        desktop.Enable(true);
    }

    public void Restore() => Restore(keepRecord: false);
    public void Restore(bool keepRecord)
    {
        if (!HasPendingRestore) return;
        var snapshot = JsonSerializer.Deserialize<DesktopSnapshot>(File.ReadAllText(SnapshotPath))
            ?? throw new IOException("原壁纸记录无法读取，请保留恢复文件。");
        if (SingleScreen && snapshot.Monitors.Count == 1)
        {
            string target = snapshot.RestoreSolidColor ? "" : snapshot.Monitors[0].BackupPath;
            WallpaperNative.SetPosition((WallpaperFit)snapshot.Position);
            WallpaperNative.SetBackgroundColor(snapshot.Background);
            WallpaperNative.SetBackgroundType(snapshot.RestoreSolidColor ? 1 : 0);
            WallpaperNative.SetImage(target);
            if (!string.Equals(WallpaperNative.GetImage(), target, StringComparison.OrdinalIgnoreCase)
                || WallpaperNative.GetBackgroundColor() != snapshot.Background
                || WallpaperNative.GetBackgroundType() != (snapshot.RestoreSolidColor ? 1 : 0))
                throw new IOException("Windows 尚未恢复原背景，恢复记录已保留，请重试。");
            if (!keepRecord) File.Delete(SnapshotPath);
            return;
        }
        desktop.SetPosition(snapshot.Position);
        desktop.SetBackgroundColor(snapshot.Background);
        if (snapshot.RestoreSolidColor)
        {
            // Clear the persisted wallpaper as well: Enable(false) alone is not durable across processes.
            WallpaperNative.ClearImage();
            desktop.Enable(false);
            WallpaperNative.SetBackgroundType(1);
            if ((desktop.GetStatus() & 1) != 0)
                throw new IOException("Windows 未能恢复纯色背景，恢复记录已保留。");
            if (!keepRecord) File.Delete(SnapshotPath);
            return;
        }
        var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (uint i = 0; i < desktop.GetMonitorDevicePathCount(); i++) connected.Add(desktop.GetMonitorDevicePathAt(i));
        var remaining = new List<MonitorSnapshot>();
        foreach (var monitor in snapshot.Monitors)
        {
            if (!connected.Contains(monitor.MonitorId)) { remaining.Add(monitor); continue; }
            // An individual solid-color monitor must not reactivate its cached image either.
            desktop.SetWallpaper(monitor.MonitorId, string.IsNullOrEmpty(monitor.BackupPath)
                ? CreateSolidColorImage(snapshot.Background) : monitor.BackupPath);
        }
        if (remaining.Count > 0)
        {
            if (!keepRecord) SettingsStore.WriteAtomic(SnapshotPath, snapshot with { Monitors = remaining });
            throw new IOException("部分原显示器已断开。请连接原显示器后重试恢复。");
        }
        if (!keepRecord) File.Delete(SnapshotPath);
    }

    private string CreateSolidColorImage(uint color)
    {
        string path = Path.Combine(data, $"solid-{color:x6}.bmp");
        using var bitmap = new Bitmap(8, 8);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.Clear(Color.FromArgb((int)(color & 255), (int)((color >> 8) & 255), (int)((color >> 16) & 255)));
        bitmap.Save(path, ImageFormat.Bmp);
        return path;
    }

    public void Dispose() { if (desktopInstance is not null) Marshal.ReleaseComObject(desktopInstance); }
}

internal static class WallpaperEncoding
{
    private static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().Single(c => c.FormatID == ImageFormat.Jpeg.Guid);
    internal const long JpegQuality = 92;

    public static void SaveJpeg(Bitmap bitmap, string path)
    {
        using var buffer = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);
        bitmap.Save(buffer, Jpeg, parameters);
        string temporary = path + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                output.Write(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
            File.Move(temporary, path, true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

// Vtable order follows shobjidl_core.h. HRESULTs become managed exceptions.
[ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDesktopWallpaper
{
    void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
    [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);
    [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint monitorIndex);
    uint GetMonitorDevicePathCount();
    void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out NativeRect rect);
    void SetBackgroundColor(uint color);
    uint GetBackgroundColor();
    void SetPosition(int position);
    int GetPosition();
    void SetSlideshow(IntPtr items);
    IntPtr GetSlideshow();
    void SetSlideshowOptions(uint options, uint milliseconds);
    void GetSlideshowOptions(out uint options, out uint milliseconds);
    void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, int direction);
    uint GetStatus();
    void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect { public int Left, Top, Right, Bottom; }

internal static class HotkeyNative
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr window, int id);
}

internal static class WallpaperNative
{
    public static int GetBackgroundType()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers");
        return key?.GetValue("BackgroundType") is int value ? value : string.IsNullOrEmpty(GetImage()) ? 1 : 0;
    }
    public static void SetBackgroundType(int value)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers");
        key.SetValue("BackgroundType", value, Microsoft.Win32.RegistryValueKind.DWord);
    }
    public static void AssertDesktopAvailable()
    {
        if (!IsInteractiveDesktop()) throw new IOException("桌面已锁定或不可用，已停止壁纸操作。");
        foreach (IntPtr window in new[] { FindWindow("Shell_TrayWnd", null), GetShellWindow() }.Distinct())
        {
            if (window == IntPtr.Zero || SendMessageTimeout(window, 0, UIntPtr.Zero, IntPtr.Zero, 2, 500, out _) == IntPtr.Zero)
                throw new IOException("Windows 桌面未响应，已停止壁纸操作。");
        }
    }
    public static string GetImage()
    {
        var path = new System.Text.StringBuilder(32768);
        if (!GetSystemParameters(0x0073, (uint)path.Capacity, path, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return path.ToString();
    }

    public static bool IsInteractiveDesktop()
    {
        IntPtr input = OpenInputDesktop(0, false, 1);
        if (input == IntPtr.Zero) return false;
        try
        {
            var name = new System.Text.StringBuilder(256);
            return GetUserObjectInformation(input, 2, name, name.Capacity * 2, out _)
                && name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(input); }
    }

    public static WallpaperFit GetPosition()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        if (key?.GetValue("TileWallpaper")?.ToString() == "1") return WallpaperFit.Tile;
        return key?.GetValue("WallpaperStyle")?.ToString() switch
        { "2" => WallpaperFit.Stretch, "6" => WallpaperFit.Fit, "10" => WallpaperFit.Fill, "22" => WallpaperFit.Span, _ => WallpaperFit.Center };
    }

    public static void SetPosition(WallpaperFit mode)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop");
        string style = mode switch
        { WallpaperFit.Stretch => "2", WallpaperFit.Fit => "6", WallpaperFit.Fill => "10", WallpaperFit.Span => "22", _ => "0" };
        key.SetValue("WallpaperStyle", style);
        key.SetValue("TileWallpaper", mode == WallpaperFit.Tile ? "1" : "0");
    }

    public static uint GetBackgroundColor() => GetSysColor(1);
    public static void SetBackgroundColor(uint color)
    {
        if (!SetSysColors(1, [1], [color])) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Control Panel\Colors");
        key.SetValue("Background", $"{color & 255} {(color >> 8) & 255} {(color >> 16) & 255}");
    }

    public static void SetImage(string path)
    {
        if (!SystemParametersInfoW(0x0014, 0, path, 0x0001 | 0x0002))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Windows 无法更新桌面壁纸。");
    }

    public static void ClearImage()
    {
        // Persist an empty image path and broadcast the desktop setting change.
        if (!SystemParametersInfoW(0x0014, 0, "", 0x0001 | 0x0002))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法清除桌面壁纸图片。");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint parameter, string value, uint flags);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemParameters(uint action, uint parameter, System.Text.StringBuilder value, uint flags);
    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSysColors(int count, int[] elements, uint[] colors);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, System.Text.StringBuilder value, int length, out int needed);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(IntPtr desktopHandle);
}
