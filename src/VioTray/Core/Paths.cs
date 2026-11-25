namespace VioTray.Core;

public static class Paths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VioTray");

    public static string LogDir => Path.Combine(Root, "logs");

    public static string ConfigFile => Path.Combine(Root, "config.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDir);
    }
}
