using Avalonia;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Govori;

internal static class Program
{
    internal static volatile bool ActivationRequested;
    [STAThread]
    public static int Main(string[] args)
    {
        // NuGet supplies the same recording engine for Windows, Linux and macOS.
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, (name, assembly, path) =>
        {
            if (name != "govori-portaudio") return 0;
            var file = OperatingSystem.IsWindows() ? "portaudio.dll" : OperatingSystem.IsMacOS() ? "libportaudio.dylib" : "libportaudio.so";
            var rid = RuntimeInformation.RuntimeIdentifier;
            foreach (var candidate in new[] { Path.Combine(AppContext.BaseDirectory, file), Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", file), file })
                if (NativeLibrary.TryLoad(candidate, assembly, path, out var handle)) return handle;
            throw new DllNotFoundException(Localization.T("PortAudioMissing"));
        });
        try
        {
            string? installed = RedirectTarget(Environment.ProcessPath!, Path.Combine(Configuration.DefaultDataDirectory, "app", "Govori.CrossPlatform.exe"), args);
            if (installed != null)
            {
                var start = new ProcessStartInfo(installed) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installed)! };
                foreach (string arg in args) start.ArgumentList.Add(arg);
                Process.Start(start)?.Dispose();
                return 0;
            }
            int dataIndex = Array.IndexOf(args, "--data-dir");
            if (dataIndex >= 0 && dataIndex + 1 < args.Length) Configuration.UseDataDirectory(args[dataIndex + 1]);
            Configuration.LoadEnvironment();
            bool pasteTarget = args.Contains("--paste-target");
            using var singleton = pasteTarget ? null : SingleInstance.TryAcquire(Configuration.DataDirectory, () =>
            {
                ActivationRequested = true;
                if (Avalonia.Application.Current is App app) app.OpenFromTray();
            });
            if (!pasteTarget && singleton == null)
            {
                SingleInstance.NotifyAsync(Configuration.DataDirectory, !args.Contains("--tray")).GetAwaiter().GetResult();
                return 0;
            }
            return BuildApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(Configuration.DataDirectory);
            File.WriteAllText(Path.Combine(Configuration.DataDirectory, "startup-error.log"), ex.ToString());
            return 1;
        }
    }

    internal static string? RedirectTarget(string executable, string installed, string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Any(arg => arg is "--verify" or "--preview" or "--paste-target" or "--data-dir" or "--portable")) return null;
        return File.Exists(installed) && !string.Equals(Path.GetFullPath(executable), Path.GetFullPath(installed), StringComparison.OrdinalIgnoreCase) ? installed : null;
    }

    public static AppBuilder BuildApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
