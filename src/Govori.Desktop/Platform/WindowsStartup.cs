using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Govori;

[SupportedOSPlatform("windows")]
internal static class WindowsStartup
{
    internal const string ShortcutName = "Govori.lnk";
    internal sealed record Shortcut(string Target, string Arguments, string WorkingDirectory);

    internal static void Configure(bool enabled, string executable, string? startupDirectory = null)
    {
        string directory = startupDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        string path = Path.Combine(directory, ShortcutName);
        if (!enabled) { File.Delete(path); return; }
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("Govori executable not found.", executable);
        var expected = new Shortcut(executable, "--tray", Path.GetDirectoryName(executable)!);
        if (File.Exists(path) && Read(path) == expected) return;
        Directory.CreateDirectory(directory);
        WithShortcut(path, link =>
        {
            link.TargetPath = expected.Target;
            link.Arguments = expected.Arguments;
            link.WorkingDirectory = expected.WorkingDirectory;
            link.Description = "Govori";
            link.IconLocation = executable + ",0";
            link.Save();
        });
    }

    internal static Shortcut Read(string path)
    {
        Shortcut? result = null;
        WithShortcut(path, link => result = new Shortcut((string)link.TargetPath, (string)link.Arguments, (string)link.WorkingDirectory));
        return result!;
    }

    private static void WithShortcut(string path, Action<dynamic> action)
    {
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
        object? shortcut = null;
        try
        {
            shortcut = ((dynamic)shell).CreateShortcut(path);
            action(shortcut);
        }
        finally
        {
            if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
