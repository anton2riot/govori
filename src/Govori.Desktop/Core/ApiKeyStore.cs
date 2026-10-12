using System.Security.Cryptography;
using System.Text;

namespace Govori;

internal static class ApiKeyStore
{
    internal static string FilePath => Path.Combine(Configuration.DataDirectory, "gemini-key.bin");
    internal static string Read()
    {
        if (!File.Exists(FilePath)) return "";
        byte[] bytes = File.ReadAllBytes(FilePath);
        if (OperatingSystem.IsWindows()) bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    internal static void Save(string key)
    {
        key = key.Trim();
        if (key.Length == 0 || key.Any(char.IsWhiteSpace)) throw new FormatException(Localization.T("InvalidApiKey"));
        byte[] bytes = Encoding.UTF8.GetBytes(key);
        if (OperatingSystem.IsWindows()) bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Configuration.DataDirectory);
        string temporary = FilePath + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                stream.Write(bytes);
            }
            File.Move(temporary, FilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void Delete() => File.Delete(FilePath);
}
