namespace ScreenReader.App;

public static class BrandAssets
{
    public static System.Drawing.Icon CreateTrayIcon()
    {
        var assembly=typeof(BrandAssets).Assembly;
        string name=assembly.GetManifestResourceNames().Single(n=>n.EndsWith("Assets.storyteller.ico",StringComparison.Ordinal));
        using var stream=assembly.GetManifestResourceStream(name)!;
        using var icon=new System.Drawing.Icon(stream,32,32);
        return (System.Drawing.Icon)icon.Clone();
    }
}
