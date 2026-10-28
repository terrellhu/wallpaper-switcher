using System.Runtime.InteropServices;
using System.Text.Json;
using WallpaperSwitcher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Resource checks are deliberately routed before the mutex and all desktop/COM access.
        if (args.Contains("--check-render-resources")) return CheckRenderResources();
        bool first = true;
        using var mutex = args.Contains("--inspect-live") ? null : new Mutex(true, @"Local\SimpleWallpaperSwitcher", out first);
        if (!first) throw new InvalidOperationException("Exit the wallpaper app before native checks.");
        if (args.Contains("--inspect-live"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Wallpaper = WallpaperNative.GetImage(),
                Background = WallpaperNative.GetBackgroundColor(), Position = WallpaperNative.GetPosition() }));
            return 0;
        }
        if (args.Contains("--check-user32")) return CheckUser32();
        Console.WriteLine("Opening desktop interface...");
        var desktop = (IDesktopWallpaper)Activator.CreateInstance(
            Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"))!)!;
        Console.WriteLine("Desktop interface opened.");
        try
        {
            PrintState(desktop, "Before");
            if (args.Length == 0 || (args[0] != "--repair-black-and-check" && args[0] != "--check-fit-modes" && args[0] != "--check-collage" && args[0] != "--benchmark-render")) return 0;
            string folder = Path.Combine(Path.GetTempPath(), "wallpaper-native-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            if (args[0] == "--benchmark-render")
            {
                var fixtures = new List<string>();
                for (int i = 0; i < 3; i++)
                {
                    string path = Path.Combine(folder, $"large-{i}.jpg");
                    using var fixture = new Bitmap(2000, 3000);
                    using (var g = Graphics.FromImage(fixture))
                    using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 2000, 3000),
                        Color.Coral, Color.SteelBlue, 30 + i * 30)) g.FillRectangle(brush, 0, 0, 2000, 3000);
                    fixture.Save(path, System.Drawing.Imaging.ImageFormat.Jpeg);
                    fixtures.Add(path);
                }
                var samples = new List<double>();
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                var cpu = process.TotalProcessorTime;
                for (int i = 0; i < 6; i++)
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    using var rendered = CollageRenderer.Render(fixtures, new Size(2560, 1600));
                    WallpaperEncoding.SaveJpeg(rendered, Path.Combine(folder, "benchmark.jpg"));
                    if (i > 0) samples.Add(clock.Elapsed.TotalMilliseconds);
                }
                process.Refresh();
                Console.WriteLine(JsonSerializer.Serialize(new { Scenario = "3 x 2000x3000 JPEG -> 2560x1600 JPEG quality 92, render+encode+write",
                    JpegBytes = new FileInfo(Path.Combine(folder, "benchmark.jpg")).Length, EquivalentBmpBytes = 2560L * 1600 * 3 + 54,
                    MedianMilliseconds = samples.Order().ElementAt(2), MaxMilliseconds = samples.Max(),
                    CpuMillisecondsForSixFrames = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                    PrivateMiB = process.PrivateMemorySize64 / 1048576.0, PeakWorkingSetMiB = process.PeakWorkingSet64 / 1048576.0 }));
                return 0;
            }
            if (args[0] == "--check-collage")
            {
                string output = args.Length > 1 ? Path.GetFullPath(args[1]) : folder;
                Directory.CreateDirectory(output);
                var paths = new List<string>();
                var colors = new[] { Color.Coral, Color.SeaGreen, Color.SteelBlue };
                for (int i = 0; i < 3; i++)
                {
                    string path = Path.Combine(output, $"portrait-{i + 1}.png");
                    using var source = new Bitmap(400, 600);
                    using (var graphics = Graphics.FromImage(source))
                    {
                        graphics.Clear(colors[i]);
                        graphics.FillEllipse(Brushes.White, 150, 130, 100, 100);
                        using var font = new Font("Segoe UI", 36, FontStyle.Bold);
                        graphics.DrawString((i + 1).ToString(), font, Brushes.White, 170, 320);
                    }
                    source.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    paths.Add(path);
                }
                var plan = CollageRenderer.PlanFrame(paths, new Size(1920, 1080), true);
                if (plan.Count != 3) throw new Exception("Expected three portrait images at 16:9.");
                if (CollageRenderer.PlanFrame(paths, new Size(1920, 1080), true, 2).Count != 2
                    || CollageRenderer.PlanFrame(paths, new Size(2560, 1600), true, 3).Count != 3
                    || CollageRenderer.PlanFrame(paths.Take(2).ToArray(), new Size(2560, 1600), true, 3).Count != 2
                    || CollageRenderer.PlanFrame(paths, new Size(2560, 1600), false, 3).Count != 1)
                    throw new Exception("Incorrect fixed count or disabled grouping.");
                using (var rendered = CollageRenderer.Render(plan, new Size(1920, 1080)))
                {
                    if (rendered.Size != new Size(1920, 1080)) throw new Exception("Incorrect canvas size.");
                    for (int i = 0; i < 3; i++)
                        if (rendered.GetPixel(i * 640 + 320, 30).ToArgb() != colors[i].ToArgb())
                            throw new Exception("Incorrect panel ordering or black padding.");
                    for (int x = 0; x < rendered.Width; x++)
                        if (rendered.GetPixel(x, 0).ToArgb() == Color.Black.ToArgb())
                            throw new Exception("Unexpected seam or uncovered canvas.");
                    rendered.Save(Path.Combine(output, "collage-preview.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                Console.WriteLine("PASS ordered, full-screen collage rendering without seams");
                using var service = new WindowsWallpaper(folder);
                service.SetFitMode(WallpaperFit.Fit);
                service.CollageCount = 3;
                var collageRotation = new RotationController(service);
                try
                {
                    collageRotation.Start(paths.Select(path => new PictureFile(path, DateTime.UtcNow)).ToList(), false);
                    if (collageRotation.CurrentPaths.Count != 3) throw new Exception("Expected fixed three-picture collage.");
                    AssertPosition(WallpaperFit.Fill);
                    var frame = collageRotation.CurrentPaths.ToArray();
                    var cacheBefore = Directory.GetFiles(folder, "current-*.jpg").Select(p => (p, File.GetLastWriteTimeUtc(p))).ToArray();
                    if (cacheBefore.Length != 1) throw new Exception("Expected one JPEG cache file.");
                    using (var jpeg = Image.FromFile(cacheBefore[0].p))
                        if (jpeg.RawFormat.Guid != System.Drawing.Imaging.ImageFormat.Jpeg.Guid || jpeg.Size != WindowsWallpaper.TargetSize(WallpaperFit.Fit))
                            throw new Exception("Incorrect JPEG format or dimensions.");
                    collageRotation.Pause(); collageRotation.Resume();
                    var cacheAfter = Directory.GetFiles(folder, "current-*.jpg").Select(p => (p, File.GetLastWriteTimeUtc(p))).ToArray();
                    if (!cacheBefore.SequenceEqual(cacheAfter)) throw new Exception("Resume unnecessarily rendered a new bitmap.");
                    if (!collageRotation.CurrentPaths.SequenceEqual(frame)) throw new Exception("Resume changed the collage.");
                    AssertPosition(WallpaperFit.Fill);
                    // Both recent entries should survive A -> B -> A without rewriting A.
                    service.Apply(paths[2]);
                    service.ApplyFrame(frame);
                    if (File.GetLastWriteTimeUtc(cacheBefore[0].p) != cacheBefore[0].Item2)
                        throw new Exception("Returning to recent collage rewrote the JPEG.");
                    service.Apply(paths[2]);
                    var twoEntries = Directory.GetFiles(folder, "current-*.jpg").Select(p => (p, File.GetLastWriteTimeUtc(p))).ToArray();
                    service.ApplyFrame(frame); service.Apply(paths[2]);
                    if (!twoEntries.SequenceEqual(Directory.GetFiles(folder, "current-*.jpg").Select(p => (p, File.GetLastWriteTimeUtc(p)))))
                        throw new Exception("Repeated single/collage frames should hit both cache entries.");
                    using (var replacement = new Bitmap(400, 600))
                    {
                        using (var graphics = Graphics.FromImage(replacement)) graphics.Clear(Color.Gold);
                        replacement.Save(paths[0], System.Drawing.Imaging.ImageFormat.Png);
                    }
                    service.ApplyFrame(frame);
                    var cacheChanged = Directory.GetFiles(folder, "current-*.jpg").Select(p => (p, File.GetLastWriteTimeUtc(p))).ToArray();
                    if (twoEntries.SequenceEqual(cacheChanged)) throw new Exception("Changed source did not invalidate cached collage.");
                    service.Apply(paths[0]); service.Apply(paths[1]); service.Apply(paths[2]);
                    if (Directory.GetFiles(folder, "current-*.jpg").Length != 2 || Directory.GetFiles(folder, "*.tmp").Length != 0)
                        throw new Exception("JPEG cache must stay bounded with no temporary files.");
                }
                finally { collageRotation.Stop(); }
                Console.WriteLine("PASS native collage apply, pause, resume and original-background restoration");
                // Verify image backups remain byte-for-byte original and usable by a fresh service after a crash.
                using (var baseline = new WindowsWallpaper(Path.Combine(folder, "baseline")))
                {
                    baseline.Capture();
                    try
                    {
                        string originalInput = Path.Combine(folder, "original-input.jpg");
                        using (var originalBitmap = new Bitmap(paths[0])) WallpaperEncoding.SaveJpeg(originalBitmap, originalInput);
                        var setup = (IDesktopWallpaper)Activator.CreateInstance(
                            Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"))!)!;
                        try
                        {
                            setup.SetBackgroundColor(0x332211); setup.SetPosition((int)WallpaperFit.Fit);
                            setup.SetWallpaper(null, originalInput); setup.Enable(true);
                        }
                        finally { Marshal.ReleaseComObject(setup); }
                        string recoveryFolder = Path.Combine(folder, "image-recovery");
                        using (var crashed = new WindowsWallpaper(recoveryFolder))
                        { crashed.Capture(); crashed.ApplyFrame(paths); }
                        using (var recovered = new WindowsWallpaper(recoveryFolder)) recovered.Restore();
                        var fresh = (IDesktopWallpaper)Activator.CreateInstance(
                            Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"))!)!;
                        try
                        {
                            string restoredPath = fresh.GetWallpaper(fresh.GetMonitorDevicePathAt(0));
                            bool sameBytes = File.ReadAllBytes(restoredPath).SequenceEqual(File.ReadAllBytes(originalInput));
                            if (!sameBytes || fresh.GetPosition() != (int)WallpaperFit.Fit || fresh.GetBackgroundColor() != 0x332211)
                                throw new Exception($"Recovery mismatch: sameBytes={sameBytes}, position={fresh.GetPosition()}, color={fresh.GetBackgroundColor():x}, path={restoredPath}");
                        }
                        finally { Marshal.ReleaseComObject(fresh); }
                        if (File.Exists(Path.Combine(recoveryFolder, "restore.json"))) throw new Exception("Recovery marker remains.");
                    }
                    finally { baseline.Restore(); }
                }
                Console.WriteLine("PASS JPEG cache reuse, two-file bound, uncompressed original backup and fresh-service image recovery");
                Console.WriteLine("Collage artifacts: " + output);
                return 0;
            }
            string picture = Path.Combine(folder, "fixture.png");
            using (var bitmap = new Bitmap(64, 64))
            {
                using var graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.SteelBlue);
                bitmap.Save(picture, System.Drawing.Imaging.ImageFormat.Png);
            }
            if (args[0] == "--check-fit-modes")
            {
                int originalPosition = desktop.GetPosition();
                uint originalColor = desktop.GetBackgroundColor();
                using var service = new WindowsWallpaper(folder);
                service.Capture();
                try
                {
                    foreach (var mode in Enum.GetValues<WallpaperFit>())
                    {
                        service.SetFitMode(mode);
                        service.Apply(picture);
                        AssertPosition(mode);
                        Console.WriteLine("PASS Windows wallpaper position: " + mode);
                    }
                    service.SetFitMode(WallpaperFit.Fit, updateDesktop: true);
                    AssertPosition(WallpaperFit.Fit);
                    Console.WriteLine("PASS live position update");
                }
                finally { service.Restore(); }
                var restored = (IDesktopWallpaper)Activator.CreateInstance(
                    Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"))!)!;
                try
                {
                    if (restored.GetPosition() != originalPosition || restored.GetBackgroundColor() != originalColor)
                        throw new Exception("Original desktop position/color was not restored.");
                    PrintState(restored, "RestoredAfterFitChecks");
                }
                finally { Marshal.ReleaseComObject(restored); }
                return 0;
            }
            // Explicit repair mode: the user's desired final desktop is solid black.
            desktop.SetBackgroundColor(0);
            WallpaperNative.ClearImage();
            desktop.Enable(false);
            AssertSolidBlack(desktop);
            using var wallpaper = new WindowsWallpaper(folder);
            var rotation = new RotationController(wallpaper);
            try
            {
                rotation.Start([new(picture, DateTime.UtcNow)], false);
                AssertPicture(desktop);
                var snapshot = JsonSerializer.Deserialize<DesktopSnapshot>(File.ReadAllText(Path.Combine(folder, "restore.json")))!;
                if (snapshot.WallpaperEnabled != false || !snapshot.RestoreSolidColor)
                    throw new Exception("Solid-color baseline was not captured.");
                rotation.Pause();
                AssertSolidBlack(desktop);
                rotation.Resume();
                AssertPicture(desktop);
                rotation.Stop();
                AssertSolidBlack(desktop);
                if (wallpaper.HasPendingRestore) throw new Exception("Restore marker was not cleared.");
                Console.WriteLine("PASS black -> slideshow -> pause -> black -> resume -> slideshow -> stop -> black");

                // Compatibility: an old empty-path snapshot must explicitly disable the image.
                wallpaper.Capture();
                var legacy = JsonSerializer.Deserialize<DesktopSnapshot>(File.ReadAllText(Path.Combine(folder, "restore.json")))!;
                SettingsStore.WriteAtomic(Path.Combine(folder, "restore.json"), legacy with { WallpaperEnabled = null });
                wallpaper.Apply(picture);
                wallpaper.Restore();
                AssertSolidBlack(desktop);
                Console.WriteLine("PASS legacy solid-color snapshot restores black");
            }
            finally
            {
                desktop.SetBackgroundColor(0);
                WallpaperNative.ClearImage();
                desktop.Enable(false);
            }
            PrintState(desktop, "After");
            return 0;
        }
        finally { Marshal.ReleaseComObject(desktop); }
    }

    private static int CheckUser32()
    {
        if (Screen.AllScreens.Length != 1) throw new Exception("This check requires one screen.");
        string folder = Path.Combine(Path.GetTempPath(), "wallpaper-user32-" + Guid.NewGuid().ToString("N"));
        using var baseline = new WindowsWallpaper(Path.Combine(folder, "baseline"));
        baseline.Capture();
        try
        {
            WallpaperNative.SetBackgroundColor(0); WallpaperNative.SetImage("");
            var paths = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                string path = Path.Combine(folder, $"portrait-{i}.jpg");
                using var bitmap = new Bitmap(400, 600);
                using (var g = Graphics.FromImage(bitmap)) g.Clear(i == 0 ? Color.Coral : i == 1 ? Color.SeaGreen : Color.SteelBlue);
                WallpaperEncoding.SaveJpeg(bitmap, path); paths.Add(path);
            }
            string data = Path.Combine(folder, "rotation");
            using var service = new WindowsWallpaper(data) { CollageCount = 3 };
            var rotation = new RotationController(service);
            try
            {
                rotation.Start(paths.Select(p => new PictureFile(p, DateTime.UtcNow)).ToList(), false);
                for (int i = 0; i < 5; i++)
                {
                    if (!WallpaperNative.GetImage().EndsWith(".jpg")) throw new Exception("JPEG was not applied.");
                    rotation.Pause();
                    if (WallpaperNative.GetImage() != "" || WallpaperNative.GetBackgroundColor() != 0 || service.HasPendingRestore)
                        throw new Exception("Pause did not persist the original black background.");
                    rotation.Resume();
                    var snapshot = JsonSerializer.Deserialize<DesktopSnapshot>(File.ReadAllText(Path.Combine(data, "restore.json")))!;
                    if (!snapshot.RestoreSolidColor) throw new Exception("Resume captured the app's own cached wallpaper.");
                }
                rotation.Pause();
                WallpaperNative.SetPosition(WallpaperFit.Fit); WallpaperNative.SetBackgroundColor(0x332211);
                WallpaperNative.SetImage(paths[0]);
                rotation.Resume(); rotation.Pause();
                if (!File.ReadAllBytes(WallpaperNative.GetImage()).SequenceEqual(File.ReadAllBytes(paths[0]))
                    || WallpaperNative.GetPosition() != WallpaperFit.Fit || WallpaperNative.GetBackgroundColor() != 0x332211)
                    throw new Exception("Background changed while paused was not preserved.");
            }
            finally { rotation.Stop(); }
            Console.WriteLine("PASS repeated JPEG -> pause -> solid black -> resume, and background changed while paused");
            service.Capture();
            try
            {
                foreach (var mode in Enum.GetValues<WallpaperFit>())
                {
                    service.SetFitMode(mode); service.Apply(paths[1]);
                    if (WallpaperNative.GetPosition() != mode) throw new Exception("Wrong fit: " + mode);
                }
                using var fresh = new WindowsWallpaper(data);
                fresh.Restore();
                if (!File.ReadAllBytes(WallpaperNative.GetImage()).SequenceEqual(File.ReadAllBytes(paths[0])))
                    throw new Exception("Fresh service did not recover original image bytes.");
            }
            finally { service.Restore(); }
            Console.WriteLine("PASS six fit modes and fresh-service image restoration");
            return 0;
        }
        finally { baseline.Restore(); }
    }

    private static void AssertSolidBlack(IDesktopWallpaper desktop)
    {
        var fresh = (IDesktopWallpaper)Activator.CreateInstance(
            Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"))!)!;
        try
        {
            if (fresh.GetBackgroundColor() != 0) throw new Exception("Expected a pure black background.");
            for (uint i = 0; i < fresh.GetMonitorDevicePathCount(); i++)
                if (!string.IsNullOrEmpty(fresh.GetWallpaper(fresh.GetMonitorDevicePathAt(i))))
                    throw new Exception("Expected a persisted empty wallpaper path.");
        }
        finally { Marshal.ReleaseComObject(fresh); }
    }
    private static void AssertPicture(IDesktopWallpaper desktop)
    {
        var fresh = (IDesktopWallpaper)Activator.CreateInstance(
            Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"))!)!;
        try
        {
            if ((fresh.GetStatus() & 1) == 0) throw new Exception("Wallpaper image was not enabled.");
        }
        finally { Marshal.ReleaseComObject(fresh); }
    }
    private static void AssertPosition(WallpaperFit mode)
    {
        var fresh = (IDesktopWallpaper)Activator.CreateInstance(
            Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"))!)!;
        try
        {
            if (fresh.GetPosition() != (int)mode) throw new Exception("Incorrect desktop position: " + mode);
        }
        finally { Marshal.ReleaseComObject(fresh); }
    }
    private static int CheckRenderResources()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        string root = Path.Combine(Path.GetTempPath(), "wallpaper-render-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = new Size(6000, 4000); var screen = new Size(2560, 1600);
        string path = Path.Combine(root, "large.jpg");
        using (var fixture = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
        using (var g = Graphics.FromImage(fixture))
        using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(Point.Empty, source), Color.Coral, Color.SteelBlue, 35))
        { g.FillRectangle(brush, new Rectangle(Point.Empty, source)); WallpaperEncoding.SaveJpeg(fixture, path); }
        Size fill = WindowsWallpaper.SingleRenderSize(source, screen, WallpaperFit.Fill);
        Size fit = WindowsWallpaper.SingleRenderSize(source, screen, WallpaperFit.Fit);
        Check(fill.Width == 2560 && fill.Height is >= 1706 and <= 1707, "Fill resolution is not sufficient for the screen");
        Check(fit == new Size(2400, 1600), "Fit resolution is incorrect");
        foreach (var mode in new[] { WallpaperFit.Center, WallpaperFit.Tile })
            Check(WindowsWallpaper.SingleRenderSize(source, screen, mode) == source, "Original pixel semantics changed");
        Check(WindowsWallpaper.SingleRenderSize(new Size(640, 480), screen, WallpaperFit.Fill) == new Size(640, 480), "Small images were upscaled unnecessarily");
        var huge = WindowsWallpaper.SingleRenderSize(new Size(20000, 30000), new Size(16000, 10000), WallpaperFit.Fill);
        Check((long)huge.Width * huge.Height <= 16777216 && Math.Max(huge.Width, huge.Height) <= 8192, "Single frame allocation exceeds the resource cap");
        using (var bounded = WindowsWallpaper.RenderSingle(path, fill))
        {
            Check(bounded.Size == fill && bounded.PixelFormat == System.Drawing.Imaging.PixelFormat.Format24bppRgb, "Rendered canvas differs from budget");
            Check(bounded.GetPixel(0, 0).ToArgb() != Color.Black.ToArgb()
                && bounded.GetPixel(fill.Width - 1, fill.Height - 1).ToArgb() != Color.Black.ToArgb(), "Downsampling introduced black edges");
        }
        string transparent = Path.Combine(root, "alpha.png");
        using (var fixture = new Bitmap(100, 200))
        {
            using (var g = Graphics.FromImage(fixture)) { g.Clear(Color.Transparent); g.FillRectangle(Brushes.Red, 20, 40, 60, 120); }
            fixture.Save(transparent, System.Drawing.Imaging.ImageFormat.Png);
        }
        using (var rendered = WindowsWallpaper.RenderSingle(transparent, new Size(50, 100)))
        { Check(rendered.GetPixel(0, 0).ToArgb() == Color.Black.ToArgb(), "Transparent regions did not become black");
          Check(rendered.GetPixel(25, 50).R > 240, "Image content was not rendered"); }
        var samples = new Dictionary<string, double[]>();
        foreach (var scenario in new[] { (Name: "original", Size: source), (Name: "bounded", Size: fill) })
        {
            var times = new List<double>();
            for (int i = 0; i < 4; i++)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                using var rendered = WindowsWallpaper.RenderSingle(path, scenario.Size);
                WallpaperEncoding.SaveJpeg(rendered, Path.Combine(root, scenario.Name + ".jpg"));
                if (i > 0) times.Add(clock.Elapsed.TotalMilliseconds);
            }
            samples[scenario.Name] = times.ToArray();
        }
        static long CanvasBytes(Size size) => ((size.Width * 3L + 3) & ~3L) * size.Height;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Success = true, Source = source, Output = fill, FitOutput = fit,
            OriginalRgbCanvasBytes = CanvasBytes(source), BoundedRgbCanvasBytes = CanvasBytes(fill),
            Note = "Canvas byte counts are exact RGB stride allocation estimates, not overall process working set; source decoding still uses memory.",
            OriginalMedianMilliseconds = samples["original"].Order().ElementAt(1),
            BoundedMedianMilliseconds = samples["bounded"].Order().ElementAt(1),
            OriginalJpegBytes = new FileInfo(Path.Combine(root, "original.jpg")).Length,
            BoundedJpegBytes = new FileInfo(Path.Combine(root, "bounded.jpg")).Length,
            Artifacts = root
        }));
        return 0;
    }

    private static void PrintState(IDesktopWallpaper desktop, string phase)
    {
        Console.WriteLine(JsonSerializer.Serialize(new {
            Phase = phase, WallpaperEnabled = (desktop.GetStatus() & 1) != 0,
            Background = desktop.GetBackgroundColor(), Monitors = desktop.GetMonitorDevicePathCount(),
            Wallpaper = desktop.GetWallpaper(desktop.GetMonitorDevicePathAt(0))
        }));
    }
}
