using WallpaperSwitcher;

int passed = 0;
void Test(string name, Action test)
{
    test(); Console.WriteLine("PASS " + name); passed++;
}
void Check(bool condition, string message = "Assertion failed")
{
    if (!condition) throw new Exception(message);
}
List<PictureFile> Files(params string[] names) => names.Select((n, i) => new PictureFile(n, DateTime.UnixEpoch.AddDays(i))).ToList();
void Throws(Action action)
{
    try { action(); } catch (IOException) { return; }
    throw new Exception("Expected IOException");
}

Test("Natural ordering, case, leading zeros, arbitrarily large numbers", () =>
{
    var result = PictureCatalog.Order(Files("10.jpg", "2.jpg", "01.jpg", "1.jpg", "999999999999999999999.jpg"), SortMode.NameAscending, []);
    Check(string.Join(",", result.Select(f => f.Path)) == "1.jpg,01.jpg,2.jpg,10.jpg,999999999999999999999.jpg");
    Check(NaturalPathComparer.Instance.Compare("A.JPG", "a.jpg") == 0);
});
Test("Date, descending and custom order including newly discovered files", () =>
{
    var files = Files("1.jpg", "2.jpg", "3.jpg");
    Check(PictureCatalog.Order(files, SortMode.NewestFirst, [])[0].Path == "3.jpg");
    Check(PictureCatalog.Order(files, SortMode.OldestFirst, [])[0].Path == "1.jpg");
    Check(PictureCatalog.Order(files, SortMode.NameDescending, [])[0].Path == "3.jpg");
    var custom = PictureCatalog.Order(files, SortMode.Custom, ["2.JPG", "2.jpg", "1.jpg", "missing.jpg"]);
    Check(string.Join(",", custom.Select(f => f.Path)) == "2.jpg,1.jpg,3.jpg");
});
Test("Recursive scan includes hidden folders and filters unsupported files", () =>
{
    string root = Path.Combine(Path.GetTempPath(), "wallpaper-test-" + Guid.NewGuid());
    try
    {
        Directory.CreateDirectory(Path.Combine(root, "nested", "deep"));
        Directory.CreateDirectory(Path.Combine(root, "hidden"));
        File.SetAttributes(Path.Combine(root, "hidden"), FileAttributes.Hidden);
        File.WriteAllText(Path.Combine(root, "1.JPG"), "");
        File.WriteAllText(Path.Combine(root, "nested", "deep", "2.png"), "");
        File.WriteAllText(Path.Combine(root, "hidden", "3.bmp"), "");
        File.WriteAllText(Path.Combine(root, "ignore.txt"), "");
        Check(PictureCatalog.Scan(root).Count == 3);
    }
    finally { Directory.Delete(root, true); }
});
Test("Capture precedes first mutation; next wraps; stop restores exactly once", () =>
{
    var service = new FakeWallpaper();
    var rotation = new RotationController(service);
    rotation.Start(Files("a", "b"), false); rotation.Next(); rotation.Next(); rotation.Stop(); rotation.Stop();
    Check(string.Join(",", service.Events) == "capture,apply:a,apply:b,apply:a,restore");
    Check(!rotation.IsRunning);
});
Test("Bad images are skipped and complete failure restores the original", () =>
{
    var service = new FakeWallpaper { FailPaths = ["bad"] };
    var rotation = new RotationController(service);
    rotation.Start(Files("bad", "ok"), false); Check(rotation.CurrentPath == "ok"); rotation.Stop();
    service = new FakeWallpaper { FailPaths = ["bad"] };
    rotation = new RotationController(service);
    Throws(() => rotation.Start(Files("bad"), false));
    Check(!rotation.IsRunning && service.Events[^1] == "restore");
});
Test("Backup failure prevents mutation; restoration failure remains retryable", () =>
{
    var service = new FakeWallpaper { FailCapture = true };
    var rotation = new RotationController(service);
    Throws(() => rotation.Start(Files("a"), false));
    Check(!rotation.IsRunning && service.Events.Count == 1);
    service.FailCapture = false; rotation.Start(Files("a"), false); service.FailRestore = true;
    Throws(rotation.Stop); Check(rotation.IsRunning);
    service.FailRestore = false; rotation.Stop(); Check(!rotation.IsRunning);
});
Test("Random playback covers each image and avoids repeats at cycle boundaries", () =>
{
    var service = new FakeWallpaper();
    var rotation = new RotationController(service);
    rotation.Start(Files("a", "b", "c"), true);
    string? previous = null;
    for (int cycle = 0; cycle < 100; cycle++)
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < 3; i++)
        {
            Check(rotation.CurrentPath != previous);
            seen.Add(rotation.CurrentPath!); previous = rotation.CurrentPath;
            rotation.Next();
        }
        Check(seen.Count == 3);
    }
    rotation.Stop();
});
Test("Pause restores default, ignores next, and resumes the same image and order", () =>
{
    var service = new FakeWallpaper();
    var rotation = new RotationController(service);
    rotation.Start(Files("a", "b", "c"), false);
    rotation.Next();
    rotation.Pause();
    Check(!rotation.IsRunning && rotation.IsPaused && rotation.CurrentPath == "b");
    rotation.Next();
    Check(service.Events[^1] == "restore" && rotation.Position == 2);
    rotation.Resume();
    Check(rotation.IsRunning && !rotation.IsPaused && rotation.CurrentPath == "b");
    Check(string.Join(",", service.Events.TakeLast(2)) == "capture,apply:b");
    rotation.Next();
    Check(rotation.CurrentPath == "c");
    rotation.Pause();
    int beforeStop = service.Events.Count;
    rotation.Stop();
    Check(!rotation.IsPaused && !rotation.IsRunning && service.Events.Count == beforeStop);
});
Test("Repeated toggle preserves shuffled order and current position", () =>
{
    var service = new FakeWallpaper();
    var rotation = new RotationController(service);
    rotation.Start(Files("a", "b", "c"), true);
    rotation.Next();
    for (int i = 0; i < 20; i++)
    {
        rotation.Pause(); rotation.Pause();
        rotation.Resume(); rotation.Resume();
        Check(rotation.CurrentPath == "b" && rotation.Position == 2);
    }
    rotation.Next();
    Check(rotation.CurrentPath == "c");
    rotation.Stop();
});
Test("Pause and resume failures retain retryable state", () =>
{
    var service = new FakeWallpaper();
    var rotation = new RotationController(service);
    rotation.Start(Files("a", "b"), false);
    service.FailRestore = true;
    Throws(rotation.Pause);
    Check(rotation.IsRunning && !rotation.IsPaused);
    service.FailRestore = false; rotation.Pause();
    service.FailCapture = true;
    Throws(rotation.Resume);
    Check(rotation.IsPaused && !rotation.IsRunning);
    service.FailCapture = false;
    service.FailPaths = ["a", "b"];
    Throws(rotation.Resume);
    Check(rotation.IsPaused && !rotation.IsRunning && service.Events[^1] == "restore");
    service.FailPaths.Clear(); rotation.Resume(); rotation.Stop();
});
Test("Resuming after the current image is deleted skips to the next usable picture", () =>
{
    var service = new FakeWallpaper();
    var rotation = new RotationController(service);
    rotation.Start(Files("a", "b"), false); rotation.Pause();
    service.FailPaths = ["a"];
    rotation.Resume();
    Check(rotation.CurrentPath == "b" && rotation.IsRunning);
    rotation.Stop();
});
Test("Existing settings migrate to tray autoplay without changing saved directory and hotkeys", () =>
{
    var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
        """{"Folder":"D:\\Pictures","IntervalSeconds":60,"HotkeyModifiers":6,"StopHotkeyKey":37}""")!;
    var options = LaunchOptions.FromArgs([], settings);
    Check(options.StartInTray && options.AutoStart);
    Check(settings.Folder == @"D:\Pictures" && settings.IntervalSeconds == 60
        && settings.HotkeyModifiers == 6 && settings.StopHotkeyKey == 37);
});
Test("Logon enforces hidden autoplay, settings launch is visible, quoted path preserves spaces", () =>
{
    var settings = new AppSettings { StartInTrayAndPlay = false };
    Check(LaunchOptions.FromArgs([], settings) == new LaunchOptions(false, false));
    Check(LaunchOptions.FromArgs(["--startup"], settings) == new LaunchOptions(true, true));
    settings.StartInTrayAndPlay = true;
    Check(LaunchOptions.FromArgs(["--settings"], settings) == new LaunchOptions(false, false));
    Check(LaunchOptions.StartupCommand(@"C:\My Apps\壁纸轮播.exe") == "\"C:\\My Apps\\壁纸轮播.exe\" --startup");
    bool rejected = false;
    try { LaunchOptions.StartupCommand("relative.exe"); } catch (ArgumentException) { rejected = true; }
    Check(rejected);
});
Test("Solid-color restoration preserves disabled image state even with cached image paths", () =>
{
    var disabled = new DesktopSnapshot(4, 0, [new("screen", "cached.jpg", "backup.jpg")], false);
    Check(disabled.RestoreSolidColor);
    var image = disabled with { WallpaperEnabled = true };
    Check(!image.RestoreSolidColor);
    var oldSnapshot = System.Text.Json.JsonSerializer.Deserialize<DesktopSnapshot>(
        """{"Position":4,"Background":0,"Monitors":[{"MonitorId":"screen","OriginalPath":"","BackupPath":""}]}""")!;
    Check(oldSnapshot.RestoreSolidColor && oldSnapshot.WallpaperEnabled is null);
    Check(!(oldSnapshot with { Monitors = [new("screen", "a.jpg", "backup.jpg")] }).RestoreSolidColor);
});
Test("Portrait grouping chooses minimal cropping, respects landscapes, and caps wide-screen groups", () =>
{
    Check(CollagePlanner.ChooseCount([2.0/3, 2.0/3, 2.0/3, 2.0/3], 16.0/9) == 3);
    Check(CollagePlanner.ChooseCount([2.0/3, 2.0/3, 2.0/3], 16.0/10) == 2);
    Check(CollagePlanner.ChooseCount([.6, 1.5, .6], 16.0/9) == 1);
    Check(CollagePlanner.ChooseCount([1.5, .6, .6], 16.0/9) == 1);
    Check(CollagePlanner.ChooseCount([.6, .6, .6], 9.0/16) == 1);
    Check(CollagePlanner.ChooseCount(Enumerable.Repeat(.5, 20).ToArray(), 5) == 6);
    Check(CollagePlanner.ChooseCount([], 16.0/9) == 0);
});
Test("Collage consumes each source once, resumes the same frame, and handles end of cycle", () =>
{
    var service = new FakeWallpaper { FrameLength = 3 };
    var rotation = new RotationController(service);
    rotation.Start(Files("a", "b", "c", "d", "e"), false);
    Check(rotation.CurrentPaths.SequenceEqual(["a", "b", "c"]) && rotation.Position == 1);
    rotation.Pause(); rotation.Resume();
    Check(rotation.CurrentPaths.SequenceEqual(["a", "b", "c"]) && service.Events[^1] == "frame:a+b+c");
    rotation.Next();
    Check(rotation.CurrentPaths.SequenceEqual(["d", "e"]) && rotation.Position == 4);
    rotation.Next();
    Check(rotation.CurrentPaths.SequenceEqual(["a", "b", "c"]));
    rotation.Stop();
});
Test("A failed collage does not consume unseen following pictures", () =>
{
    var service = new FakeWallpaper { FrameLength = 2, FailPaths = ["bad"] };
    var rotation = new RotationController(service);
    rotation.Start(Files("bad", "b", "c", "d"), false);
    Check(rotation.CurrentPaths.SequenceEqual(["b", "c"]));
    rotation.Next();
    Check(rotation.CurrentPaths.SequenceEqual(["d"]));
    rotation.Stop();
});
Test("Random collage cycles contain every source exactly once", () =>
{
    var service = new FakeWallpaper { FrameLength = 2 };
    var rotation = new RotationController(service);
    rotation.Start(Files("a", "b", "c", "d", "e"), true);
    for (int cycle = 0; cycle < 20; cycle++)
    {
        var seen = new List<string>();
        for (int frame = 0; frame < 3; frame++)
        {
            seen.AddRange(rotation.CurrentPaths);
            rotation.Next();
        }
        Check(seen.Count == 5 && seen.Distinct().Count() == 5);
    }
    rotation.Stop();
});
Test("Fixed portrait counts respect choice, boundaries, short tails and old settings", () =>
{
    Check(CollagePlanner.ChooseCount([.6, .6, .6, .6], 16.0/9, 2) == 2);
    Check(CollagePlanner.ChooseCount([.6, .6, .6, .6], 16.0/10, 3) == 3);
    Check(CollagePlanner.ChooseCount([.6, .6, .6], 9.0/16, 3) == 3);
    Check(CollagePlanner.ChooseCount([.6, .6], 16.0/9, 3) == 2);
    Check(CollagePlanner.ChooseCount([.6, 1.5, .6], 16.0/9, 3) == 1);
    Check(CollagePlanner.ChooseCount([.6], 16.0/9, 6) == 1);
    Check(CollagePlanner.ChooseCount([], 16.0/9, 3) == 0);
    Check(CollagePlanner.NormalizeCount(1) == 0 && CollagePlanner.NormalizeCount(7) == 0);
    var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{"AutoCollage":false}""")!;
    Check(old.CollageCount == 0 && !old.AutoCollage);
    var saved = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(
        new AppSettings { CollageCount = 3 }))!;
    Check(saved.CollageCount == 3 && saved.AutoCollage);
});
Test("Uncertain native application halts immediately without retrying or auto-restoring", () =>
{
    var service = new FakeWallpaper { FatalApply = true };
    var rotation = new RotationController(service);
    try { rotation.Start(Files("a", "b", "c"), false); throw new Exception("Expected safe stop"); }
    catch (WallpaperOperationException) { }
    Check(rotation.IsFaulted && !rotation.IsRunning && !rotation.IsPaused);
    Check(string.Join(",", service.Events) == "capture,apply:a");
    rotation.Next(); rotation.Resume();
    Check(service.Events.Count == 2);
    rotation.Stop(); Check(!rotation.IsFaulted && service.Events.Count == 2);
});
Test("Failed restoration never reports paused and does not retry automatically", () =>
{
    var service = new FakeWallpaper();
    var rotation = new RotationController(service);
    rotation.Start(Files("a", "b"), false); service.FatalRestore = true;
    try { rotation.Pause(); throw new Exception("Expected safe stop"); }
    catch (WallpaperOperationException) { }
    Check(rotation.IsFaulted && !rotation.IsPaused && !rotation.IsRunning);
    int calls = service.Events.Count;
    rotation.Pause(); rotation.Next(); rotation.Stop();
    Check(service.Events.Count == calls);
});
Test("Manual pause survives lock/sleep and requires explicit resume", () =>
{
    var service = new FakeWallpaper { FrameLength = 2 };
    var rotation = new RotationController(service);
    rotation.Start(Files("a", "b", "c", "d"), false); rotation.Next(); rotation.Pause();
    var frame = rotation.CurrentPaths.ToArray(); int calls = service.Events.Count;
    for (int i = 0; i < 3; i++) { rotation.HoldForSession(); rotation.ResumeAfterSession(false, false); }
    Check(rotation.IsPaused && !rotation.IsFaulted && service.Events.Count == calls);
    rotation.Resume();
    Check(rotation.IsRunning && rotation.CurrentPaths.SequenceEqual(frame));
    Check(string.Join(",", service.Events.Skip(calls)) == "capture,frame:c+d"); rotation.Stop();
});
Test("Active session resumes the same collage without recapturing original background", () =>
{
    var service = new FakeWallpaper { FrameLength = 2 };
    var rotation = new RotationController(service);
    rotation.Start(Files("a", "b", "c", "d"), false); int calls = service.Events.Count;
    rotation.HoldForSession(); rotation.Next();
    Check(service.Events.Count == calls);
    rotation.ResumeAfterSession(true, true);
    Check(rotation.IsRunning && string.Join(",", service.Events.Skip(calls)) == "frame:a+b");
    rotation.Next(); Check(rotation.CurrentPaths.SequenceEqual(["c", "d"])); rotation.Stop();
});
Test("Unlock before wake and duplicate notifications do not lose playback intent", () =>
{
    var state = new SessionPlaybackState { WantsPlayback = true };
    state.Suspend(SessionBlocker.Locked); state.Suspend(SessionBlocker.Sleeping);
    state.Wake(SessionBlocker.Locked); Check(!state.CanResume && state.WantsPlayback);
    state.Wake(SessionBlocker.Sleeping); Check(state.CanResume);
    state.Complete(); state.Wake(SessionBlocker.Sleeping); Check(!state.CanResume);
    state.Suspend(SessionBlocker.Sleeping); state.Suspend(SessionBlocker.Locked);
    state.Wake(SessionBlocker.Sleeping); Check(!state.CanResume);
    state.Wake(SessionBlocker.Locked); Check(state.CanResume);
    state.Cancel(); state.Wake(SessionBlocker.Locked); Check(!state.CanResume && !state.WantsPlayback);
});
Test("Interrupted advance does not skip an image on session recovery", () =>
{
    var service = new FakeWallpaper(); var rotation = new RotationController(service);
    rotation.Start(Files("a", "b", "c"), false); service.SessionApply = true;
    try { rotation.Next(); throw new Exception("Expected interruption"); } catch (SessionInterruptedException) { }
    service.SessionApply = false; rotation.HoldForSession(); rotation.ResumeAfterSession(true, true);
    Check(rotation.CurrentPath == "a"); rotation.Next(); Check(rotation.CurrentPath == "b"); rotation.Stop();
});
Test("Interrupted manual pause restores black on wake and never starts playback", () =>
{
    var service = new FakeWallpaper(); var rotation = new RotationController(service);
    rotation.Start(Files("a", "b"), false); service.SessionRestore = true;
    try { rotation.Pause(); throw new Exception("Expected interruption"); } catch (SessionInterruptedException) { }
    service.SessionRestore = false; rotation.HoldForSession(); int calls = service.Events.Count;
    rotation.ResumeAfterSession(false, true);
    Check(rotation.IsPaused && !rotation.IsRunning && string.Join(",", service.Events.Skip(calls)) == "restore");
    rotation.Stop();
});
Test("Real application failures cannot be retried by session recovery", () =>
{
    var service = new FakeWallpaper(); var rotation = new RotationController(service);
    rotation.Start(Files("a", "b"), false); rotation.HoldForSession(); service.FatalApply = true;
    try { rotation.ResumeAfterSession(true, true); throw new Exception("Expected fault"); } catch (WallpaperOperationException) { }
    int calls = service.Events.Count;
    try { rotation.ResumeAfterSession(true, true); throw new Exception("Expected block"); } catch (InvalidOperationException) { }
    Check(rotation.IsFaulted && service.Events.Count == calls);
});
Console.WriteLine($"All {passed} checks passed.");

sealed class FakeWallpaper : IWallpaperService
{
    public List<string> Events = [];
    public HashSet<string> FailPaths = [];
    public bool FailCapture, FailRestore;
    public bool FatalApply, FatalRestore, SessionApply, SessionRestore;
    public int FrameLength = 1;
    public IReadOnlyList<string> PlanFrame(IReadOnlyList<string> candidates) => candidates.Take(FrameLength).ToArray();
    public void ApplyFrame(IReadOnlyList<string> paths)
    {
        if (paths.Count == 1) { Apply(paths[0]); return; }
        Events.Add("frame:" + string.Join("+", paths));
        if (paths.Any(FailPaths.Contains)) throw new IOException("corrupt frame");
    }
    public void Capture() { Events.Add("capture"); if (FailCapture) throw new IOException("backup failed"); }
    public void Apply(string path) { Events.Add("apply:" + path); if (SessionApply) throw new SessionInterruptedException(); if (FatalApply) throw new WallpaperOperationException("timed out"); if (FailPaths.Contains(path)) throw new IOException("corrupt"); }
    public void Restore() { Events.Add("restore"); if (SessionRestore) throw new SessionInterruptedException(); if (FatalRestore) throw new WallpaperOperationException("timed out"); if (FailRestore) throw new IOException("restore failed"); }
}
