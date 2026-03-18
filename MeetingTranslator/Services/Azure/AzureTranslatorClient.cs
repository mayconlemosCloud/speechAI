using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MeetingTranslator.Services.Azure;

/// <summary>
/// Cliente leve para Azure Translator REST API.
/// Usa a mesma AZURE_SPEECH_KEY quando o recurso é do tipo multi-service.
/// </summary>
public sealed class AzureTranslatorClient : IDisposable
{
    private const string Endpoint = "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0";
    private readonly HttpClient _http;

    public AzureTranslatorClient(string subscriptionKey, string region)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        _http.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", subscriptionKey);
        _http.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Region", region);
    }

    /// <summary>
    /// Traduz <paramref name="text"/> do idioma <paramref name="from"/> para <paramref name="to"/>.
    /// Retorna o texto original em caso de erro para não interromper o fluxo.
    /// </summary>
    public async Task<string> TranslateAsync(string text, string from, string to, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        if (from.Equals(to, StringComparison.OrdinalIgnoreCase)) return text;

        try
        {
            var url = $"{Endpoint}&from={from}&to={to}";
            var body = JsonSerializer.Serialize(new[] { new { Text = text } });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[Translator] HTTP {(int)response.StatusCode} para texto: {text[..Math.Min(40, text.Length)]}");
                return text; // fallback: original
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var translated = doc.RootElement[0]
                .GetProperty("translations")[0]
                .GetProperty("text")
                .GetString();

            return string.IsNullOrWhiteSpace(translated) ? text : translated;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Translator] Erro: {ex.Message}");
            return text; // fallback: original
        }
    }

    public void Dispose() => _http.Dispose();
}
