using System.Security.Cryptography;
using System.Text;

namespace Govori;

internal static class ApiKeyStore
{
    internal static string FilePath => PathFor(Providers.Gemini);
    internal static string PathFor(string provider) => Path.Combine(Configuration.DataDirectory, provider == Providers.LiteLlm ? "litellm-key.bin" : "gemini-key.bin");
    internal static string Read(string provider = Providers.Gemini)
    {
        if (!File.Exists(PathFor(provider))) return "";
        byte[] bytes = File.ReadAllBytes(PathFor(provider));
        if (OperatingSystem.IsWindows()) bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    internal static void Save(string key, string provider = Providers.Gemini)
    {
        key = key.Trim();
        if (key.Length == 0 || key.Any(char.IsWhiteSpace)) throw new FormatException(Localization.T("InvalidApiKey"));
        byte[] bytes = Encoding.UTF8.GetBytes(key);
        if (OperatingSystem.IsWindows()) bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Configuration.DataDirectory);
        string temporary = PathFor(provider) + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                stream.Write(bytes);
            }
            File.Move(temporary, PathFor(provider), true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void Delete(string provider = Providers.Gemini) => File.Delete(PathFor(provider));
}
