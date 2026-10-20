using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Govori;

public static class Providers
{
    public const string Gemini = "gemini", LiteLlm = "litellm";
    public static bool IsSupported(string provider) => provider is Gemini or LiteLlm;
}

public sealed record AudioModel(string Id, bool TranscriptionEndpoint = false, ModelPricing? Pricing = null)
{
    public override string ToString() => Id;
}

public sealed class TranscriptionClient : IDisposable
{
    private readonly GeminiClient gemini;
    private readonly HttpClient http;
    private readonly Dictionary<(string, string, string), (DateTime Time, IReadOnlyList<AudioModel> Models)> catalog = [];
    internal string? CatalogWarning { get; private set; }
    public static IReadOnlyList<AudioModel> GeminiModels { get; } = new[] { "gemini-2.5-flash", "gemini-2.5-flash-lite", "gemini-2.5-pro" }.Select(id => new AudioModel(id)).ToArray();
    public TranscriptionClient(HttpMessageHandler? handler = null)
    {
        gemini = new GeminiClient(handler);
        http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(90);
    }
    internal static bool IsGeminiAudioModel(string id) => Regex.IsMatch(id, @"^gemini-(?:2\.[05]|3(?:\.\d+)?)-(?:flash|pro)(?:$|[.-])", RegexOptions.IgnoreCase)
        && !Regex.IsMatch(id, "image|tts|live|native-audio|embedding", RegexOptions.IgnoreCase);
    internal static Uri BaseUri(string address)
    {
        if (!Uri.TryCreate(address.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new FormatException(Localization.T("InvalidServerUrl"));
        return uri;
    }
    private static Uri Endpoint(string address, string path)
    {
        var uri = BaseUri(address);
        // Accept either the proxy root or its OpenAI-compatible /v1 base URL.
        return new Uri(uri.AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? uri : new Uri(uri, "v1/"), path);
    }
    public async Task<IReadOnlyList<AudioModel>> GetModelsAsync(Settings settings, string key, CancellationToken cancellation, bool refresh = false)
    {
        CatalogWarning = null;
        if (settings.Provider == Providers.Gemini && string.IsNullOrWhiteSpace(key)) return GeminiModels;
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException(Localization.T("ApiKeyNotFound"));
        var cacheKey = (settings.Provider, settings.LiteLlmBaseUrl, key);
        if (!refresh && catalog.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.Time < TimeSpan.FromMinutes(5)) return cached.Models;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        IReadOnlyList<AudioModel> models;
        if (settings.Provider == Providers.Gemini)
        {
            var found = new List<AudioModel>();
            string page = "";
            do
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000" + (page.Length > 0 ? "&pageToken=" + Uri.EscapeDataString(page) : ""));
                request.Headers.Add("x-goog-api-key", key);
                using var response = await http.SendAsync(request, timeout.Token);
                await EnsureSuccessAsync(response, timeout.Token);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                if (document.RootElement.TryGetProperty("models", out var entries))
                    foreach (var entry in entries.EnumerateArray())
                    {
                        var id = entry.GetProperty("name").GetString()!.Replace("models/", "");
                        if (IsGeminiAudioModel(id) && entry.TryGetProperty("supportedGenerationMethods", out var methods) && methods.EnumerateArray().Any(x => x.GetString() == "generateContent")) found.Add(new(id));
                    }
                page = document.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() ?? "" : "";
            } while (page.Length > 0);
            models = found.DistinctBy(x => x.Id).OrderBy(x => x.Id).ToArray();
        }
        else
        {
            string? available;
            try { available = await GetJsonAsync(Endpoint(settings.LiteLlmBaseUrl, "models"), key, timeout.Token); }
            catch (BudgetExceededException) { available = null; CatalogWarning = Localization.T("ServiceBudgetExceeded"); }
            string info = await GetJsonAsync(Endpoint(settings.LiteLlmBaseUrl, "model/info"), key, timeout.Token);
            models = ParseLiteLlmModels(available, info);
        }
        if (models.Count == 0) throw new InvalidOperationException(Localization.T("NoAudioModels"));
        catalog[cacheKey] = (DateTime.UtcNow, models);
        return models;
    }
    internal static IReadOnlyList<AudioModel> ParseLiteLlmModels(string? availableJson, string infoJson)
    {
        using var available = availableJson == null ? null : JsonDocument.Parse(availableJson);
        var ids = available?.RootElement.GetProperty("data").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToHashSet();
        using var info = JsonDocument.Parse(infoJson);
        var result = new List<AudioModel>();
        foreach (var entry in info.RootElement.GetProperty("data").EnumerateArray())
        {
            string? id = entry.GetProperty("model_name").GetString();
            if (id == null || ids != null && !ids.Contains(id) || !entry.TryGetProperty("model_info", out var metadata)) continue;
            string? mode = metadata.TryGetProperty("mode", out var value) ? value.GetString() : null;
            bool transcription = mode == "audio_transcription";
            bool audioChat = mode == "chat" && metadata.TryGetProperty("supports_audio_input", out var audio) && audio.ValueKind == JsonValueKind.True;
            string endpoint = transcription ? "/audio/transcriptions" : "/chat/completions";
            if ((transcription || audioChat) && (!metadata.TryGetProperty("supported_endpoints", out var endpoints) || endpoints.ValueKind == JsonValueKind.Null
                || endpoints.ValueKind == JsonValueKind.Array && endpoints.EnumerateArray().Any(x => x.GetString()?.EndsWith(endpoint, StringComparison.Ordinal) == true)))
                result.Add(new(id, transcription, RequestPricing.Model(metadata)));
        }
        return result.DistinctBy(x => x.Id).OrderBy(x => x.Id).ToArray();
    }
    private async Task<string> GetJsonAsync(Uri uri, string key, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await http.SendAsync(request, cancellation);
        await EnsureSuccessAsync(response, cancellation);
        return await response.Content.ReadAsStringAsync(cancellation);
    }
    public async Task<string> TranscribeAsync(string audioPath, Settings settings, string key, CancellationToken cancellation, Action<RequestCharge>? onCharge = null, double duration = 0)
    {
        if (settings.Provider == Providers.Gemini) return await gemini.TranscribeAsync(audioPath, settings, key, cancellation, onCharge, duration);
        var models = await GetModelsAsync(settings, key, cancellation);
        var model = models.FirstOrDefault(x => x.Id == settings.LiteLlmModel) ?? throw new InvalidOperationException(Localization.T("InvalidModel"));
        byte[] audio = await File.ReadAllBytesAsync(audioPath, cancellation);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(settings.LiteLlmBaseUrl, model.TranscriptionEndpoint ? "audio/transcriptions" : "chat/completions"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Content = model.TranscriptionEndpoint ? TranscriptionForm(audio, settings) : new StringContent(JsonSerializer.Serialize(new
            {
                model = model.Id, temperature = 0, max_tokens = 8192,
                messages = new object[] { new { role = "system", content = GeminiClient.TranscriptionPrompt(settings.Vocabulary) }, new { role = "user", content = new[] { new { type = "input_audio", input_audio = new { data = Convert.ToBase64String(audio), format = "wav" } } } } }
            }), Encoding.UTF8, "application/json");
            var charge = new RequestCharge { Provider = settings.Provider, Model = model.Id };
            onCharge?.Invoke(charge);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            RequestPricing.Apply(charge, response, "", model, duration); onCharge?.Invoke(charge);
            string body = await response.Content.ReadAsStringAsync(cancellation);
            RequestPricing.Apply(charge, response, body, model, duration); onCharge?.Invoke(charge);
            if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(response.Headers.RetryAfter?.Delta?.TotalSeconds ?? attempt + 1, 1, 8)), cancellation);
                continue;
            }
            await EnsureSuccessAsync(response, cancellation);
            return ParseResponse(body, model.TranscriptionEndpoint);
        }
        throw new InvalidOperationException(Localization.T("ServiceConnectionError"));
    }
    private static MultipartFormDataContent TranscriptionForm(byte[] audio, Settings settings)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(settings.LiteLlmModel), "model");
        form.Add(new StringContent("json"), "response_format");
        if (!string.IsNullOrWhiteSpace(settings.Vocabulary)) form.Add(new StringContent(settings.Vocabulary), "prompt");
        var file = new ByteArrayContent(audio); file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "recording.wav");
        return form;
    }
    internal static string ParseResponse(string json, bool transcription)
    {
        using var document = JsonDocument.Parse(json);
        if (transcription) return document.RootElement.GetProperty("text").GetString()?.Trim() ?? "";
        if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) throw new InvalidOperationException(Localization.T("ServiceEmpty"));
        var choice = choices[0];
        if (choice.TryGetProperty("finish_reason", out var finish) && finish.GetString() != "stop") throw new InvalidOperationException(Localization.T(finish.GetString() == "length" ? "ServiceTruncated" : "ServiceFailed"));
        var content = choice.GetProperty("message").GetProperty("content");
        return content.ValueKind == JsonValueKind.String ? content.GetString()!.Trim() : "";
    }
    private sealed class BudgetExceededException() : InvalidOperationException(Localization.T("ServiceBudgetExceeded"));
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (response.IsSuccessStatusCode) return;
        if ((int)response.StatusCode == 422)
        {
            try
            {
                using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
                if (error.RootElement.TryGetProperty("error", out var details) && details.ValueKind == JsonValueKind.Object
                    && details.TryGetProperty("type", out var type) && type.GetString() == "budget_exceeded") throw new BudgetExceededException();
            }
            catch (JsonException) { }
        }
        throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Localization.T("ServiceUnauthorized"),
            HttpStatusCode.NotFound => Localization.T("ServiceModelMissing"),
            HttpStatusCode.TooManyRequests => Localization.T("ServiceRateLimit"),
            HttpStatusCode.BadRequest => Localization.T("ServiceBadRequest"),
            _ => Localization.T("ServiceUnavailable", (int)response.StatusCode)
        });
    }
    public void Dispose() { http.Dispose(); gemini.Dispose(); }
}
