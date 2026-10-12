using Avalonia;
using System.Runtime.InteropServices;

namespace Govori;

internal static class Program
{
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
        try { return BuildApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex)
        {
            Directory.CreateDirectory(Configuration.DataDirectory);
            File.WriteAllText(Path.Combine(Configuration.DataDirectory, "startup-error.log"), ex.ToString());
            return 1;
        }
    }

    public static AppBuilder BuildApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
