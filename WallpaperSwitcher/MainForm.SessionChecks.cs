using Microsoft.Win32;

namespace WallpaperSwitcher;

public sealed partial class MainForm
{
    // Explicit diagnostic mode, isolated settings, real message loop and real desktop helpers.
    // Session notifications are injected; the test never locks or suspends the user's computer.
    internal static int VerifySession(string reportPath)
    {
        string root = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        if (!File.Exists(Path.Combine(root, "allow-session-tests"))) return 3;
        using var mutex = new Mutex(true, @"Local\SimpleWallpaperSwitcher", out bool first);
        if (!first) return 4;
        if (!WallpaperNative.IsInteractiveDesktop() || WallpaperNative.GetBackgroundType() != 1
            || WallpaperNative.GetBackgroundColor() != 0) return 5;
        string data = Path.Combine(root, "session-" + Guid.NewGuid().ToString("N"));
        string images = Path.Combine(data, "images"); Directory.CreateDirectory(images);
        for (int i = 0; i < 6; i++)
        {
            using var bitmap = new Bitmap(240, 400);
            using (var g = Graphics.FromImage(bitmap)) g.Clear(Color.FromArgb(30 + i * 25, 100, 160));
            WallpaperEncoding.SaveJpeg(bitmap, Path.Combine(images, $"portrait-{i}.jpg"));
        }
        SettingsStore.TestDataDirectory = data;
        SettingsStore.Save(new AppSettings { Folder = images, IntervalSeconds = 600, CollageCount = 2,
            HotkeyModifiers = 7, HotkeyKey = (uint)Keys.F10, StopHotkeyModifiers = 7, StopHotkeyKey = (uint)Keys.F11,
            StartInTrayAndPlay = false });
        using var service = new SafeWallpaperService();
        using var form = new MainForm(service);
        form.suppressNotifications = true;
        using var context = new ApplicationContext(form);
        var passed = new List<string>();
        string? error = null; int exitCode = 1;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        async Task Settled()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            while (form.session.Pending || form.desktopOperation) await Task.Delay(50, timeout.Token);
        }
        async Task Notify(SessionSwitchReason reason)
        { form.SessionChanged(null, new SessionSwitchEventArgs(reason)); await Task.Delay(100); }
        async Task Power(PowerModes mode)
        { form.PowerChanged(null!, new PowerModeChangedEventArgs(mode)); await Task.Delay(100); }
        void Black() => Check(WallpaperNative.GetImage() == "" && WallpaperNative.GetBackgroundType() == 1
            && WallpaperNative.GetBackgroundColor() == 0, "Expected pure black");
        _ = form.Handle;
        form.BeginInvoke(async () =>
        {
            try
            {
                await form.ScanAsync();
                form.sort.SelectedIndex = (int)SortMode.Random;
                var catalogBeforeStart = form.catalog;
                string[] previewOrder = form.ordered.Select(p => p.Path).ToArray();
                await form.ScanAsync(force: false);
                Check(ReferenceEquals(catalogBeforeStart, form.catalog) && previewOrder.SequenceEqual(form.ordered.Select(p => p.Path)),
                    "Fresh catalog was rescanned or random preview changed");
                File.Copy(Path.Combine(images, "portrait-0.jpg"), Path.Combine(images, "portrait-6.jpg"));
                await form.ScanAsync(); Check(form.catalog.Count == 7, "Explicit refresh missed new images");
                form.sort.SelectedIndex = (int)SortMode.NameAscending;
                using (var conflictingWindow = new Form())
                {
                    Check(HotkeyNative.RegisterHotKey(conflictingWindow.Handle, 999, 7 | 0x4000, (uint)Keys.F11), "Could not reserve test hotkey");
                    try
                    {
                        await form.StartAsync(false);
                        Check(!service.IsSafetyStopped && !service.HasPendingRestore && !form.hotkeyRegistered
                            && !form.stopHotkeyRegistered && form.start.Enabled && !form.rotation.IsRunning,
                            "Hotkey conflict became a desktop fault or leaked a registration");
                    }
                    finally { HotkeyNative.UnregisterHotKey(conflictingWindow.Handle, 999); }
                }
                passed.Add("fresh catalog reused, random preview stable, explicit refresh sees additions; hotkey conflict leaves Start usable");
                await form.StartAsync(false);
                Check(form.rotation.IsRunning && form.start.Enabled, "Could not start");
                string[] frame = form.rotation.CurrentPaths.ToArray();
                string? original = WallpaperWorker.SnapshotHash(data);
                await Notify(SessionSwitchReason.SessionLock);
                Check(!form.timer.Enabled && !form.rotation.IsRunning && !service.IsSafetyStopped, "Lock was treated as a fault");
                await Notify(SessionSwitchReason.SessionUnlock); await Settled();
                Check(form.rotation.IsRunning && form.timer.Enabled && form.rotation.CurrentPaths.SequenceEqual(frame), "Unlock did not resume the same frame");
                Check(WallpaperWorker.SnapshotHash(data) == original, "Original background was overwritten");
                int pid = service.LastWorkerProcessId;
                await Notify(SessionSwitchReason.SessionUnlock); await Task.Delay(1700);
                Check(service.LastWorkerProcessId == pid, "Duplicate unlock replayed wallpaper");
                passed.Add("playing -> lock/unlock -> same frame, timer and original backup preserved; duplicate unlock ignored");

                await form.ToggleRotationAsync(); Black(); pid = service.LastWorkerProcessId;
                await Notify(SessionSwitchReason.SessionLock); await Power(PowerModes.Suspend);
                await Power(PowerModes.Resume);
                Check(form.session.Pending && !form.session.CanResume, "Resumed while still locked");
                await Notify(SessionSwitchReason.SessionUnlock); await Settled(); Black();
                Check(form.rotation.IsPaused && !form.timer.Enabled && form.start.Enabled
                    && form.hotkeyRegistered && form.stopHotkeyRegistered && service.LastWorkerProcessId == pid,
                    "Manual pause or hotkeys lost, or wallpaper changed while paused");
                await form.ToggleRotationAsync();
                Check(form.rotation.IsRunning && form.rotation.CurrentPaths.SequenceEqual(frame), "Explicit toggle did not resume");
                passed.Add("manual pause -> lock/sleep/wake/unlock -> black, no desktop writes, hotkeys retained, explicit toggle resumes");

                await Power(PowerModes.Suspend); await Notify(SessionSwitchReason.SessionLock);
                await Notify(SessionSwitchReason.SessionUnlock);
                Check(!form.session.CanResume, "Unlock bypassed pending sleep");
                await Power(PowerModes.Resume); await Settled();
                Check(form.rotation.IsRunning && form.timer.Enabled, "Wake after unlock failed");
                passed.Add("sleep/lock with reverse wake order resumes once");

                var next = form.NextPictureAsync(); form.ChangeSession(SessionBlocker.Locked, true); await next;
                Check(!service.IsSafetyStopped, "In-flight cancellation became a fault");
                form.ChangeSession(SessionBlocker.Locked, false); await Settled();
                Check(form.rotation.IsRunning && form.rotation.CurrentPaths.SequenceEqual(frame), "Interrupted next lost the current frame");
                await form.NextPictureAsync();
                Check(Path.GetFileName(form.rotation.CurrentPath) == "portrait-2.jpg", "Interrupted next skipped images");
                passed.Add("lock during next cancels old operation and resumes without skipping");

                var pause = form.ToggleRotationAsync(); form.ChangeSession(SessionBlocker.Locked, true); await pause;
                form.ChangeSession(SessionBlocker.Locked, false); await Settled(); Black();
                Check(form.rotation.IsPaused && !form.timer.Enabled && form.start.Enabled, "Interrupted manual pause resumed playback");
                passed.Add("lock during manual pause completes black restoration without autoplay");

                service.StopForSafety("injected real fault"); form.FinishDesktopOperation(); pid = service.LastWorkerProcessId;
                await Notify(SessionSwitchReason.SessionLock); await Notify(SessionSwitchReason.SessionUnlock);
                await Task.Delay(1700);
                Check(service.IsSafetyStopped && service.LastWorkerProcessId == pid && form.start.Enabled,
                    "Real fault retried automatically or blocked the manual recovery button");
                await form.ToggleRotationAsync();
                Check(form.rotation.IsRunning && !service.IsSafetyStopped, "Explicit recovery could not start");
                await form.StopRotationAsync(); Black();
                passed.Add("real fault never auto retries; recovery/start button works; final pure black");

                var starting = form.StartAsync(false);
                using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    while (!form.desktopOperation) await Task.Delay(10, limit.Token);
                form.ChangeSession(SessionBlocker.Locked, true); await starting;
                form.ChangeSession(SessionBlocker.Locked, false); await Settled();
                Check(form.rotation.IsRunning && form.timer.Interval == 600000 && form.start.Enabled,
                    "First-start interruption lost playback or used the default rapid timer");
                await form.StopRotationAsync(); Black();
                passed.Add("lock during initial start resumes with the configured full interval");
                exitCode = 0;
            }
            catch (Exception ex) { error = ex.ToString(); }
            finally
            {
                form.sessionTimer.Stop(); form.timer.Stop(); form.session.Cancel();
                try { await Task.Run(service.Recover); Black(); }
                catch (Exception ex) { error += " Cleanup: " + ex; exitCode = 1; }
                SettingsStore.WriteAtomic(reportPath, new { Success = exitCode == 0, Passed = passed, Error = error, DataDirectory = data });
                form.allowClose = true; form.Close(); context.ExitThread();
            }
        });
        Application.Run(context);
        return exitCode;
    }
}
