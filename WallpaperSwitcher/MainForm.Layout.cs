namespace WallpaperSwitcher;

public sealed partial class MainForm
{
    private readonly ComboBox fit = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Label fitHint = new() { Dock = DockStyle.Fill, ForeColor = Palette.Muted };
    private readonly WallpaperPreview preview = new() { Dock = DockStyle.Fill };
    private readonly Label previewName = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "等待选择图片" };
    private readonly Label previewInfo = new() { Dock = DockStyle.Fill, ForeColor = Palette.Muted, AutoEllipsis = true, Text = "预览不会修改桌面壁纸" };
    private readonly Label stateBadge = new() { AutoSize = false, Width = 130, Height = 30, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Label playbackHint = new() { Dock = DockStyle.Fill, ForeColor = Palette.Muted, AutoEllipsis = true };
    private readonly Label shortcutHint = new() { Dock = DockStyle.Fill, ForeColor = Palette.Muted, AutoEllipsis = true };
    private Font? listDetailFont;
    private int previewRequest;
    private readonly SemaphoreSlim previewGate = new(1, 1);
    private bool previewSuspended = true;
    private bool changingFit;
    private bool changingCollage;
    private bool previewIsCollage;
    private readonly CheckBox autoCollage = new() { Text = "竖图拼贴", AutoSize = true, BackColor = Color.White };
    private readonly ComboBox collageCount = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "每次拼贴张数" };

    private PictureBox BuildLayout()
    {
        SuspendLayout();
        Text = "壁纸轮播";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 10F);
        ForeColor = Palette.Ink;
        ClientSize = new Size(1120, 820);
        MinimumSize = new Size(1020, 780);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Palette.Canvas;
        Icon = AppBrand.CreateIcon();
        DoubleBuffered = true;

        var root = Grid(1, [82, 100, -1, 78]);
        root.Padding = new Padding(24, 18, 24, 18);
        root.BackColor = Palette.Canvas;
        Controls.Add(root);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
        header.ColumnStyles.Add(new(SizeType.Absolute, 60)); header.ColumnStyles.Add(new(SizeType.Percent, 100)); header.ColumnStyles.Add(new(SizeType.Absolute, 270));
        var logo = new PictureBox { Image = AppBrand.CreateLogo(), SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(48, 48), Margin = new Padding(0, 7, 0, 0) };
        header.Controls.Add(logo, 0, 0);
        var titles = Grid(1, [36, 26]);
        titles.BackColor = Palette.Canvas;
        titles.Controls.Add(TextLabel("壁纸轮播", 23, true), 0, 0);
        titles.Controls.Add(TextLabel("让喜欢的风景，轮流出现在桌面。", 9, false, Palette.Muted), 0, 1);
        header.Controls.Add(titles, 1, 0);
        var hide = MakeButton("收起到托盘", 120);
        hide.Click += (_, _) => Hide();
        var headerActions = Row(stateBadge, hide); headerActions.BackColor = Palette.Canvas;
        headerActions.Padding = new Padding(0, 12, 0, 0);
        header.Controls.Add(headerActions, 2, 0);
        root.Controls.Add(header, 0, 0);

        var source = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 12, 18, 12), Margin = new Padding(0, 0, 0, 14) };
        var sourceGrid = Grid(1, [25, -1]);
        sourceGrid.Controls.Add(TextLabel("图片来源   /   自动包含所有子目录", 9, true, Palette.Muted), 0, 0);
        var folderRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty, BackColor = Color.White };
        folderRow.ColumnStyles.Add(new(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new(SizeType.Absolute, 114)); folderRow.ColumnStyles.Add(new(SizeType.Absolute, 104));
        folder.BorderStyle = BorderStyle.FixedSingle; folder.Margin = new Padding(0, 3, 14, 0); folder.AccessibleName = "图片目录";
        browse.Width = 106; browse.Height = 34; scan.Width = 96; scan.Height = 34;
        folderRow.Controls.Add(folder, 0, 0); folderRow.Controls.Add(browse, 1, 0); folderRow.Controls.Add(scan, 2, 0);
        sourceGrid.Controls.Add(folderRow, 0, 1); source.Controls.Add(sourceGrid); root.Controls.Add(source, 0, 1);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty, BackColor = Palette.Canvas };
        body.ColumnStyles.Add(new(SizeType.Percent, 59)); body.ColumnStyles.Add(new(SizeType.Percent, 41));
        root.Controls.Add(body, 0, 2);
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = new Padding(0, 0, 16, 0), BackColor = Palette.Canvas };
        left.RowStyles.Add(new(SizeType.Percent, 63)); left.RowStyles.Add(new(SizeType.Percent, 37));
        body.Controls.Add(left, 0, 0);
        var previewCard = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 14), Padding = new Padding(18, 14, 18, 14) };
        var previewGrid = Grid(1, [28, -1, 26, 21]);
        previewGrid.Controls.Add(TextLabel("桌面预览", 11, true), 0, 0);
        preview.Margin = new Padding(0, 0, 0, 8);
        previewGrid.Controls.Add(preview, 0, 1);
        previewName.Font = new Font(Font.FontFamily, 10, FontStyle.Bold);
        previewInfo.Font = new Font(Font.FontFamily, 8.5F);
        previewGrid.Controls.Add(previewName, 0, 2); previewGrid.Controls.Add(previewInfo, 0, 3);
        previewCard.Controls.Add(previewGrid); left.Controls.Add(previewCard, 0, 0);
        var library = new CardPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(18, 12, 18, 12) };
        var libraryGrid = Grid(1, [29, -1, 37]);
        count.ForeColor = Palette.Ink; count.Font = new Font(Font.FontFamily, 10, FontStyle.Bold);
        libraryGrid.Controls.Add(count, 0, 0);
        pictures.BorderStyle = BorderStyle.None; pictures.BackColor = Color.White;
        pictures.DrawMode = DrawMode.OwnerDrawFixed; pictures.ItemHeight = 50; pictures.AccessibleName = "播放列表";
        listDetailFont = new Font(Font.FontFamily, 8.5F);
        pictures.DrawItem += DrawPictureRow;
        pictures.SelectedIndexChanged += async (_, _) => await UpdatePreviewAsync();
        libraryGrid.Controls.Add(pictures, 0, 1);
        up.Width = 80; down.Width = 80; up.Height = down.Height = 30;
        var reorder = Row(up, down, TextLabel("上下移动以自定义顺序", 8.5F, false, Palette.Muted));
        libraryGrid.Controls.Add(reorder, 0, 2); library.Controls.Add(libraryGrid); left.Controls.Add(library, 0, 1);

        var preferences = new CardPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(18, 14, 18, 14) };
        body.Controls.Add(preferences, 1, 0);
        var preferencesGrid = Grid(1, [44, -1, 44]);
        var playbackTab = MakeButton("播放设置", 124); var generalTab = MakeButton("软件设置", 124);
        ((ModernButton)playbackTab).Primary = true;
        preferencesGrid.Controls.Add(Row(playbackTab, generalTab), 0, 0);
        var pages = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = Padding.Empty };
        var playback = BuildPlaybackPage();
        var general = BuildGeneralPage();
        pages.Controls.Add(general); pages.Controls.Add(playback); general.Visible = false;
        playbackTab.Click += (_, _) => { general.Visible = false; playback.Visible = true; ((ModernButton)playbackTab).Primary = true; ((ModernButton)generalTab).Primary = false; playbackTab.Invalidate(); generalTab.Invalidate(); };
        generalTab.Click += (_, _) => { playback.Visible = false; general.Visible = true; ((ModernButton)generalTab).Primary = true; ((ModernButton)playbackTab).Primary = false; playbackTab.Invalidate(); generalTab.Invalidate(); };
        preferencesGrid.Controls.Add(pages, 0, 1);
        save.Dock = DockStyle.Fill; ((ModernButton)save).Primary = true; save.Text = "保存播放与快捷键设置";
        preferencesGrid.Controls.Add(save, 0, 2);
        preferences.Controls.Add(preferencesGrid);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 14, 0, 0), BackColor = Palette.Canvas };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.Absolute, 342));
        status.Font = new Font(Font.FontFamily, 9); status.ForeColor = Palette.Ink; status.Padding = Padding.Empty;
        var statusArea = Grid(1, [27, -1]); statusArea.BackColor = Palette.Canvas;
        shortcutHint.Font = new Font(Font.FontFamily, 8.5F);
        statusArea.Controls.Add(status, 0, 0); statusArea.Controls.Add(shortcutHint, 0, 1);
        footer.Controls.Add(statusArea, 0, 0);
        start.Width = 124; ((ModernButton)start).Primary = true;
        next.Width = 94; stop.Width = 100;
        var transport = Row(start, next, stop); transport.BackColor = Palette.Canvas;
        footer.Controls.Add(transport, 1, 0); root.Controls.Add(footer, 0, 3);

        foreach (var combo in new[] { sort, hotkey, stopHotkey, fit, collageCount })
        { combo.FlatStyle = FlatStyle.Flat; combo.BackColor = Palette.Canvas; combo.ForeColor = Palette.Ink; combo.Margin = new Padding(0, 3, 0, 8); }
        foreach (var check in new[] { ctrl, alt, shift, stopCtrl, stopAlt, stopShift, launchAtLogon, startInTray })
        { check.BackColor = Color.White; check.ForeColor = Palette.Ink; check.Margin = new Padding(0, 5, 12, 0); }
        ResumeLayout(true);
        return logo;
    }

    private Control BuildPlaybackPage()
    {
        var page = Grid(1, [26, 38, 26, 40, 10, 26, 38, 38, 30, 38, 44, -1]);
        page.Padding = new Padding(0, 16, 0, 6);
        page.Controls.Add(TextLabel("播放顺序 · 名称 / 日期 / 随机 / 自定义", 9, true, Palette.Muted), 0, 0);
        sort.Dock = DockStyle.Fill;
        sort.AccessibleName = "播放顺序（包含随机模式）";
        sort.Items.AddRange(["名称 · 从小到大", "名称 · 从大到小", "修改时间 · 从旧到新", "修改时间 · 从新到旧", "随机 · 每轮不重复", "自定义 · 按列表顺序"]);
        page.Controls.Add(sort, 0, 1);
        page.Controls.Add(TextLabel("切换间隔", 9, true, Palette.Muted), 0, 2);
        interval.Width = 112; interval.BorderStyle = BorderStyle.FixedSingle;
        page.Controls.Add(Row(interval, TextLabel("秒   /   300 秒 = 5 分钟", 9, false, Palette.Muted)), 0, 3);
        page.Controls.Add(TextLabel("画面适配", 9, true, Palette.Muted), 0, 5);
        fit.Items.AddRange(new object[] {
            new FitChoice("填充 · 铺满屏幕", WallpaperFit.Fill, "保持比例铺满屏幕，多出的边缘会被裁剪。"),
            new FitChoice("适应 · 完整显示", WallpaperFit.Fit, "保持比例，完整显示图片；比例不一致时会留黑边。"),
            new FitChoice("拉伸 · 拉满画面", WallpaperFit.Stretch, "拉伸到屏幕尺寸，不留边，但图片可能变形。"),
            new FitChoice("居中 · 原始尺寸", WallpaperFit.Center, "不缩放，按原始像素尺寸居中；大图会被裁切。"),
            new FitChoice("平铺 · 重复图案", WallpaperFit.Tile, "按原始尺寸重复铺满，适合纹理、小图案。"),
            new FitChoice("跨越 · 多屏全景", WallpaperFit.Span, "一张图片跨越所有显示器；预览按整个桌面比例模拟。")
        });
        page.Controls.Add(fit, 0, 6); fitHint.Font = new Font(Font.FontFamily, 9); page.Controls.Add(fitHint, 0, 7);
        page.Controls.Add(autoCollage, 0, 8);
        collageCount.Items.Add("自动 · 按屏幕比例选择张数");
        for (int i = 2; i <= CollagePlanner.MaxPictures; i++) collageCount.Items.Add($"固定 {i} 张 · 相邻竖图");
        page.Controls.Add(collageCount, 0, 9);
        page.Controls.Add(TextLabel("等比例裁切铺满；不足时显示剩余张数。\n拼贴设置下次切换生效，横图按适配模式显示。", 8.5F, false, Palette.Muted), 0, 10);
        playbackHint.Font = new Font(Font.FontFamily, 8.5F);
        page.Controls.Add(playbackHint, 0, 11);
        fit.SelectedIndexChanged += async (_, _) => await ChangeFitModeAsync();
        return page;
    }

    private Control BuildGeneralPage()
    {
        var page = Grid(1, [27, 43, 27, 43, 20, 28, 34, 48, 34, -1]);
        page.Padding = new Padding(0, 16, 0, 6);
        page.Controls.Add(TextLabel("下一张快捷键", 9, true, Palette.Muted), 0, 0);
        PopulateHotkeyChoices(hotkey); PopulateHotkeyChoices(stopHotkey);
        hotkey.Width = stopHotkey.Width = 100;
        page.Controls.Add(Row(ctrl, alt, shift, hotkey), 0, 1);
        page.Controls.Add(TextLabel("壁纸开关快捷键", 9, true, Palette.Muted), 0, 2);
        page.Controls.Add(Row(stopCtrl, stopAlt, stopShift, stopHotkey), 0, 3);
        page.Controls.Add(TextLabel("启动与托盘", 10, true), 0, 5);
        launchAtLogon.Text = "开机自启动";
        page.Controls.Add(launchAtLogon, 0, 6);
        page.Controls.Add(TextLabel("由你手动开启。登录 Windows 后\n在托盘自动开始轮播。", 9, false, Palette.Muted), 0, 7);
        startInTray.Text = "手动打开时，也在托盘自动轮播";
        page.Controls.Add(startInTray, 0, 8);
        page.Controls.Add(TextLabel("正常退出会恢复原背景。\n异常停止后，请手动点击“恢复原背景”。", 9, false, Palette.Muted), 0, 9);
        return page;
    }

    private static TableLayoutPanel Grid(int columns, int[] heights)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = heights.Length, BackColor = Color.White, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (int height in heights) grid.RowStyles.Add(new(height < 0 ? SizeType.Percent : SizeType.Absolute, height < 0 ? 100 : height));
        return grid;
    }
    private static Label TextLabel(string text, float size, bool bold = false, Color? color = null) => new()
    {
        Text = text, AutoSize = false, Dock = DockStyle.Fill, BackColor = Color.Transparent,
        Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        ForeColor = color ?? Palette.Ink, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty
    };
    private void DrawPictureRow(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var fill = new SolidBrush(selected ? Palette.Soft : Color.White);
        e.Graphics.FillRectangle(fill, e.Bounds);
        string relative = pictures.Items[e.Index]?.ToString() ?? "";
        TextRenderer.DrawText(e.Graphics, (e.Index + 1).ToString("D2"), pictures.Font,
            new Rectangle(e.Bounds.X + 6, e.Bounds.Y, 32, e.Bounds.Height), Palette.Muted, TextFormatFlags.VerticalCenter);
        var name = new Rectangle(e.Bounds.X + 44, e.Bounds.Y + 3, e.Bounds.Width - 54, e.Bounds.Height / 2);
        TextRenderer.DrawText(e.Graphics, Path.GetFileName(relative), pictures.Font, name, selected ? Palette.Accent : Palette.Ink, TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, Path.GetDirectoryName(relative) is { Length: > 0 } dir ? dir : "根目录", listDetailFont ?? pictures.Font,
            new Rectangle(name.X, e.Bounds.Y + e.Bounds.Height / 2, name.Width, e.Bounds.Height / 2), Palette.Muted, TextFormatFlags.EndEllipsis);
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }

    private void RefreshPreviewVisibility()
    {
        bool suspended = !Visible || WindowState == FormWindowState.Minimized;
        if (suspended == previewSuspended) return;
        previewSuspended = suspended;
        if (!suspended) { _ = UpdatePreviewAsync(); return; }
        ++previewRequest;
        preview.Picture?.Dispose(); preview.Picture = null;
    }

    private async Task UpdatePreviewAsync()
    {
        int request = ++previewRequest;
        if (!Visible || WindowState == FormWindowState.Minimized || IsDisposed) return;
        int selected = pictures.SelectedIndex;
        if (selected < 0 || selected >= ordered.Count) return;
        string path = ordered[selected].Path;
        var candidates = ordered.Skip(selected).Take(CollagePlanner.MaxPictures).Select(p => p.Path).ToArray();
        var currentFrame = (rotation.IsRunning || rotation.IsPaused) && path == rotation.CurrentPath
            ? rotation.CurrentPaths.ToArray() : null;
        var target = WindowsWallpaper.TargetSize(settings.FitMode);
        var mode = settings.FitMode;
        bool collageEnabled = settings.AutoCollage;
        int requestedCount = settings.CollageCount;
        // Coalesce rapid selection changes; never decode multiple full-sized previews concurrently.
        await Task.Delay(120);
        if (IsDisposed || request != previewRequest) return;
        await previewGate.WaitAsync();
        try
        {
            if (IsDisposed || request != previewRequest) return;
            var result = await Task.Run(() =>
            {
                var frame = currentFrame ?? CollageRenderer.PlanFrame(candidates, target, collageEnabled, requestedCount).ToArray();
                if (frame.Length > 1)
                    return (bitmap: CollageRenderer.Render(frame, target, 960), original: target, frame);
                using var source = CollageRenderer.LoadImage(path);
                var original = source.Size;
                double scale = Math.Min(1, 960.0 / Math.Max(source.Width, source.Height));
                var bitmap = new Bitmap(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(source, 0, 0, bitmap.Width, bitmap.Height);
                }
                return (bitmap, original, frame);
            });
            if (IsDisposed || request != previewRequest) { result.bitmap.Dispose(); return; }
            previewIsCollage = result.frame.Length > 1;
            preview.FitMode = previewIsCollage && mode != WallpaperFit.Span ? WallpaperFit.Fill : mode;
            preview.Picture?.Dispose(); preview.Picture = result.bitmap; preview.OriginalSize = result.original; preview.Invalidate();
            previewName.Text = previewIsCollage ? $"{result.frame.Length} 张竖图拼贴 · {string.Join("  /  ", result.frame.Select(Path.GetFileName))}" : Path.GetFileName(path);
            var screen = Screen.PrimaryScreen?.Bounds.Size ?? new Size(1920, 1080);
            previewInfo.Text = previewIsCollage ? $"拼贴画布 {target.Width} × {target.Height}   /   保持比例 · 无留边"
                : $"图片 {result.original.Width} × {result.original.Height}   /   主屏 {screen.Width} × {screen.Height}";
        }
        catch
        {
            if (IsDisposed || request != previewRequest) return;
            preview.Picture?.Dispose(); preview.Picture = null; preview.Invalidate();
            previewIsCollage = false;
            previewName.Text = "无法预览此图片"; previewInfo.Text = "文件可能已移动，或格式不受支持。";
        }
        finally { previewGate.Release(); }
    }

    private async Task ChangeFitModeAsync()
    {
        if (changingFit || desktopOperation || fit.SelectedItem is not FitChoice choice) return;
        var previous = settings.FitMode;
        bool updateDesktop = rotation.IsRunning;
        desktopOperation = true; UpdateControls();
        if (updateDesktop) timer.Stop();
        try
        {
            await Task.Run(() => wallpaper.SetFitMode(choice.Mode, updateDesktop));
            settings.FitMode = choice.Mode;
            SettingsStore.Save(settings);
            preview.FitMode = previewIsCollage && choice.Mode != WallpaperFit.Span ? WallpaperFit.Fill : choice.Mode;
            preview.Invalidate(); fitHint.Text = choice.Description;
            _ = UpdatePreviewAsync();
            if (updateDesktop) timer.Start();
        }
        catch (Exception ex)
        {
            settings.FitMode = previous;
            wallpaper.SetFitMode(previous);
            SetFitSelection(previous);
            HandleDesktopFailure(ex);
        }
        finally { FinishDesktopOperation(); }
    }

    private void SetFitSelection(WallpaperFit mode)
    {
        changingFit = true;
        for (int i = 0; i < fit.Items.Count; i++)
            if (((FitChoice)fit.Items[i]!).Mode == mode) fit.SelectedIndex = i;
        changingFit = false;
        preview.FitMode = previewIsCollage && mode != WallpaperFit.Span ? WallpaperFit.Fill : mode;
        fitHint.Text = ((FitChoice)fit.SelectedItem!).Description;
        preview.Invalidate();
    }
    private sealed record FitChoice(string Name, WallpaperFit Mode, string Description)
    { public override string ToString() => Name; }

    private async Task ChangeAutoCollageAsync()
    {
        if (changingCollage) return;
        bool previous = settings.AutoCollage;
        int previousCount = settings.CollageCount;
        try
        {
            settings.AutoCollage = autoCollage.Checked;
            settings.CollageCount = collageCount.SelectedIndex <= 0 ? 0 : collageCount.SelectedIndex + 1;
            SettingsStore.Save(settings);
            wallpaper.AutoCollage = settings.AutoCollage;
            wallpaper.CollageCount = settings.CollageCount;
            collageCount.Enabled = settings.AutoCollage;
            status.Text = settings.AutoCollage ? "拼贴张数已保存，下次切换生效。" : "竖图拼贴已关闭，下次切换生效。";
            await UpdatePreviewAsync();
        }
        catch (Exception ex)
        {
            settings.AutoCollage = previous; wallpaper.AutoCollage = previous;
            settings.CollageCount = previousCount; wallpaper.CollageCount = previousCount;
            changingCollage = true;
            autoCollage.Checked = previous; collageCount.Enabled = previous;
            collageCount.SelectedIndex = previousCount == 0 ? 0 : previousCount - 1;
            changingCollage = false;
            ShowError("无法保存拼贴设置", ex);
        }
    }
}
