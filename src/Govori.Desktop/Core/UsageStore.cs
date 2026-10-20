using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Govori;

public sealed class RequestCharge
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string HistoryId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public decimal? CostUsd { get; set; }
    public bool Estimated { get; set; }
    public string Source { get; set; } = "";
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public int? HttpStatus { get; set; }
    [JsonIgnore] public string CostLabel => CostUsd is decimal cost ? Money.Format(cost, Estimated) : Localization.T("CostUnknown");
}

public static class Money
{
    public static string Format(decimal value, bool estimated = false) => (estimated ? "≈ " : "") + "$" + value.ToString(value == 0 ? "0.00" : value < 0.01m ? "0.000000" : "0.0000", CultureInfo.InvariantCulture);
}

public sealed record DailySpending(DateOnly Day, decimal CostUsd, int Requests, int Unknown, bool Estimated);

// Separate from transcription history: deleting text must not erase money spent.
public sealed class UsageStore
{
    private readonly string path;
    public List<RequestCharge> Entries { get; private set; } = [];
    public event Action? Changed;
    public UsageStore(string? filePath = null)
    {
        path = filePath ?? Path.Combine(Configuration.DataDirectory, "usage.json");
        if (!File.Exists(path)) return;
        try { Entries = JsonSerializer.Deserialize<List<RequestCharge>>(File.ReadAllText(path)) ?? []; }
        catch (JsonException) { File.Copy(path, path + ".damaged", true); }
    }
    public void Save(RequestCharge charge)
    {
        int index = Entries.FindIndex(x => x.Id == charge.Id);
        if (index < 0) Entries.Add(charge); else Entries[index] = charge;
        Configuration.AtomicWrite(path, JsonSerializer.Serialize(Entries, new JsonSerializerOptions { WriteIndented = true }));
        Changed?.Invoke();
    }
    public IReadOnlyList<DailySpending> Days(TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        return Entries.GroupBy(x => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.CreatedAt, zone).DateTime))
            .OrderByDescending(x => x.Key)
            .Select(x => new DailySpending(x.Key, x.Sum(c => c.CostUsd ?? 0), x.Count(), x.Count(c => c.CostUsd == null), x.Any(c => c.Estimated))).ToArray();
    }
}
