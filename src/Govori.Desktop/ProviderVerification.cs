using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Govori;

internal static partial class Verification
{
    private const string AvailableModels = """
        {"data":[{"id":"speech-alias"},{"id":"whisper-alias"},{"id":"text-only"},{"id":"image-model"},{"id":"output-audio"}]}
        """;
    private const string ModelMetadata = """
        {"data":[
          {"model_name":"speech-alias","model_info":{"mode":"chat","supports_audio_input":true,"supported_endpoints":["/v1/chat/completions"]}},
          {"model_name":"whisper-alias","model_info":{"mode":"audio_transcription","supported_endpoints":["/v1/audio/transcriptions"]}},
          {"model_name":"text-only","model_info":{"mode":"chat","supports_audio_input":false}},
          {"model_name":"image-model","model_info":{"mode":"image_generation","supports_audio_input":true}},
          {"model_name":"output-audio","model_info":{"mode":"chat","supports_audio_output":true}},
          {"model_name":"inaccessible","model_info":{"mode":"chat","supports_audio_input":true}}
        ]}
        """;

    private static async Task ProviderChecks(MainWindow window, DictationController controller, string[] args)
    {
        controller.Cancel();
        var idle = System.Diagnostics.Stopwatch.StartNew();
        while (controller.Busy && idle.Elapsed.TotalSeconds < 5) await Task.Delay(30);
        Check(Configuration.DataDirectory != Configuration.DefaultDataDirectory, "provider checks use an isolated data directory");
        var filtered = TranscriptionClient.ParseLiteLlmModels(AvailableModels, ModelMetadata);
        Check(filtered.Select(x => x.Id).SequenceEqual(new[] { "speech-alias", "whisper-alias" }), "catalog excludes text, images, audio output and models unavailable to this key");
        Check(!filtered[0].TranscriptionEndpoint && filtered[1].TranscriptionEndpoint, "server metadata selects the endpoint even for custom model aliases");
        Check(TranscriptionClient.IsGeminiAudioModel("gemini-2.5-flash") && !TranscriptionClient.IsGeminiAudioModel("gemini-2.5-flash-image") && !TranscriptionClient.IsGeminiAudioModel("gemini-2.5-flash-preview-tts"), "direct Gemini list excludes image and speech-generation models");
        bool rejected = false;
        try { TranscriptionClient.BaseUri(new UriBuilder("http", "remote.invalid").Uri.ToString()); } catch (FormatException) { rejected = true; }
        Check(rejected && TranscriptionClient.BaseUri(new UriBuilder("http", "localhost", 4000).Uri.ToString()).IsLoopback, "remote credentials require HTTPS while local proxies support HTTP");

        string audio = Path.Combine(Configuration.AudioDirectory, "provider-test.wav");
        File.WriteAllBytes(audio, [82, 73, 70, 70, 1, 2, 3, 4]);
        var settings = new Settings { Provider = Providers.LiteLlm, LiteLlmBaseUrl = new UriBuilder("https", "proxy.invalid").Uri.ToString(), LiteLlmModel = "speech-alias", Vocabulary = "Unity" };
        using (var handler = new ProviderApi())
        using (var client = new TranscriptionClient(handler))
        {
            Check(await client.TranscribeAsync(audio, settings, "verification-provider-key", CancellationToken.None) == "Проверка 42.", "LiteLLM audio chat returns only the transcription");
            Check(handler.ChatRequests == 1 && handler.FileRequests == 0, "audio chat uses JSON input_audio WAV with Bearer authentication");
            settings.LiteLlmBaseUrl = new Uri(new Uri(settings.LiteLlmBaseUrl), "v1").ToString(); settings.LiteLlmModel = "whisper-alias";
            Check(await client.TranscribeAsync(audio, settings, "verification-provider-key", CancellationToken.None) == "Проверка 42." && handler.FileRequests == 1, "speech models use multipart audio/transcriptions and accept a /v1 base URL");
            settings.LiteLlmModel = "text-only"; rejected = false;
            try { await client.TranscribeAsync(audio, settings, "verification-provider-key", CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && handler.ChatRequests == 1 && handler.FileRequests == 1 && File.Exists(audio), "an unsupported model is rejected before sending audio and preserves the recording");
            settings.Provider = Providers.Gemini;
            Check(await client.TranscribeAsync(audio, settings, "verification-provider-key", CancellationToken.None) == "Проверка 42." && handler.GeminiRequests == 1, "switching to Gemini uses its native endpoint and a separate authentication header");
        }
        using (var client = new TranscriptionClient(new ProviderApi(budgetExceeded: true)))
        {
            settings.Provider = Providers.LiteLlm; settings.LiteLlmModel = "speech-alias";
            var models = await client.GetModelsAsync(settings, "verification-provider-key", CancellationToken.None);
            Check(models.Any(x => x.Id == "speech-alias") && !models.Any(x => x.Id == "text-only") && client.CatalogWarning == Localization.T("ServiceBudgetExceeded"), "budget exhaustion keeps the authenticated audio-model catalog available with a clear warning");
            rejected = false;
            try { await client.TranscribeAsync(audio, settings, "verification-provider-key", CancellationToken.None); }
            catch (InvalidOperationException ex) { rejected = ex.Message == Localization.T("ServiceBudgetExceeded"); }
            Check(rejected && File.Exists(audio), "budget rejection gives a clear error and retains audio for retry");
        }
        Check(TranscriptionClient.ParseResponse("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"text\",\"reasoning_content\":\"hidden\"}}]}", false) == "text", "LiteLLM reasoning is excluded from dictation");
        rejected = false;
        try { TranscriptionClient.ParseResponse("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"partial\"}}]}", false); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "truncated responses cannot be pasted as complete transcriptions");
        File.Delete(audio);

        ApiKeyStore.Save("verification-gemini-key"); ApiKeyStore.Save("verification-proxy-key", Providers.LiteLlm);
        try
        {
            Check(Configuration.GetApiKey(Providers.Gemini) == "verification-gemini-key" && Configuration.GetApiKey(Providers.LiteLlm) == "verification-proxy-key", "provider keys are stored separately and never substituted for each other");
            ApiKeyStore.Delete(Providers.LiteLlm);
            Check(ApiKeyStore.Read() == "verification-gemini-key", "deleting the LiteLLM key preserves the Gemini key");
            Check(!File.Exists(Configuration.SettingsPath) || !File.ReadAllText(Configuration.SettingsPath).Contains("verification-proxy-key"), "settings never contain the LiteLLM key");
        }
        finally { ApiKeyStore.Delete(); ApiKeyStore.Delete(Providers.LiteLlm); }

        window.OpenSettings(); await Task.Delay(100);
        var provider = window.GetVisualDescendants().OfType<ComboBox>().Single(x => x.Name == "ProviderSelector");
        var model = window.GetVisualDescendants().OfType<ComboBox>().Single(x => x.Name == "ModelSelector");
        Check(model.ItemsSource is IEnumerable<AudioModel> && !window.GetVisualDescendants().OfType<TextBox>().Any(x => x.Name == "ModelSelector"), "models are selected with a dropdown without text input");
        Check(!window.GetVisualDescendants().OfType<Button>().Any(x => x.Name == "RefreshModels")
            && !window.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Classes.Contains("hint")), "settings contain no refresh button or explanatory captions");
        var direct = ((IEnumerable<AudioModel>)model.ItemsSource!).FirstOrDefault(x => x.Id != controller.Settings.Model);
        if (direct != null && controller.Settings.Provider == Providers.Gemini)
        {
            model.SelectedItem = direct;
            Check(controller.Settings.Model == direct.Id && Configuration.LoadSettings().Model == direct.Id, "choosing a model applies and saves it without another action");
        }
        var original = controller.Settings.Clone();
        var unconfigured = original.Clone(); unconfigured.LiteLlmBaseUrl = ""; controller.CommitSettings(unconfigured);
        provider.SelectedItem = ((IEnumerable<MainWindow.ProviderOption>)provider.ItemsSource!).Single(x => x.Code == Providers.LiteLlm);
        await window.RefreshModelsAsync();
        Check(controller.Settings.Provider == Providers.LiteLlm && Configuration.LoadSettings().Provider == Providers.LiteLlm, "provider dropdown applies and saves the chosen service");
        Check(!model.IsEnabled && !((IEnumerable<AudioModel>)model.ItemsSource!).Any(), "an unconfigured LiteLLM connection shows no invented or unsupported models");
        controller.CommitSettings(original);

        if (args.Contains("--live-providers"))
        {
            int index = Array.IndexOf(args, "--live-providers"); string speech = args[index + 1];
            var live = controller.Settings.Clone(); live.Provider = Providers.LiteLlm; live.LiteLlmBaseUrl = original.LiteLlmBaseUrl;
            controller.CommitSettings(live); await window.RefreshModelsAsync();
            var choices = ((IEnumerable<AudioModel>)model.ItemsSource!).ToArray();
            string? catalogStatus = window.ModelLoadError;
            bool budgetBlocked = catalogStatus == Localization.T("ServiceBudgetExceeded");
            Check(choices.Any(x => x.Id.StartsWith("gemini-", StringComparison.Ordinal)) && choices.Any(x => x.TranscriptionEndpoint), "live LiteLLM dropdown contains audio-capable Gemini and transcription models (status=" + catalogStatus + ")");
            foreach (string id in new[] { "gemini-3.5-flash", "whisper-1", "gpt-transcribe" }.Where(id => choices.Any(x => x.Id == id)))
            {
                model.SelectedItem = choices.Single(x => x.Id == id);
                Check(controller.Settings.LiteLlmModel == id && Configuration.LoadSettings().LiteLlmModel == id, "model dropdown saves " + id);
                string copy = Path.Combine(Configuration.AudioDirectory, "live-provider.wav"); File.Copy(speech, copy, true);
                var entry = new HistoryEntry { AudioPath = copy, Duration = 4 }; controller.History.Add(entry);
                await controller.ProcessAsync(entry, 0, false);
                if (budgetBlocked)
                {
                    Check(entry.Text.Length == 0 && entry.Error == Localization.T("ServiceBudgetExceeded") && entry.CanRetry && File.Exists(copy), "live budget rejection explains the limit and preserves audio for retry");
                    controller.History.Delete(entry);
                    break;
                }
                bool number = entry.Text.Contains("42") || entry.Text.Contains("forty two", StringComparison.OrdinalIgnoreCase) || entry.Text.Contains("forty-two", StringComparison.OrdinalIgnoreCase);
                Check(number && entry.Text.Contains("dictation", StringComparison.OrdinalIgnoreCase), "live transcription through " + id + " matches the prepared speech (status=" + entry.Status + ", error=" + entry.Error + ")");
                Check(!File.Exists(copy) && entry.AudioPath.Length == 0, "successful " + id + " transcription deletes the saved audio");
            }
            model.SelectedItem = choices.FirstOrDefault(x => x.Id == "gemini-3.5-flash") ?? choices[0];
            var beforeSwitch = controller.Settings.Clone();
            provider.SelectedItem = ((IEnumerable<MainWindow.ProviderOption>)provider.ItemsSource!).Single(x => x.Code == Providers.Gemini);
            Check(controller.Settings.Model == original.Model, "switching to direct Gemini preserves its previous model");
            provider.SelectedItem = ((IEnumerable<MainWindow.ProviderOption>)provider.ItemsSource!).Single(x => x.Code == Providers.LiteLlm);
            await window.RefreshModelsAsync();
            Check(controller.Settings.LiteLlmModel == beforeSwitch.LiteLlmModel && model.SelectedItem is AudioModel selected && selected.Id == beforeSwitch.LiteLlmModel, "switching back to LiteLLM restores its chosen model");
            Localization.Apply("ru"); await Task.Delay(150);
            Render(window, Path.Combine(Configuration.DataDirectory, "settings-litellm.png"));
        }
    }

    private sealed class ProviderApi(bool budgetExceeded = false) : HttpMessageHandler
    {
        internal int ChatRequests, FileRequests, GeminiRequests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "generativelanguage.googleapis.com")
            {
                if (!request.Headers.Contains("x-goog-api-key") || request.Headers.Authorization != null) throw new InvalidOperationException("Incorrect native authentication");
                GeminiRequests++;
                return Json("{\"candidates\":[{\"finishReason\":\"STOP\",\"content\":{\"parts\":[{\"text\":\"Проверка 42.\"}]}}]}");
            }
            if (request.Headers.Authorization?.Scheme != "Bearer" || request.Headers.Contains("x-goog-api-key")) throw new InvalidOperationException("Incorrect proxy authentication");
            if (budgetExceeded && (path == "/v1/models" || request.Method == HttpMethod.Post)) return new HttpResponseMessage((HttpStatusCode)422) { Content = new StringContent("{\"error\":{\"type\":\"budget_exceeded\"}}") };
            if (path == "/v1/models") return Json(AvailableModels);
            if (path == "/v1/model/info") return Json(ModelMetadata);
            if (path == "/v1/chat/completions")
            {
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var input = payload.RootElement.GetProperty("messages")[1].GetProperty("content")[0];
                if (input.GetProperty("type").GetString() != "input_audio" || input.GetProperty("input_audio").GetProperty("format").GetString() != "wav") throw new InvalidOperationException("Incorrect audio payload");
                ChatRequests++; return Json("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"Проверка 42.\"}}]}");
            }
            if (path == "/v1/audio/transcriptions")
            {
                if (request.Content is not MultipartFormDataContent form || !form.Any(x => x.Headers.ContentType?.MediaType == "audio/wav")) throw new InvalidOperationException("Missing WAV upload");
                FileRequests++; return Json("{\"text\":\"Проверка 42.\"}");
            }
            throw new InvalidOperationException("Unexpected request path: " + path);
        }
        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
