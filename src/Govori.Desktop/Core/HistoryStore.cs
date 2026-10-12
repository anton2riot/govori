using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Govori;

public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string Text { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string Error { get; set; } = "";
    public string AudioPath { get; set; } = "";
    public string Target { get; set; } = "";
    public double Duration { get; set; }
    public double Latency { get; set; }
    [JsonIgnore] public string TimeLabel => CreatedAt.ToLocalTime().ToString("dd MMM · HH:mm", Localization.Culture);
    [JsonIgnore] public string StatusLabel => Localization.Catalog("en").ContainsKey("History." + Status) ? Localization.T("History." + Status) : Localization.TranslateKnown(Status);
    [JsonIgnore] public string DetailLabel => Localization.T("HistoryDetail", TimeLabel, Duration, StatusLabel) + (Latency > 0 ? Localization.T("GeminiLatency", Latency) : "");
    [JsonIgnore] public string DisplayText => string.IsNullOrEmpty(Text) ? (string.IsNullOrEmpty(Error) ? Localization.T("SavedAudioHint") : Localization.TranslateKnown(Error)) : Text;
    [JsonIgnore] public string ShortDetails => Localization.T("Duration", Duration) + (Latency > 0 ? Localization.T("GeminiLatency", Latency) : "") + (CanRetry ? " · " + Localization.T("Retry") : "");
    [JsonIgnore] public bool CanRetry => !string.IsNullOrEmpty(AudioPath) && File.Exists(AudioPath);
}

public sealed class HistoryStore
{
    private readonly string path;
    public List<HistoryEntry> Entries { get; private set; } = [];
    public event Action? Changed;
    public HistoryStore(string? filePath = null)
    {
        path = filePath ?? Path.Combine(Configuration.DataDirectory, "history.json");
        if (!File.Exists(path)) return;
        try { Entries = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(path)) ?? []; }
        catch (JsonException) { File.Copy(path, path + ".damaged", true); }
        foreach (var entry in Entries)
        {
            entry.Status = Localization.NormalizeStatus(entry.Status);
            if (entry.Status is "transcribing" or "pending") entry.Status = "interrupted";
        }
    }
    public void Add(HistoryEntry entry) { Entries.Insert(0, entry); Save(); }
    public void Save()
    {
        Configuration.AtomicWrite(path, JsonSerializer.Serialize(Entries, new JsonSerializerOptions { WriteIndented = true }));
        Changed?.Invoke();
    }
    public void Delete(HistoryEntry entry)
    {
        // Only delete audio that belongs to this application's recordings folder.
        if (entry.CanRetry && Path.GetFullPath(entry.AudioPath).StartsWith(Path.GetFullPath(Configuration.AudioDirectory) + Path.DirectorySeparatorChar, (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) File.Delete(entry.AudioPath);
        Entries.Remove(entry);
        Save();
    }
}
