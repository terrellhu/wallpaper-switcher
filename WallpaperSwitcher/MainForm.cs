namespace WallpaperSwitcher;

public sealed partial class MainForm : Form
{
    private readonly SafeWallpaperService wallpaper;
    private readonly RotationController rotation;
    private AppSettings settings = SettingsStore.Load();
    private readonly TextBox folder = new() { Dock = DockStyle.Fill, PlaceholderText = "选择一个图片目录，自动包含所有子目录" };
    private readonly ComboBox sort = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly NumericUpDown interval = new() { Minimum = 1, Maximum = 86400, Width = 110, ThousandsSeparator = true };
    private readonly CheckBox ctrl = new() { Text = "Ctrl", AutoSize = true };
    private readonly CheckBox alt = new() { Text = "Alt", AutoSize = true };
    private readonly CheckBox shift = new() { Text = "Shift", AutoSize = true };
    private readonly ComboBox hotkey = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly CheckBox stopCtrl = new() { Text = "Ctrl", AutoSize = true };
    private readonly CheckBox stopAlt = new() { Text = "Alt", AutoSize = true };
    private readonly CheckBox stopShift = new() { Text = "Shift", AutoSize = true };
    private readonly ComboBox stopHotkey = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly CheckBox launchAtLogon = new() { Text = "开机自启动", AutoSize = true };
    private readonly CheckBox startInTray = new() { Text = "启动后在托盘自动轮播", AutoSize = true };
    private readonly ListBox pictures = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true, IntegralHeight = false };
    private readonly Label count = new() { AutoSize = true, Text = "尚未扫描", ForeColor = Color.DimGray };
    private readonly Label status = new() { Dock = DockStyle.Fill, Text = "就绪 · 选择目录后，点击“开始轮播”", AutoEllipsis = true, Padding = new Padding(0, 8, 0, 0) };
    private readonly Button start = MakeButton("开始轮播", 116);
    private readonly Button stop = MakeButton("结束轮播", 124);
    private readonly Button next = MakeButton("下一张", 100);
    private readonly Button scan = MakeButton("刷新列表", 110);
    private readonly Button browse = MakeButton("选择目录…", 110);
    private readonly Button up = MakeButton("↑ 上移", 88);
    private readonly Button down = MakeButton("↓ 下移", 88);
    private readonly Button save = MakeButton("保存设置", 106);
    private readonly System.Windows.Forms.Timer timer = new();
    private readonly System.Windows.Forms.Timer sessionTimer = new() { Interval = 1500 };
    private readonly SessionPlaybackState session = new();
    private int readinessChecks;
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem trayNext = new("下一张");
    private readonly ToolStripMenuItem trayToggle = new("开始轮播");
    private readonly ToolStripMenuItem trayStop = new("结束轮播并恢复原壁纸");
    private List<PictureFile> catalog = [];
    private List<PictureFile> ordered = [];
    private string scannedFolder = "";
    private long scannedAt;
    private CancellationTokenSource? scanCancellation;
    private readonly ToolTip tips = new() { AutoPopDelay = 15000 };
    private bool busy;
    private bool desktopOperation;
    private bool closeRequested, allowClose, closing;
    private bool suppressSort;
    private bool hotkeyRegistered;
    private bool stopHotkeyRegistered;
    private bool updatingStartupControls;
    private bool suppressNotifications;
    private const int NextHotkeyId = 1;
    private const int StopHotkeyId = 2;

    public MainForm(SafeWallpaperService wallpaper)
    {
        this.wallpaper = wallpaper;
        rotation = new(wallpaper);
        var logo = BuildLayout();

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开设置", null, (_, _) => ShowWindow());
        menu.Items.Add(trayToggle); menu.Items.Add(trayNext); menu.Items.Add(trayStop); menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出并恢复原壁纸", null, (_, _) => Close());
        tray = new NotifyIcon { Icon = Icon, Text = "壁纸轮播 · 尚未开始", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowWindow();
        trayToggle.Click += async (_, _) => await ToggleRotationAsync();
        trayNext.Click += async (_, _) => await NextPictureAsync(); trayStop.Click += async (_, _) => await StopRotationAsync();
        browse.Click += async (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "选择图片目录（包含子目录）", UseDescriptionForTitle = true };
            if (Directory.Exists(folder.Text)) dialog.InitialDirectory = folder.Text;
            if (dialog.ShowDialog(this) == DialogResult.OK) { folder.Text = dialog.SelectedPath; await ScanAsync(); }
        };
        scan.Click += async (_, _) => await ScanAsync();
        start.Click += async (_, _) => await ToggleRotationAsync();
        next.Click += async (_, _) => await NextPictureAsync(); stop.Click += async (_, _) => await StopRotationAsync();
        save.Click += (_, _) => SaveSettings();
        sort.SelectedIndexChanged += (_, _) => { if (!suppressSort) RefreshOrder(); };
        up.Click += (_, _) => MovePicture(-1); down.Click += (_, _) => MovePicture(1);
        timer.Tick += async (_, _) => await NextPictureAsync();
        sessionTimer.Tick += async (_, _) => await ResumeSessionAsync();
        FormClosing += HandleClosing;
        Microsoft.Win32.SystemEvents.SessionSwitch += SessionChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += PowerChanged;
        FormClosed += (_, _) => { Microsoft.Win32.SystemEvents.SessionSwitch -= SessionChanged; Microsoft.Win32.SystemEvents.PowerModeChanged -= PowerChanged;
            scanCancellation?.Cancel(); wallpaper.Dispose(); timer.Dispose(); sessionTimer.Dispose(); tips.Dispose();
            tray.Visible = false; tray.Dispose(); logo.Image?.Dispose(); preview.Picture?.Dispose(); listDetailFont?.Dispose(); Icon?.Dispose(); };
        folder.Text = settings.Folder; interval.Value = settings.IntervalSeconds; sort.SelectedIndex = (int)settings.Sort;
        ctrl.Checked = (settings.HotkeyModifiers & 2) != 0; alt.Checked = (settings.HotkeyModifiers & 1) != 0; shift.Checked = (settings.HotkeyModifiers & 4) != 0;
        hotkey.SelectedIndex = 0;
        for (int i = 0; i < hotkey.Items.Count; i++) if ((uint)((KeyChoice)hotkey.Items[i]!).Key == settings.HotkeyKey) hotkey.SelectedIndex = i;
        stopCtrl.Checked = (settings.StopHotkeyModifiers & 2) != 0;
        stopAlt.Checked = (settings.StopHotkeyModifiers & 1) != 0;
        stopShift.Checked = (settings.StopHotkeyModifiers & 4) != 0;
        stopHotkey.SelectedIndex = 3; // End
        for (int i = 0; i < stopHotkey.Items.Count; i++)
            if ((uint)((KeyChoice)stopHotkey.Items[i]!).Key == settings.StopHotkeyKey) stopHotkey.SelectedIndex = i;
        try { launchAtLogon.Checked = StartupRegistration.IsEnabled(); }
        catch (Exception ex) { status.Text = "无法读取自启动设置：" + ex.Message; }
        startInTray.Checked = settings.StartInTrayAndPlay;
        wallpaper.SetFitMode(settings.FitMode);
        wallpaper.AutoCollage = settings.AutoCollage;
        wallpaper.CollageCount = settings.CollageCount;
        autoCollage.Checked = settings.AutoCollage;
        collageCount.SelectedIndex = settings.CollageCount == 0 ? 0 : settings.CollageCount - 1;
        collageCount.Enabled = settings.AutoCollage;
        autoCollage.CheckedChanged += async (_, _) => await ChangeAutoCollageAsync();
        collageCount.SelectedIndexChanged += async (_, _) => await ChangeAutoCollageAsync();
        SetFitSelection(settings.FitMode);
        launchAtLogon.CheckedChanged += (_, _) => ChangeLaunchAtLogon();
        startInTray.CheckedChanged += (_, _) => ChangeTrayStartup();
        UpdateControls();
        status.TextChanged += (_, _) => tips.SetToolTip(status, status.Text);
        tips.SetToolTip(sort, "随机模式每轮不重复。结束轮播后可修改顺序。");
        tips.SetToolTip(scan, "重新读取目录和子目录；新增或删除图片后可刷新列表。");
        VisibleChanged += (_, _) => RefreshPreviewVisibility();
        Resize += (_, _) => RefreshPreviewVisibility();
    }

    public async Task InitializeAsync(bool autoStart)
    {
        try
        {
            if (wallpaper.HasPendingRestore || wallpaper.IsSafetyStopped)
            {
                if (!wallpaper.IsSafetyStopped)
                    wallpaper.StopForSafety("上次壁纸操作未确认完成，需要手动恢复。");
                rotation.HaltForSafety();
                status.Text = "已安全停止 · 点击“恢复并开始”检查恢复后启动，或仅恢复原背景。";
                ShowWindow(); return;
            }
            if (autoStart)
            {
                if (string.IsNullOrWhiteSpace(folder.Text))
                {
                    status.Text = "请双击托盘图标，选择图片目录并保存设置。";
                    tray.ShowBalloonTip(5000, "壁纸轮播需要设置", status.Text, ToolTipIcon.Info);
                    return;
                }
                await StartAsync(saveSettings: false);
            }
            else if (Directory.Exists(folder.Text)) await ScanAsync();
        }
        catch (Exception ex) { ShowError("启动轮播失败，请从托盘打开设置", ex); }
        finally { if (!IsDisposed) UpdateControls(); }
    }

    private void ChangeLaunchAtLogon()
    {
        if (updatingStartupControls) return;
        try
        {
            StartupRegistration.SetEnabled(launchAtLogon.Checked);
            status.Text = launchAtLogon.Checked ? "已启用开机自启动：登录 Windows 后在托盘自动轮播。" : "已关闭开机自启动。";
        }
        catch (Exception ex)
        {
            updatingStartupControls = true; launchAtLogon.Checked = !launchAtLogon.Checked; updatingStartupControls = false;
            ShowError("无法修改开机自启动", ex);
        }
    }

    private void ChangeTrayStartup()
    {
        if (updatingStartupControls) return;
        bool previous = settings.StartInTrayAndPlay;
        try
        {
            settings.StartInTrayAndPlay = startInTray.Checked;
            SettingsStore.Save(settings);
            status.Text = "启动方式已保存，下次启动生效（开机自启动始终在托盘自动轮播）。";
        }
        catch (Exception ex)
        {
            settings.StartInTrayAndPlay = previous;
            updatingStartupControls = true; startInTray.Checked = previous; updatingStartupControls = false;
            ShowError("无法保存启动方式", ex);
        }
    }

    private async Task<bool> ScanAsync(bool force = true)
    {
        if (busy || desktopOperation || rotation.IsRunning || rotation.IsPaused) return false;
        string path;
        try { path = Path.GetFullPath(folder.Text.Trim()); }
        catch (Exception ex) { ShowError("请选择有效的图片目录", ex); return false; }
        if (!force && catalog.Count > 0 && string.Equals(path, scannedFolder, StringComparison.OrdinalIgnoreCase)
            && Environment.TickCount64 - scannedAt < 30000) return true;
        using var cancellation = new CancellationTokenSource(); scanCancellation = cancellation;
        busy = true; UpdateControls(); status.Text = "正在扫描目录和子目录…";
        try
        {
            var result = await Task.Run(() => PictureCatalog.Scan(path, cancellation.Token));
            if (IsDisposed || Disposing) return false;
            catalog = result; scannedFolder = path; scannedAt = Environment.TickCount64; RefreshOrder();
            status.Text = result.Count == 0 ? "目录中没有 JPG、PNG、BMP、GIF 或 TIFF 图片。" : "扫描完成，可以开始轮播。";
            return result.Count > 0;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { if (!IsDisposed) ShowError("扫描失败", ex); return false; }
        finally { scanCancellation = null; busy = false; if (!IsDisposed) UpdateControls(); }
    }

    private void RefreshOrder()
    {
        ordered = PictureCatalog.Order(catalog, (SortMode)Math.Max(0, sort.SelectedIndex), settings.CustomOrder);
        RefreshList();
    }
    private void RefreshList()
    {
        pictures.BeginUpdate(); pictures.Items.Clear();
        pictures.Items.AddRange(ordered.Select(picture => (object)Path.GetRelativePath(scannedFolder, picture.Path)).ToArray());
        pictures.EndUpdate(); count.Text = $"播放列表   ·   {ordered.Count} 张图片";
        if (ordered.Count > 0) pictures.SelectedIndex = 0;
        else
        {
            ++previewRequest;
            preview.Picture?.Dispose(); preview.Picture = null; preview.Invalidate();
            previewName.Text = "等待选择图片"; previewInfo.Text = "预览不会修改桌面壁纸";
        }
    }
    private void MovePicture(int offset)
    {
        int i = pictures.SelectedIndex, j = i + offset;
        if (i < 0 || j < 0 || j >= ordered.Count) return;
        (ordered[i], ordered[j]) = (ordered[j], ordered[i]);
        settings.CustomOrder = ordered.Select(p => p.Path).ToList();
        suppressSort = true; sort.SelectedIndex = (int)SortMode.Custom; suppressSort = false;
        RefreshList(); pictures.SelectedIndex = j;
    }
    private bool SaveSettings()
    {
        try
        {
            uint modifiers = (ctrl.Checked ? 2u : 0) | (alt.Checked ? 1u : 0) | (shift.Checked ? 4u : 0);
            uint stopModifiers = (stopCtrl.Checked ? 2u : 0) | (stopAlt.Checked ? 1u : 0) | (stopShift.Checked ? 4u : 0);
            uint nextKey = (uint)((KeyChoice)hotkey.SelectedItem!).Key;
            uint stopKey = (uint)((KeyChoice)stopHotkey.SelectedItem!).Key;
            if (modifiers == 0 || stopModifiers == 0)
                throw new InvalidOperationException("两个快捷键都请至少选择 Ctrl、Alt 或 Shift 中的一项。");
            if (modifiers == stopModifiers && nextKey == stopKey)
                throw new InvalidOperationException("“下一张”和“壁纸开关”不能使用相同的快捷键。");
            settings = new AppSettings
            {
                Folder = folder.Text.Trim(), IntervalSeconds = (int)interval.Value, Sort = (SortMode)sort.SelectedIndex,
                CustomOrder = settings.CustomOrder, HotkeyModifiers = modifiers, HotkeyKey = nextKey,
                StopHotkeyModifiers = stopModifiers, StopHotkeyKey = stopKey,
                StartInTrayAndPlay = startInTray.Checked, FitMode = settings.FitMode, AutoCollage = settings.AutoCollage,
                CollageCount = settings.CollageCount
            };
            SettingsStore.Save(settings); status.Text = "设置已保存。"; UpdateControls(); return true;
        }
        catch (Exception ex) { ShowError("保存设置失败", ex); return false; }
    }
    private async Task StartAsync(bool saveSettings = true)
    {
        if (busy || desktopOperation || session.Pending || session.IsUnavailable || wallpaper.IsSafetyStopped || rotation.IsRunning || rotation.IsPaused) return;
        if (!await ScanAsync(force: false) || IsDisposed || closing || closeRequested)
        {
            if (!IsDisposed && !Visible) tray.ShowBalloonTip(5000, "壁纸轮播未开始", status.Text, ToolTipIcon.Warning);
            return;
        }
        if (saveSettings && !SaveSettings()) return;
        if (session.Pending || session.IsUnavailable) return;
        try
        {
            RegisterPlaybackHotkeys();
        }
        catch (Exception ex)
        { ReleaseHotkey(); ShowError("快捷键不可用，请修改组合后重试", ex); UpdateControls(); return; }
        session.WantsPlayback = true;
        desktopOperation = true; UpdateControls();
        try
        {
            var playlist = ordered.ToList(); bool random = settings.Sort == SortMode.Random;
            timer.Interval = checked(settings.IntervalSeconds * 1000);
            await Task.Run(() => rotation.Start(playlist, random));
            timer.Start(); ShowCurrent();
        }
        catch (Exception ex) { HandleDesktopFailure(ex); }
        finally { FinishDesktopOperation(); }
        UpdateControls();
    }
    private void RegisterPlaybackHotkeys()
    {
        if (!HotkeyNative.RegisterHotKey(Handle, NextHotkeyId, settings.HotkeyModifiers | 0x4000, settings.HotkeyKey))
            throw new InvalidOperationException("“下一张”快捷键已被 Windows 或其他软件占用，请换一个组合。");
        hotkeyRegistered = true;
        if (!HotkeyNative.RegisterHotKey(Handle, StopHotkeyId, settings.StopHotkeyModifiers | 0x4000, settings.StopHotkeyKey))
            throw new InvalidOperationException("“壁纸开关”快捷键已被 Windows 或其他软件占用，请换一个组合。");
        stopHotkeyRegistered = true;
    }
    private async Task ToggleRotationAsync()
    {
        if (busy || desktopOperation || session.Pending || session.IsUnavailable) return;
        if (wallpaper.IsSafetyStopped)
        {
            // Only this explicit button/tray action may recover and restart. Autoplay remains blocked.
            if (!await StopRotationAsync() || IsDisposed || closing || closeRequested || wallpaper.IsSafetyStopped) return;
            await StartAsync();
            return;
        }
        if (!rotation.IsRunning && !rotation.IsPaused) { await StartAsync(); return; }
        session.WantsPlayback = !rotation.IsRunning;
        timer.Stop();
        desktopOperation = true; UpdateControls();
        try
        {
            if (rotation.IsRunning)
            {
                await Task.Run(rotation.Pause);
                status.Text = "已恢复原壁纸 · 再按开关快捷键，从当前图片继续轮播。";
                tray.Text = "壁纸轮播 · 已暂停（再次按快捷键继续）";
            }
            else
            {
                await Task.Run(rotation.Resume);
                // Resume the current image for a full interval before advancing.
                timer.Start();
                ShowCurrent();
            }
        }
        catch (Exception ex) { HandleDesktopFailure(ex); }
        finally { FinishDesktopOperation(); }
        UpdateControls();
    }
    private async Task NextPictureAsync()
    {
        if (!rotation.IsRunning || desktopOperation || session.Pending || session.IsUnavailable || wallpaper.IsSafetyStopped) return;
        timer.Stop();
        desktopOperation = true; UpdateControls();
        try { await Task.Run(rotation.Next); ShowCurrent(); timer.Start(); }
        catch (Exception ex) { HandleDesktopFailure(ex); }
        finally { FinishDesktopOperation(); }
    }
    private async Task<bool> StopRotationAsync()
    {
        if (desktopOperation) return false;
        session.Cancel(); sessionTimer.Stop();
        timer.Stop();
        desktopOperation = true; UpdateControls();
        try
        {
            await Task.Run(() =>
            {
                if (wallpaper.IsSafetyStopped) wallpaper.Recover();
                rotation.Stop();
                if (wallpaper.HasPendingRestore) wallpaper.Restore();
            });
            ReleaseHotkey(); status.Text = "已停止，并恢复原壁纸。"; tray.Text = "壁纸轮播 · 已停止"; UpdateControls(); return true;
        }
        catch (Exception ex) { HandleDesktopFailure(ex); return false; }
        finally { FinishDesktopOperation(); }
    }
    private void ShowCurrent()
    {
        status.Text = rotation.CurrentPaths.Count > 1
            ? $"正在拼贴 {rotation.CurrentPaths.Count} 张 · 图片 {rotation.Position}–{rotation.Position + rotation.CurrentPaths.Count - 1}/{rotation.Count} · 每 {settings.IntervalSeconds} 秒切换"
            : $"正在播放 {rotation.Position}/{rotation.Count} · {Path.GetFileName(rotation.CurrentPath)} · 每 {settings.IntervalSeconds} 秒切换";
        tray.Text = "壁纸轮播 · 正在运行";
        int selected = ordered.FindIndex(p => p.Path == rotation.CurrentPath);
        if (selected >= 0)
        {
            if (pictures.SelectedIndex == selected) _ = UpdatePreviewAsync();
            else pictures.SelectedIndex = selected;
        }
    }
    private void UpdateControls()
    {
        bool available = !session.Pending && !session.IsUnavailable;
        bool editable = available && !busy && !desktopOperation && !rotation.IsRunning && !rotation.IsPaused;
        foreach (Control control in new Control[] { folder, browse, scan, sort, interval, ctrl, alt, shift, hotkey,
            stopCtrl, stopAlt, stopShift, stopHotkey, up, down, save }) control.Enabled = editable;
        start.Enabled = trayToggle.Enabled = available && !busy && !desktopOperation;
        start.Text = trayToggle.Text = wallpaper.IsSafetyStopped ? "恢复并开始"
            : rotation.IsRunning ? "暂停并恢复" : rotation.IsPaused ? "继续轮播" : "开始轮播";
        next.Enabled = trayNext.Enabled = available && rotation.IsRunning && !desktopOperation;
        stop.Enabled = trayStop.Enabled = available && !desktopOperation && (rotation.IsRunning || rotation.IsPaused || wallpaper.HasPendingRestore || wallpaper.IsSafetyStopped);
        stop.Text = wallpaper.IsSafetyStopped ? "恢复原背景" : "结束轮播";
        fit.Enabled = autoCollage.Enabled = available && !desktopOperation;
        collageCount.Enabled = available && !desktopOperation && settings.AutoCollage;
        stateBadge.Text = desktopOperation ? "● 处理中" : wallpaper.IsSafetyStopped ? "● 安全停止"
            : session.Pending ? "● 等待会话恢复" : rotation.IsRunning ? "● 轮播中" : rotation.IsPaused ? "● 已暂停" : "● 就绪";
        stateBadge.BackColor = rotation.IsRunning ? Palette.Soft : Color.FromArgb(233, 238, 245);
        stateBadge.ForeColor = rotation.IsRunning ? Palette.Accent : Palette.Muted;
        playbackHint.Text = !available ? "等待解锁或唤醒后恢复。" : rotation.IsRunning || rotation.IsPaused
            ? "顺序、间隔和快捷键：结束轮播后可修改。" : "选择随机可打乱顺序；修改后保存设置。";
        shortcutHint.Text = $"开关  {HotkeyText(settings.StopHotkeyModifiers, settings.StopHotkeyKey)}    ·    下一张  {HotkeyText(settings.HotkeyModifiers, settings.HotkeyKey)}";
    }
    private void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void ReleaseHotkey()
    {
        if (hotkeyRegistered) { HotkeyNative.UnregisterHotKey(Handle, NextHotkeyId); hotkeyRegistered = false; }
        if (stopHotkeyRegistered) { HotkeyNative.UnregisterHotKey(Handle, StopHotkeyId); stopHotkeyRegistered = false; }
    }
    private async void HandleClosing(object? sender, FormClosingEventArgs e)
    {
        if (allowClose) return;
        scanCancellation?.Cancel();
        sessionTimer.Stop();
        if (desktopOperation)
        {
            e.Cancel = true; closeRequested = true;
            wallpaper.StopForSafety("退出已取消正在执行的操作，恢复记录已保留。"); return;
        }
        if (wallpaper.IsSafetyStopped || session.Pending || session.IsUnavailable)
        { session.Cancel(); ReleaseHotkey(); allowClose = true; return; }
        e.Cancel = true;
        if (closing) return;
        closing = true;
        await StopRotationAsync();
        ReleaseHotkey();
        allowClose = true; Close();
    }
    private void HandleDesktopFailure(Exception ex)
    {
        if (ex is SessionInterruptedException && !wallpaper.IsSafetyStopped)
        { timer.Stop(); rotation.HoldForSession(); return; }
        session.Cancel(); sessionTimer.Stop();
        timer.Stop(); wallpaper.StopForSafety(ex.Message); rotation.HaltForSafety(); ReleaseHotkey();
        status.Text = "已安全停止 · 未确认恢复原背景。可点击“恢复并开始”重试，或仅恢复原背景。";
        tray.Text = "壁纸轮播 · 安全停止，等待手动恢复";
        if (!suppressNotifications) tray.ShowBalloonTip(8000, "壁纸操作已停止", ex.Message, ToolTipIcon.Warning);
    }
    private void FinishDesktopOperation()
    {
        desktopOperation = false;
        if (IsDisposed) return;
        if (wallpaper.IsSafetyStopped)
        {
            session.Cancel(); sessionTimer.Stop();
            timer.Stop(); rotation.HaltForSafety(); ReleaseHotkey();
            status.Text = "已安全停止 · 可点击“恢复并开始”重试，或仅恢复原背景。";
            tray.Text = "壁纸轮播 · 安全停止，等待手动恢复";
        }
        else if (session.Pending)
        {
            timer.Stop(); rotation.HoldForSession();
            status.Text = session.WantsPlayback ? "会话暂停 · 解锁或唤醒后自动继续当前图片。"
                : "已手动暂停 · 解锁或唤醒后保持原背景，按快捷键继续。";
            QueueSessionResume();
        }
        UpdateControls();
        if (closeRequested) BeginInvoke(Close);
    }
    private void SessionChanged(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case Microsoft.Win32.SessionSwitchReason.SessionLock: PostSessionChange(SessionBlocker.Locked, true); break;
            case Microsoft.Win32.SessionSwitchReason.SessionUnlock: PostSessionChange(SessionBlocker.Locked, false); break;
            case Microsoft.Win32.SessionSwitchReason.RemoteDisconnect:
            case Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect: PostSessionChange(SessionBlocker.Disconnected, true); break;
            case Microsoft.Win32.SessionSwitchReason.RemoteConnect:
            case Microsoft.Win32.SessionSwitchReason.ConsoleConnect: PostSessionChange(SessionBlocker.Disconnected, false); break;
        }
    }
    private void PowerChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Suspend) PostSessionChange(SessionBlocker.Sleeping, true);
        else if (e.Mode == Microsoft.Win32.PowerModes.Resume) PostSessionChange(SessionBlocker.Sleeping, false);
    }
    private void PostSessionChange(SessionBlocker reason, bool suspend)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() => ChangeSession(reason, suspend));
    }
    internal void ChangeSession(SessionBlocker reason, bool suspend)
    {
        if (IsDisposed || closing || closeRequested) return;
        if (suspend)
        {
            session.Suspend(reason); sessionTimer.Stop(); timer.Stop(); readinessChecks = 0;
            if (!wallpaper.IsSafetyStopped) wallpaper.SuspendSession();
            if (!desktopOperation) rotation.HoldForSession();
            status.Text = session.WantsPlayback ? "会话暂停 · 解锁或唤醒后自动继续当前图片。"
                : "已暂停 · 解锁或唤醒后保持原背景。";
        }
        else { session.Wake(reason); QueueSessionResume(); }
        if (wallpaper.IsSafetyStopped) session.Cancel();
        UpdateControls();
    }
    private void QueueSessionResume()
    {
        if (session.CanResume && !wallpaper.IsSafetyStopped && !closing && !closeRequested)
            sessionTimer.Start();
    }
    private async Task ResumeSessionAsync()
    {
        sessionTimer.Stop();
        if (!session.CanResume || wallpaper.IsSafetyStopped || closing || closeRequested) return;
        if (desktopOperation || busy) { QueueSessionResume(); return; }
        // Read-only desktop-name checks may wait for Windows to finish unlocking. No wallpaper retries.
        if (!WallpaperNative.IsInteractiveDesktop())
        {
            if (++readinessChecks < 20) { QueueSessionResume(); return; }
            HandleDesktopFailure(new IOException("等待桌面解锁超过 30 秒，已停止自动继续。")); UpdateControls(); return;
        }
        desktopOperation = true; UpdateControls();
        try
        {
            wallpaper.EndSessionHold();
            bool play = session.WantsPlayback;
            int revision = session.Revision;
            await Task.Run(() => rotation.ResumeAfterSession(play, wallpaper.HasPendingRestore));
            if (session.IsUnavailable || revision != session.Revision) return;
            session.Complete();
            if (play) { timer.Start(); ShowCurrent(); }
            else
            {
                status.Text = rotation.IsPaused ? "已保持原背景 · 按开关快捷键继续轮播。" : "就绪 · 点击开始轮播。";
                tray.Text = rotation.IsPaused ? "壁纸轮播 · 已暂停" : "壁纸轮播 · 就绪";
            }
        }
        catch (Exception ex) { HandleDesktopFailure(ex); }
        finally { FinishDesktopOperation(); }
    }
    private void ShowError(string title, Exception ex)
    {
        status.Text = title + "：" + ex.Message;
        if (suppressNotifications) return;
        if (Visible) MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else tray.ShowBalloonTip(5000, title, ex.Message, ToolTipIcon.Warning);
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && !desktopOperation)
        {
            if (m.WParam.ToInt32() == NextHotkeyId && hotkeyRegistered) _ = NextPictureAsync();
            else if (m.WParam.ToInt32() == StopHotkeyId && stopHotkeyRegistered) _ = ToggleRotationAsync();
        }
        base.WndProc(ref m);
    }
    private static void PopulateHotkeyChoices(ComboBox combo)
    {
        combo.Items.AddRange(new object[] {
            new KeyChoice("→ 右方向", Keys.Right), new KeyChoice("← 左方向", Keys.Left),
            new KeyChoice("Space", Keys.Space), new KeyChoice("End", Keys.End), new KeyChoice("Home", Keys.Home)
        });
        for (int i = 1; i <= 11; i++) combo.Items.Add(new KeyChoice($"F{i}", Keys.F1 + i - 1));
        for (char c = 'A'; c <= 'Z'; c++) combo.Items.Add(new KeyChoice(c.ToString(), (Keys)c));
    }
    private static string HotkeyText(uint modifiers, uint key)
    {
        var parts = new List<string>();
        if ((modifiers & 2) != 0) parts.Add("Ctrl");
        if ((modifiers & 1) != 0) parts.Add("Alt");
        if ((modifiers & 4) != 0) parts.Add("Shift");
        parts.Add(key == (uint)Keys.Left ? "←" : key == (uint)Keys.Right ? "→" : ((Keys)key).ToString());
        return string.Join(" + ", parts);
    }
    private static Button MakeButton(string text, int width) => new ModernButton()
    { Text = text, Width = width, Height = 40, Margin = new Padding(0, 0, 8, 0) };
    private static Label LabelOf(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 7, 12, 0) };
    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0, 3, 0, 0) };
        foreach (var label in controls.OfType<Label>().Where(label => label.Dock == DockStyle.Fill))
        { label.Dock = DockStyle.None; label.AutoSize = true; label.Margin = new Padding(0, 7, 8, 0); }
        panel.Controls.AddRange(controls); return panel;
    }
    private sealed record KeyChoice(string Label, Keys Key) { public override string ToString() => Label; }
}
