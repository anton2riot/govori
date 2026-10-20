using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.VisualTree;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Govori;

internal static partial class Verification
{
    private static async Task CostAndLayoutChecks(MainWindow window, DictationController controller)
    {
        Check(Configuration.DataDirectory != Configuration.DefaultDataDirectory, "cost and layout checks use isolated data");
        Check(Localization.Catalog("en").Keys.Order().SequenceEqual(Localization.Catalog("ru").Keys.Order()), "billing translations match in both languages");
        Check(Localization.Catalog("en").Keys.All(key => System.Text.CompositeFormat.Parse(Localization.Catalog("en")[key]).MinimumArgumentCount == System.Text.CompositeFormat.Parse(Localization.Catalog("ru")[key]).MinimumArgumentCount), "billing translation placeholders match");

        RequestCharge Price(string body, string provider = Providers.Gemini, string model = "gemini-2.5-flash", string? header = null, ModelPricing? pricing = null)
        {
            using var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (header != null) response.Headers.TryAddWithoutValidation("x-litellm-response-cost", header);
            var charge = new RequestCharge { Provider = provider, Model = model };
            RequestPricing.Apply(charge, response, body, new AudioModel(model, Pricing: pricing), 60);
            return charge;
        }
        const string nativeUsage = """{"usageMetadata":{"promptTokenCount":1100,"candidatesTokenCount":100,"thoughtsTokenCount":50,"promptTokensDetails":[{"modality":"TEXT","tokenCount":100},{"modality":"AUDIO","tokenCount":1000}]}}""";
        var native = Price(nativeUsage);
        Check(native.CostUsd == 0.001405m && native.Estimated && native.OutputTokens == 150, "Gemini counts text, audio and thinking at their separate paid rates");
        Check(Price(nativeUsage, model: "gemini-2.5-flash-lite").CostUsd == 0.000370m, "Flash Lite uses its own audio and output rates");
        Check(Price(nativeUsage, model: "unknown-model").CostUsd == null && Price("{}").CostUsd == null, "unknown tariffs and missing usage never display a fabricated zero");
        Check(Price("{}", Providers.LiteLlm, header: "4.2e-4").CostUsd == 0.00042m && !Price("{}", Providers.LiteLlm, header: "0").Estimated, "LiteLLM response header preserves exact cost, exponent notation and genuine zero");
        Check(Price("not json", Providers.LiteLlm, header: "0.0042").CostUsd == 0.0042m, "reported billing survives malformed transcription responses");
        Check(Price("{}", Providers.LiteLlm, header: "-1").CostUsd == null, "invalid negative reported prices are ignored");
        Check(Price("""{"usage":{"cost":0.007}}""", Providers.LiteLlm).CostUsd == 0.007m, "LiteLLM usage cost is recorded");
        var metadataPrice = Price("""{"usage":{"prompt_tokens":1100,"completion_tokens":100,"prompt_tokens_details":{"audio_tokens":1000}}}""", Providers.LiteLlm, pricing: new(0.0000003m, 0.0000025m, 0.000001m));
        Check(metadataPrice.CostUsd == 0.00128m && metadataPrice.Estimated, "proxy model metadata prices text and audio usage without guessing from aliases");
        Check(Price("{}", Providers.LiteLlm, pricing: new(InputSecond: 0.0001m)).CostUsd == 0.006m, "transcription models support server rates per second");
        Check(Price("""{"usage":{"prompt_tokens":100,"completion_tokens":10}}""", Providers.LiteLlm, pricing: new(0.000001m, 0.000002m, 0.000003m)).CostUsd == null, "missing modality counts do not silently use a wrong audio rate");
        var pro = Price("""{"usageMetadata":{"promptTokenCount":200001,"candidatesTokenCount":1}}""", model: "gemini-2.5-pro");
        Check(pro.CostUsd == 0.5000175m, "Gemini Pro applies the rate for prompts above 200k tokens");

        string audio = Path.Combine(Configuration.AudioDirectory, "cost-test.wav"); File.WriteAllBytes(audio, [82, 73, 70, 70]);
        var settings = new Settings { Provider = Providers.LiteLlm, LiteLlmBaseUrl = new UriBuilder("https", "proxy.invalid").Uri.ToString(), LiteLlmModel = "speech-alias" };
        var entry = new HistoryEntry { Text = "Проверка стоимости запроса.", Status = "saved", Duration = 60 };
        controller.History.Add(entry);
        void Save(RequestCharge charge) { charge.HistoryId = entry.Id; controller.Usage.Save(charge); }
        using (var client = new TranscriptionClient(new BillingApi(retry: true)))
            Check(await client.TranscribeAsync(audio, settings, "verification-billing-key", CancellationToken.None, Save, 60) == "Проверка.", "cost collection preserves transcription results across automatic retries");
        var recorded = new UsageStore().Entries.Where(x => x.HistoryId == entry.Id).ToArray();
        Check(recorded.Length == 2 && recorded.Sum(x => x.CostUsd) == 0.00044m, "each HTTP attempt has one persistent charge and retries are summed");
        using (var client = new TranscriptionClient(new BillingApi(truncated: true)))
        {
            bool rejected = false;
            try { await client.TranscribeAsync(audio, settings, "verification-billing-key", CancellationToken.None, Save, 60); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && new UsageStore().Entries.Count(x => x.HistoryId == entry.Id) == 3, "paid truncated responses retain their charge even when transcription fails");
        }
        var cancelledCharges = new List<RequestCharge>();
        using (var client = new TranscriptionClient(new BillingApi(cancel: true)))
        {
            bool cancelled = false;
            try { await client.TranscribeAsync(audio, settings, "verification-billing-key", CancellationToken.None, x => { if (cancelledCharges.All(c => c.Id != x.Id)) cancelledCharges.Add(x); }, 60); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && cancelledCharges.Count == 1 && cancelledCharges[0].CostUsd == null, "requests interrupted before a response remain tracked with an unknown cost");
        }
        using (var client = new TranscriptionClient(new BillingApi()))
        {
            settings.Provider = Providers.Gemini;
            await client.TranscribeAsync(audio, settings, "verification-billing-key", CancellationToken.None, Save, 60);
            Check(controller.Usage.Entries.Last().Estimated && controller.Usage.Entries.Last().CostUsd == 0.001405m, "native Gemini usage reaches persistent spending through the actual client");
        }
        File.Delete(audio);

        var ledger = new UsageStore(Path.Combine(Configuration.DataDirectory, "daily-cost-test.json"));
        var zone = TimeZoneInfo.CreateCustomTimeZone("verification-plus-two", TimeSpan.FromHours(2), "verification", "verification");
        ledger.Save(new RequestCharge { CreatedAt = new DateTimeOffset(2026, 10, 19, 22, 30, 0, TimeSpan.Zero), CostUsd = 0.0001m });
        ledger.Save(new RequestCharge { CreatedAt = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.FromHours(2)), CostUsd = 0.0002m, Estimated = true });
        ledger.Save(new RequestCharge { CreatedAt = new DateTimeOffset(2026, 10, 20, 2, 0, 0, TimeSpan.FromHours(2)) });
        ledger.Save(new RequestCharge { CreatedAt = new DateTimeOffset(2026, 10, 19, 21, 0, 0, TimeSpan.Zero), CostUsd = 1m });
        var days = ledger.Days(zone);
        Check(days.Count == 2 && days[0].Day == new DateOnly(2026, 10, 20) && days[0].Requests == 3 && days[0].CostUsd == 0.0003m && days[0].Unknown == 1 && days[0].Estimated, "daily totals use local midnight, exact decimal arithmetic and a separate unknown count");
        int chargeCount = controller.Usage.Entries.Count;
        controller.History.Delete(entry);
        Check(new UsageStore().Entries.Count == chargeCount, "deleting a transcription keeps all its spending after reload");

        Localization.Apply("ru");
        for (int i = 0; i < 14; i++)
        {
            var sample = new HistoryEntry { Text = i == 0 ? string.Join(" ", Enumerable.Repeat("Длинная расшифровка с выделением текста без вложенной прокрутки.", 50)) : "Пример расшифровки. Данные о стоимости сохраняются отдельно от текста.", Status = "saved", Duration = 5 };
            controller.History.Add(sample);
            controller.Usage.Save(new RequestCharge { HistoryId = sample.Id, CostUsd = 0.000123m + i * 0.00001m, Estimated = true, Model = "gemini-2.5-flash" });
        }
        foreach (var size in new[] { new Size(600, 500), new Size(660, 580), new Size(900, 700) })
        {
            window.Width = size.Width; window.Height = size.Height;
            window.OpenSettings(); await Task.Delay(180);
            var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(x => x.Name == "SettingsScroll");
            var key = window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "ApiKey");
            var delete = window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "DeleteApiKey");
            var keyPosition = key.TranslatePoint(default, scroll)!.Value;
            var deletePosition = delete.TranslatePoint(default, scroll)!.Value;
            Check(Math.Abs(keyPosition.Y - deletePosition.Y) < 3 && deletePosition.X >= keyPosition.X + key.Bounds.Width, "key deletion is immediately to the right of its field at " + size);
            var presenter = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
            double contentRight = presenter.TranslatePoint(default, scroll)!.Value.X + presenter.Bounds.Width;
            var fields = window.GetVisualDescendants().OfType<Control>().Where(x => x is TextBox or ComboBox or HotkeyBox);
            Check(fields.All(x => x.TranslatePoint(default, scroll)!.Value.X + x.Bounds.Width <= contentRight - 7), "all settings fields stay clear of the scrollbar at " + size);
            Check(!scroll.AllowAutoHide && scroll.GetVisualDescendants().OfType<ScrollBar>().All(x => !x.AllowAutoHide), "settings scrollbars have no thin/expanded auto-hide states at " + size);
            Render(window, Path.Combine(Configuration.DataDirectory, $"settings-{size.Width}.png"));
            key.BringIntoView(); await Task.Delay(100);
            Render(window, Path.Combine(Configuration.DataDirectory, $"settings-key-{size.Width}.png"));
            scroll.Offset = default;
            window.OpenHistory(); await Task.Delay(150);
            Check(window.GetVisualDescendants().OfType<SelectableTextBlock>().Any() && !window.GetVisualDescendants().OfType<TextBox>().Any(x => x.IsReadOnly), "history text has selection and only the outer scrollbar at " + size);
            var historyScroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(x => x.Name == "HistoryScroll");
            Check(!historyScroll.AllowAutoHide && window.GetVisualDescendants().OfType<Border>().Any(x => x.Name == "RequestCostBadge"), "history uses consistent scrollbars and shows per-request cost badges at " + size);
            Render(window, Path.Combine(Configuration.DataDirectory, $"history-{size.Width}.png"));
            window.OpenSpending(); await Task.Delay(100);
            Render(window, Path.Combine(Configuration.DataDirectory, $"spending-{size.Width}.png"));
        }
        window.OpenSettings(); await Task.Delay(100);
        var tabControl = window.GetVisualDescendants().OfType<TabControl>().Single(x => x.Name == "MainTabs");
        Check(((IEnumerable<TabItem>)tabControl.ItemsSource!).Select(x => x.Header).SequenceEqual(new[] { "История", "Расходы", "Настройки" }), "main navigation uses standard tabs with clear labels");
        var status = window.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Name == "ApplicationStatus");
        Check(status.TranslatePoint(default, window)!.Value.Y < 48, "status stays in the header with no footer strip");
        await LanguageChecks(window, controller);
    }

    private sealed class BillingApi(bool retry = false, bool truncated = false, bool cancel = false) : HttpMessageHandler
    {
        private int attempts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("model/info") ? ModelMetadata : AvailableModels));
            if (cancel) throw new OperationCanceledException();
            if (request.RequestUri!.Host == "generativelanguage.googleapis.com")
                return Task.FromResult(Json("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"Проверка."}]}}],"usageMetadata":{"promptTokenCount":1100,"candidatesTokenCount":100,"thoughtsTokenCount":50,"promptTokensDetails":[{"modality":"TEXT","tokenCount":100},{"modality":"AUDIO","tokenCount":1000}]}}"""));
            var response = retry && attempts++ == 0 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }
                : Json(truncated ? """{"choices":[{"finish_reason":"length","message":{"content":"partial"}}]}""" : """{"choices":[{"finish_reason":"stop","message":{"content":"Проверка."}}]}""");
            response.Headers.TryAddWithoutValidation("x-litellm-response-cost", response.IsSuccessStatusCode ? "0.00042" : "0.00002");
            return Task.FromResult(response);
        }
        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
