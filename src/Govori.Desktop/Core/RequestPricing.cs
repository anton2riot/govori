using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace Govori;

public sealed record ModelPricing(decimal? InputToken = null, decimal? OutputToken = null,
    decimal? AudioInputToken = null, decimal? AudioOutputToken = null, decimal? InputSecond = null);

internal static class RequestPricing
{
    internal static decimal? Number(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number >= 0) return number;
        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && number >= 0 ? number : null;
    }
    internal static ModelPricing Model(JsonElement info) => new(Number(info, "input_cost_per_token"), Number(info, "output_cost_per_token"),
        Number(info, "input_cost_per_audio_token"), Number(info, "output_cost_per_audio_token"), Number(info, "input_cost_per_second"));

    internal static void Apply(RequestCharge charge, HttpResponseMessage response, string json, AudioModel model, double duration)
    {
        charge.HttpStatus = (int)response.StatusCode;
        if (response.Headers.TryGetValues("x-litellm-response-cost", out var costs)
            && decimal.TryParse(costs.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var reported) && reported >= 0)
        {
            charge.CostUsd = reported; charge.Source = "litellm";
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (charge.Provider == Providers.Gemini)
            {
                if (response.IsSuccessStatusCode && root.TryGetProperty("usageMetadata", out var usage)) Gemini(charge, usage, duration);
                return;
            }
            if (root.TryGetProperty("_hidden_params", out var hidden) && Number(hidden, "response_cost") is decimal hiddenCost && charge.CostUsd == null)
            { charge.CostUsd = hiddenCost; charge.Source = "litellm"; }
            if (!root.TryGetProperty("usage", out var proxyUsage))
            {
                if (response.IsSuccessStatusCode && charge.CostUsd == null && model.Pricing?.InputSecond is decimal perSecond && duration > 0)
                    Estimate(charge, (decimal)duration * perSecond, "litellm-model-info");
                return;
            }
            charge.InputTokens = (long?)(Number(proxyUsage, "prompt_tokens") ?? Number(proxyUsage, "input_tokens"));
            charge.OutputTokens = (long?)(Number(proxyUsage, "completion_tokens") ?? Number(proxyUsage, "output_tokens"));
            if (Number(proxyUsage, "cost") is decimal usageCost && charge.CostUsd == null)
            { charge.CostUsd = usageCost; charge.Source = "litellm"; }
            if (charge.CostUsd != null || !response.IsSuccessStatusCode || model.Pricing is not { } pricing) return;
            if (pricing.InputSecond is decimal seconds && duration > 0) { Estimate(charge, (decimal)duration * seconds, "litellm-model-info"); return; }
            if (charge.InputTokens is not long input || charge.OutputTokens is not long output) return;
            decimal audioInput = proxyUsage.TryGetProperty("prompt_tokens_details", out var details) ? Number(details, "audio_tokens") ?? 0 : 0;
            // Missing modality counts cannot safely use different text/audio rates.
            if (pricing.AudioInputToken != null && pricing.AudioInputToken != pricing.InputToken && audioInput == 0) return;
            if (pricing.InputToken is not decimal inputRate || pricing.OutputToken is not decimal outputRate) return;
            decimal cached = details.ValueKind == JsonValueKind.Object ? Number(details, "cached_tokens") ?? 0 : 0;
            if (cached > 0) return; // A cache discount is not part of this model's advertised base rates.
            Estimate(charge, Math.Max(0, input - audioInput) * inputRate + audioInput * (pricing.AudioInputToken ?? inputRate) + output * outputRate, "litellm-model-info");
        }
        catch (JsonException) { } // Missing billing metadata must not break transcription.
    }
    private static void Estimate(RequestCharge charge, decimal value, string source)
    { charge.CostUsd = value; charge.Estimated = true; charge.Source = source; }

    private static void Gemini(RequestCharge charge, JsonElement usage, double duration)
    {
        var input = Number(usage, "promptTokenCount");
        if (input == null) return;
        decimal output = (Number(usage, "candidatesTokenCount") ?? 0) + (Number(usage, "thoughtsTokenCount") ?? 0);
        charge.InputTokens = (long)input; charge.OutputTokens = (long)output;
        // Standard paid-tier USD per million tokens. Verified 2026-10-20:
        // https://ai.google.dev/gemini-api/docs/pricing
        bool longPrompt = input > 200_000;
        (decimal text, decimal audio, decimal output)? rates = charge.Model switch
        {
            "gemini-2.5-flash" => (0.30m, 1.00m, 2.50m),
            "gemini-2.5-flash-lite" => (0.10m, 0.30m, 0.40m),
            "gemini-2.5-pro" => longPrompt ? (2.50m, 2.50m, 15m) : (1.25m, 1.25m, 10m),
            "gemini-3-flash-preview" => (0.50m, 1.00m, 3.00m),
            "gemini-3.1-flash-lite" => (0.25m, 0.50m, 1.50m),
            "gemini-3.5-flash-lite" => (0.30m, 0.30m, 2.50m),
            "gemini-3.1-pro-preview" => longPrompt ? (4m, 4m, 18m) : (2m, 2m, 12m),
            "gemini-3.6-flash" or "gemini-3.8-flash" => charge.CreatedAt.UtcDateTime < new DateTime(2027, 1, 1) ? (0.75m, 0.75m, 3.75m) : (1.50m, 1.50m, 7.50m),
            _ => null
        };
        if (rates is not { } rate) return;
        decimal audioTokens = Math.Min(input.Value, (decimal)Math.Max(0, duration) * 32);
        if (usage.TryGetProperty("promptTokensDetails", out var modalities) && modalities.ValueKind == JsonValueKind.Array)
            audioTokens = modalities.EnumerateArray().Where(x => x.TryGetProperty("modality", out var m) && m.GetString() == "AUDIO").Sum(x => Number(x, "tokenCount") ?? 0);
        decimal cached = Number(usage, "cachedContentTokenCount") ?? 0;
        // This app sends no explicit cached content; apply the standard 90% implicit cache discount.
        decimal cachedAudio = 0;
        if (usage.TryGetProperty("cacheTokensDetails", out var cacheDetails) && cacheDetails.ValueKind == JsonValueKind.Array)
            cachedAudio = cacheDetails.EnumerateArray().Where(x => x.TryGetProperty("modality", out var m) && m.GetString() == "AUDIO").Sum(x => Number(x, "tokenCount") ?? 0);
        decimal total = (Math.Max(0, input.Value - audioTokens) * rate.text + audioTokens * rate.audio + output * rate.output
            - (Math.Max(0, cached - cachedAudio) * rate.text + cachedAudio * rate.audio) * 0.9m) / 1_000_000;
        Estimate(charge, Math.Max(0, total), "gemini-standard-2026-10-20");
    }
}
