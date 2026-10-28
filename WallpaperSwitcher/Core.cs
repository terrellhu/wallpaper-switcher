using System.Text.Json;

namespace WallpaperSwitcher;

public enum SortMode { NameAscending, NameDescending, OldestFirst, NewestFirst, Random, Custom }
public enum WallpaperFit { Center = 0, Tile = 1, Stretch = 2, Fit = 3, Fill = 4, Span = 5 }

public sealed class AppSettings
{
    public string Folder { get; set; } = "";
    public int IntervalSeconds { get; set; } = 300;
    public SortMode Sort { get; set; }
    public List<string> CustomOrder { get; set; } = [];
    public uint HotkeyModifiers { get; set; } = 3; // Ctrl + Alt
    public uint HotkeyKey { get; set; } = 0x27; // Right
    public uint StopHotkeyModifiers { get; set; } = 3; // Ctrl + Alt
    public uint StopHotkeyKey { get; set; } = 0x23; // End
    public bool StartInTrayAndPlay { get; set; } = true;
    public WallpaperFit FitMode { get; set; } = WallpaperFit.Fill;
    public bool AutoCollage { get; set; } = true;
    // Zero selects the best count for the screen; 2–6 requests a fixed portrait group size.
    public int CollageCount { get; set; }
}

public sealed record LaunchOptions(bool StartInTray, bool AutoStart)
{
    public static LaunchOptions FromArgs(string[] args, AppSettings settings)
    {
        if (args.Contains("--settings", StringComparer.OrdinalIgnoreCase)) return new(false, false);
        bool background = args.Contains("--startup", StringComparer.OrdinalIgnoreCase) || settings.StartInTrayAndPlay;
        return new(background, background);
    }

    public static string StartupCommand(string executablePath)
    {
        if (!Path.IsPathFullyQualified(executablePath) || executablePath.IndexOfAny(['"', '\r', '\n']) >= 0)
            throw new ArgumentException("自启动程序必须使用有效的绝对路径。");
        string command = $"\"{executablePath}\" --startup";
        if (command.Length > 260) throw new ArgumentException("程序路径过长，无法添加到 Windows 自启动。");
        return command;
    }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    internal static string? TestDataDirectory { get; set; }
    public static string DataDirectory => TestDataDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimpleWallpaperSwitcher");
    public static AppSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(DataDirectory, "settings.json"))) ?? new();
            settings.IntervalSeconds = Math.Clamp(settings.IntervalSeconds, 1, 86400);
            if (!Enum.IsDefined(settings.Sort)) settings.Sort = SortMode.NameAscending;
            if (!Enum.IsDefined(settings.FitMode)) settings.FitMode = WallpaperFit.Fill;
            settings.CollageCount = CollagePlanner.NormalizeCount(settings.CollageCount);
            settings.Folder ??= "";
            settings.CustomOrder ??= [];
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public static void Save(AppSettings settings) => WriteAtomic(Path.Combine(DataDirectory, "settings.json"), settings);
    public static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, PrettyJson));
        File.Move(path + ".tmp", path, true);
    }
}

public sealed record PictureFile(string Path, DateTime Modified)
{
    public override string ToString() => Path;
}

public static class PictureCatalog
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };
    public static List<PictureFile> Scan(string folder, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("图片目录不存在或无法访问。");
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false
        };
        var files = new List<PictureFile>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", options))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Extensions.Contains(System.IO.Path.GetExtension(path))) continue;
            try { files.Add(new(path, File.GetLastWriteTimeUtc(path))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return files;
    }

    public static List<PictureFile> Order(IEnumerable<PictureFile> files, SortMode mode, IReadOnlyList<string> custom)
    {
        var byName = files.OrderBy(f => f.Path, NaturalPathComparer.Instance).ToList();
        switch (mode)
        {
            case SortMode.NameDescending: byName.Reverse(); return byName;
            case SortMode.OldestFirst: return byName.OrderBy(f => f.Modified).ToList();
            case SortMode.NewestFirst: return byName.OrderByDescending(f => f.Modified).ToList();
            case SortMode.Random: Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(byName)); return byName;
            case SortMode.Custom:
                var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < custom.Count; i++) rank.TryAdd(custom[i], i);
                return byName.OrderBy(f => rank.GetValueOrDefault(f.Path, int.MaxValue)).ToList();
            default: return byName;
        }
    }
}

// Numeric path components sort naturally: image2 precedes image10, including huge numbers.
public sealed class NaturalPathComparer : IComparer<string>
{
    public static readonly NaturalPathComparer Instance = new();
    public int Compare(string? x, string? y)
    {
        if (x is null || y is null) return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        int a = 0, b = 0;
        while (a < x.Length && b < y.Length)
        {
            if (char.IsAsciiDigit(x[a]) && char.IsAsciiDigit(y[b]))
            {
                int ae = a, be = b;
                while (ae < x.Length && char.IsAsciiDigit(x[ae])) ae++;
                while (be < y.Length && char.IsAsciiDigit(y[be])) be++;
                int az = a, bz = b;
                while (az < ae && x[az] == '0') az++;
                while (bz < be && y[bz] == '0') bz++;
                int result = (ae - az).CompareTo(be - bz);
                if (result == 0) result = x.AsSpan(az, ae - az).SequenceCompareTo(y.AsSpan(bz, be - bz));
                if (result != 0) return result;
                result = (ae - a).CompareTo(be - b);
                if (result != 0) return result;
                a = ae; b = be;
            }
            else
            {
                int result = char.ToUpperInvariant(x[a++]).CompareTo(char.ToUpperInvariant(y[b++]));
                if (result != 0) return result;
            }
        }
        return (x.Length - a).CompareTo(y.Length - b);
    }
}

public interface IWallpaperService
{
    void Capture();
    void Apply(string path);
    void Restore();
    IReadOnlyList<string> PlanFrame(IReadOnlyList<string> candidates) => [candidates[0]];
    void ApplyFrame(IReadOnlyList<string> paths) => Apply(paths[0]);
}

// Deliberately not an IOException: uncertain native operations must never enter the bad-image retry loop.
public sealed class WallpaperOperationException(string message) : Exception(message);
public sealed class SessionInterruptedException() : Exception("会话暂时不可用，等待解锁或唤醒。");

[Flags]
public enum SessionBlocker { None = 0, Locked = 1, Disconnected = 2, Sleeping = 4 }

public sealed class SessionPlaybackState
{
    public bool WantsPlayback { get; set; }
    public bool Pending { get; private set; }
    public SessionBlocker Blockers { get; private set; }
    public int Revision { get; private set; }
    public bool IsUnavailable => Blockers != SessionBlocker.None;
    public bool CanResume => Pending && !IsUnavailable;
    public void Suspend(SessionBlocker reason) { Blockers |= reason; Pending = true; Revision++; }
    public void Wake(SessionBlocker reason) { Blockers &= ~reason; }
    public void Complete() => Pending = false;
    public void Cancel() { Pending = false; WantsPlayback = false; }
}

public static class CollagePlanner
{
    public const int MaxPictures = 6;
    public static int NormalizeCount(int count) => count is >= 2 and <= MaxPictures ? count : 0;
    public static int ChooseCount(IReadOnlyList<double> aspectRatios, double screenAspect, int requestedCount = 0)
    {
        if (aspectRatios.Count == 0) return 0;
        if (screenAspect <= 0 || !double.IsFinite(screenAspect)) throw new ArgumentOutOfRangeException(nameof(screenAspect));
        requestedCount = NormalizeCount(requestedCount);
        if (requestedCount > 0)
        {
            int available = aspectRatios.Take(requestedCount).TakeWhile(a => double.IsFinite(a) && a > 0 && a < 1).Count();
            return Math.Max(1, available);
        }
        double sum = 0, bestScore = double.MaxValue;
        int best = 1;
        for (int i = 0; i < Math.Min(MaxPictures, aspectRatios.Count); i++)
        {
            double aspect = aspectRatios[i];
            if (aspect <= 0 || aspect >= 1 || !double.IsFinite(aspect)) break;
            sum += aspect;
            // Closest total aspect ratio needs the least proportional cropping.
            double score = Math.Abs(Math.Log(sum / screenAspect));
            if (score < bestScore) { best = i + 1; bestScore = score; }
        }
        return best;
    }
}

public sealed record MonitorSnapshot(string MonitorId, string OriginalPath, string BackupPath);
public sealed record DesktopSnapshot(int Position, uint Background, List<MonitorSnapshot> Monitors, bool? WallpaperEnabled = null)
{
    // Older snapshots had no enabled flag. Empty image paths still mean a solid background.
    public bool RestoreSolidColor => WallpaperEnabled == false || Monitors.All(m => string.IsNullOrEmpty(m.BackupPath));
}

public sealed class RotationController(IWallpaperService wallpaper)
{
    private List<PictureFile> playlist = [];
    private int index = -1;
    private int frameStart = -1;
    private bool random;
    public bool IsRunning { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsFaulted { get; private set; }
    public void HaltForSafety() { IsRunning = false; IsPaused = false; IsFaulted = true; }
    public string? CurrentPath { get; private set; }
    public IReadOnlyList<string> CurrentPaths { get; private set; } = [];
    public int Position => frameStart + 1;
    public int Count => playlist.Count;
    public void HoldForSession() { IsRunning = false; }
    public void ResumeAfterSession(bool play, bool hasPendingRestore)
    {
        if (IsFaulted) throw new InvalidOperationException("壁纸操作失败后需要手动恢复。");
        if (!play)
        {
            if (hasPendingRestore) wallpaper.Restore();
            IsRunning = false; IsPaused = playlist.Count > 0;
            return;
        }
        if (playlist.Count == 0) throw new InvalidOperationException("没有可继续的播放列表。");
        try
        {
            if (!hasPendingRestore) wallpaper.Capture();
            IsRunning = true; IsPaused = false;
            if (CurrentPaths.Count == 0) Next();
            else wallpaper.ApplyFrame(CurrentPaths);
        }
        catch (WallpaperOperationException) { HaltForSafety(); throw; }
    }

    public void Start(List<PictureFile> pictures, bool shuffle)
    {
        if (IsRunning || IsPaused || IsFaulted) throw new InvalidOperationException("请先结束轮播或恢复原背景。");
        if (pictures.Count == 0) throw new InvalidOperationException("目录中没有支持的图片。");
        playlist = [.. pictures]; index = frameStart = -1; random = shuffle;
        try { wallpaper.Capture(); } // Durable snapshot must exist before any desktop changes.
        catch (WallpaperOperationException) { HaltForSafety(); throw; }
        IsRunning = true;
        try { Next(); }
        catch (SessionInterruptedException) { throw; }
        catch (WallpaperOperationException) { HaltForSafety(); throw; }
        catch { Stop(); throw; }
    }

    public void Next()
    {
        if (!IsRunning) return;
        int previousIndex = index;
        Exception? lastError = null;
        for (int attempts = 0; attempts < playlist.Count; attempts++)
        {
            if (++index >= playlist.Count)
            {
                index = 0;
                if (random && playlist.Count > 1)
                {
                    Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(playlist));
                    if (playlist[0].Path == CurrentPath) (playlist[0], playlist[1]) = (playlist[1], playlist[0]);
                }
            }
            try
            {
                var candidates = playlist.Skip(index).Take(CollagePlanner.MaxPictures).Select(p => p.Path).ToArray();
                var frame = wallpaper.PlanFrame(candidates).ToArray();
                if (frame.Length == 0 || frame.Length > candidates.Length || !frame.SequenceEqual(candidates.Take(frame.Length)))
                    throw new InvalidOperationException("拼贴分组必须遵循播放顺序。");
                wallpaper.ApplyFrame(frame);
                CurrentPaths = frame;
                CurrentPath = frame[0];
                frameStart = index;
                index += frame.Length - 1;
                return;
            }
            catch (SessionInterruptedException) { index = previousIndex; throw; }
            catch (WallpaperOperationException) { HaltForSafety(); throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
            { lastError = ex; }
        }
        throw new IOException("所有图片均无法读取或设置。请检查目录和图片格式。", lastError);
    }

    public void Pause()
    {
        if (!IsRunning) return;
        try { wallpaper.Restore(); }
        catch (WallpaperOperationException) { HaltForSafety(); throw; }
        IsRunning = false;
        IsPaused = true;
    }

    public void Resume()
    {
        if (!IsPaused) return;
        // The user may have changed the default wallpaper while paused.
        try { wallpaper.Capture(); }
        catch (WallpaperOperationException) { HaltForSafety(); throw; }
        IsRunning = true;
        IsPaused = false;
        try
        {
            try { wallpaper.ApplyFrame(CurrentPaths); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
            { Next(); }
        }
        catch (WallpaperOperationException) { HaltForSafety(); throw; }
        catch (SessionInterruptedException) { throw; }
        catch { Pause(); throw; }
    }

    public void Stop()
    {
        if (IsRunning) wallpaper.Restore(); // Retain state on failure so restoration can be retried.
        IsRunning = false;
        IsPaused = false;
        IsFaulted = false;
        CurrentPath = null;
        CurrentPaths = [];
        playlist.Clear();
        index = frameStart = -1;
    }
}
