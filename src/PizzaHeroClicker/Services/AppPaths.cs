using System.IO;

namespace PizzaHeroClicker.Services;

/// <summary>
/// Decides where data lives. Normal mode: %APPDATA%\PizzaHeroClicker. Portable mode: a file
/// named "portable.flag" next to the exe moves profiles, settings and logs next to the exe.
/// Developers can also pass "--data &lt;folder&gt;" to use any folder. The choice is made once at startup.
/// </summary>
public static class AppPaths
{
    public const string AppName = "PizzaHeroClicker";
    public const string PortableFlagName = "portable.flag";

    public static string ExeDir { get; } =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    public static string AppDataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    public static string PortableFlag => Path.Combine(ExeDir, PortableFlagName);

    public static bool IsPortable { get; } = File.Exists(Path.Combine(ExeDir, PortableFlagName));

    public static string Root { get; } = ResolveRoot();

    public static string Profiles => Path.Combine(Root, "profiles");
    public static string Logs => Path.Combine(Root, "logs");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    /// <summary>Where area-watch troubleshooting snapshots go.</summary>
    public static string Debug => Path.Combine(Root, "debug");

    private static string ResolveRoot()
    {
        string[] args = Environment.GetCommandLineArgs();
        int flag = Array.IndexOf(args, "--data");
        if (flag >= 0 && flag + 1 < args.Length && CanWrite(Directory.CreateDirectory(args[flag + 1]).FullName))
            return Path.GetFullPath(args[flag + 1]);

        if (IsPortable && CanWrite(ExeDir)) return ExeDir;
        return AppDataRoot;
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
