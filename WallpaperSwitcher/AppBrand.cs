using System.Reflection;

namespace WallpaperSwitcher;

internal static class AppBrand
{
    public static Icon CreateIcon()
    {
        using var stream = Open("wallpaper.ico");
        using var icon = new Icon(stream, 32, 32);
        return (Icon)icon.Clone();
    }

    public static Image CreateLogo()
    {
        using var stream = Open("logo.png");
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    private static Stream Open(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream("WallpaperSwitcher.Assets." + name)
        ?? throw new InvalidOperationException("缺少程序图标资源：" + name);
}
