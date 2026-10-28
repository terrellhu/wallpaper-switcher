using System.Diagnostics;
using WallpaperSwitcher;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args[0] == "--parent-probe")
        { using var parent = new SafeWallpaperService(args[1], args[2], 30000); parent.RunTest("test-hang"); return 0; }
        string exe = Path.GetFullPath(args[0]);
        string root = Path.Combine(Path.GetTempPath(), "wallpaper-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string Fixture(string name)
        {
            string data = Path.Combine(root, name); Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "allow-worker-tests"), "test");
            File.WriteAllText(Path.Combine(data, "restore.json"), "original-record-must-survive");
            return data;
        }
        string timed = Fixture("timeout");
        using (var service = new SafeWallpaperService(exe, timed, 1200))
        {
            var watch = Stopwatch.StartNew();
            ExpectFault(() => service.RunTest("test-hang"));
            Check(watch.Elapsed.TotalSeconds < 5, "Timeout did not bound the wait");
            Check(Exited(service.LastWorkerProcessId), "Timed-out worker still alive");
            Check(service.IsSafetyStopped, "Missing safety stop");
            Check(File.ReadAllText(Path.Combine(timed, "restore.json")) == "original-record-must-survive", "Backup record changed");
            int pid = service.LastWorkerProcessId;
            ExpectFault(() => service.RunTest("test-delay"));
            Check(service.LastWorkerProcessId == pid, "Fault caused an automatic retry");
        }
        using (var restarted = new SafeWallpaperService(exe, timed))
        { ExpectFault(() => restarted.RunTest("test-delay")); Check(restarted.LastWorkerProcessId == 0, "Restart bypassed safety stop"); }
        Console.WriteLine("PASS bounded timeout, worker killed, restore retained, no retry across restart");

        string crash = Fixture("crash");
        using (var service = new SafeWallpaperService(exe, crash))
        { ExpectFault(() => service.RunTest("test-crash")); Check(service.IsSafetyStopped && service.HasPendingRestore, "Crash lost recovery state"); }
        Console.WriteLine("PASS worker crash stops safely");

        string concurrent = Fixture("concurrent");
        using (var service = new SafeWallpaperService(exe, concurrent))
        {
            var first = Task.Run(() => service.RunTest("test-delay"));
            await Started(concurrent);
            int rejected = 0;
            await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Task.Run(() =>
            {
                try { service.RunTest("test-delay"); }
                catch (InvalidOperationException) { Interlocked.Increment(ref rejected); }
            })));
            await first;
            Check(rejected == 30 && !service.IsSafetyStopped, "Concurrent operations were queued or started");
        }
        Console.WriteLine("PASS 30 overlapping requests rejected; no queued toggles");

        string cancelled = Fixture("cancel");
        using (var service = new SafeWallpaperService(exe, cancelled))
        {
            var task = Task.Run(() => ExpectFault(() => service.RunTest("test-hang")));
            await Started(cancelled);
            service.StopForSafety("simulated session lock / exit");
            await task.WaitAsync(TimeSpan.FromSeconds(4));
            Check(Exited(service.LastWorkerProcessId) && service.HasPendingRestore, "Cancellation left an orphan or lost restore data");
        }
        Console.WriteLine("PASS session/exit cancellation stops the worker and retains restoration");

        string sessionData = Fixture("session-cancel");
        using (var service = new SafeWallpaperService(exe, sessionData))
        {
            var task = Task.Run(() =>
            {
                try { service.RunTest("test-hang"); throw new Exception("Expected session cancellation"); }
                catch (SessionInterruptedException) { }
            });
            await Started(sessionData); service.SuspendSession(); await task.WaitAsync(TimeSpan.FromSeconds(4));
            Check(Exited(service.LastWorkerProcessId) && !service.IsSafetyStopped && service.HasPendingRestore,
                "Session cancellation became a permanent fault or lost the snapshot");
            service.EndSessionHold(); service.RunTest("test-delay");
            service.StopForSafety("real fault");
            try { service.EndSessionHold(); throw new Exception("Fault was automatically cleared"); }
            catch (WallpaperOperationException) { }
        }
        Console.WriteLine("PASS recoverable session cancellation kills worker; true faults remain blocked");

        string orphan = Fixture("parent-exit");
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "WallpaperSwitcher.SafetyChecks.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string arg in new[] { "--parent-probe", exe, orphan }) info.ArgumentList.Add(arg);
        using (var parent = Process.Start(info)!)
        {
            await Started(orphan);
            int child = int.Parse(File.ReadAllText(Path.Combine(orphan, "test-worker-started")));
            parent.Kill(); await parent.WaitForExitAsync();
            for (int i = 0; i < 40 && !Exited(child); i++) await Task.Delay(50);
            Check(Exited(child), "Job object did not terminate child after parent death");
        }
        Console.WriteLine("PASS parent termination kills the worker through its Windows job");

        if (args.Contains("--desktop")) DesktopChecks(exe, root);
        Console.WriteLine("Safety checks passed. Artifacts: " + root);
        return 0;
    }
    private static void DesktopChecks(string exe, string root)
    {
        WallpaperNative.AssertDesktopAvailable();
        Check(Screen.AllScreens.Length == 1, "Desktop fixture requires one screen");
        Check(WallpaperNative.GetBackgroundType() == 1 && WallpaperNative.GetBackgroundColor() == 0, "Expected user's pure black baseline");
        var paths = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            string path = Path.Combine(root, $"portrait-{i}.jpg");
            using var bitmap = new Bitmap(400, 600);
            using (var g = Graphics.FromImage(bitmap)) g.Clear(i == 0 ? Color.Coral : i == 1 ? Color.SeaGreen : Color.SteelBlue);
            WallpaperEncoding.SaveJpeg(bitmap, path); paths.Add(path);
        }
        string data = Path.Combine(root, "desktop");
        using var service = new SafeWallpaperService(exe, data) { CollageCount = 3 };
        var rotation = new RotationController(service);
        try
        {
            rotation.Start(paths.Select(p => new PictureFile(p, DateTime.UtcNow)).ToList(), false);
            string firstImage = WallpaperNative.GetImage();
            Check(firstImage.EndsWith(".jpg") && File.Exists(firstImage), "JPG was not applied");
            DateTime stamp = File.GetLastWriteTimeUtc(firstImage);
            DateTime metadataStamp = File.GetLastWriteTimeUtc(Path.Combine(data, "frame-cache.json"));
            for (int i = 0; i < 3; i++)
            {
                rotation.Pause(); AssertBlack();
                Check(!service.HasPendingRestore && rotation.IsPaused, "Pause was not committed");
                rotation.HoldForSession(); rotation.ResumeAfterSession(false, service.HasPendingRestore);
                Check(rotation.IsPaused, "A session notification discarded a confirmed pause");
                rotation.Resume();
                Check(WallpaperNative.GetImage() == firstImage && File.GetLastWriteTimeUtc(firstImage) == stamp,
                    "Fresh helper failed to reuse JPG cache");
                Check(File.GetLastWriteTimeUtc(Path.Combine(data, "frame-cache.json")) == metadataStamp,
                    "Unchanged cache metadata was rewritten");
            }
            foreach (var mode in Enum.GetValues<WallpaperFit>())
            {
                service.SetFitMode(mode); service.Apply(paths[0]);
                Check(WallpaperNative.GetPosition() == mode, "Incorrect fit mode " + mode);
            }
            rotation.Stop(); AssertBlack();
            Console.WriteLine("PASS real JPG collage, repeated pause/resume, six fit modes and committed pure black restoration");
            service.Capture(); service.Apply(paths[1]);
            string hash = WallpaperWorker.SnapshotHash(data)!;
            using var fresh = new SafeWallpaperService(exe, data);
            ExpectFault(fresh.Capture);
            Check(WallpaperWorker.SnapshotHash(data) == hash, "Pending original snapshot was overwritten");
            fresh.Recover(); AssertBlack();
            Check(!fresh.HasPendingRestore && !fresh.IsSafetyStopped, "Explicit recovery was not committed");
            Console.WriteLine("PASS pending record cannot be overwritten; a fresh client explicitly recovers black");
            using var picture = new SafeWallpaperService(exe, Path.Combine(root, "picture-baseline"));
            picture.Capture(); picture.Apply(paths[0]);
            byte[] original = File.ReadAllBytes(WallpaperNative.GetImage());
            try
            {
                using var replacement = new SafeWallpaperService(exe, Path.Combine(root, "picture-restore"));
                replacement.Capture(); replacement.Apply(paths[2]); replacement.Restore();
                Check(File.ReadAllBytes(WallpaperNative.GetImage()).SequenceEqual(original), "Original image bytes changed");
            }
            finally { picture.Restore(); }
            AssertBlack();
            Console.WriteLine("PASS original image backup restored byte-for-byte; final background is pure black");
        }
        finally
        {
            // Never retry a failed native operation automatically, even in the test harness.
            if (!service.IsSafetyStopped && service.HasPendingRestore) service.Restore();
        }
    }
    private static void AssertBlack() => Check(WallpaperNative.GetImage() == "" && WallpaperNative.GetBackgroundColor() == 0
        && WallpaperNative.GetBackgroundType() == 1, "Black background mode/path/color mismatch");
    private static async Task Started(string data)
    {
        for (int i = 0; i < 150; i++)
        { if (File.Exists(Path.Combine(data, "test-worker-started"))) return; await Task.Delay(20); }
        throw new Exception("Worker did not start");
    }
    private static bool Exited(int pid)
    { try { using var p = Process.GetProcessById(pid); return p.HasExited; } catch (ArgumentException) { return true; } }
    private static void Check(bool value, string error) { if (!value) throw new Exception(error); }
    private static void ExpectFault(Action action)
    { try { action(); } catch (WallpaperOperationException) { return; } throw new Exception("Expected safe failure"); }
}
