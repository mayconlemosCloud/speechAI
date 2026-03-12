using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeetingTranslator.Models.Gemini;

namespace MeetingTranslator.Services.Google;

public class GeminiService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private const string ApiUrl = "https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash-latest:generateContent";

    public GeminiService()
    {
        _httpClient = new HttpClient();
        _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? string.Empty;
        
        // Note: For production or fully packaged applications, relying solely on Environment.GetEnvironmentVariable 
        // without a fallback or secure secrets manager is risky, but works well with DotEnv.
    }

    public async Task<string> AnalyzeTextAsync(string prompt)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return "Erro: Chave da API do Gemini não configurada no arquivo .env.";

        var request = new GenerateContentRequest
        {
            Contents = new List<Content>
            {
                new Content
                {
                    Parts = new List<Part>
                    {
                        new Part { Text = prompt }
                    }
                }
            }
        };

        return await SendRequestAsync(request);
    }

    public async Task<string> AnalyzeImageAsync(string prompt, string base64Image, string mimeType = "image/png")
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return "Erro: Chave da API do Gemini não configurada no arquivo .env.";

        // Clean base64 prefix if present (e.g. data:image/png;base64,)
        if (base64Image.Contains(","))
        {
            base64Image = base64Image.Split(',')[1];
        }

        var request = new GenerateContentRequest
        {
            Contents = new List<Content>
            {
                new Content
                {
                    Parts = new List<Part>
                    {
                        new Part { Text = prompt },
                        new Part 
                        { 
                            InlineData = new InlineData 
                            { 
                                MimeType = mimeType, 
                                Data = base64Image 
                            } 
                        }
                    }
                }
            }
        };

        return await SendRequestAsync(request);
    }

    private async Task<string> SendRequestAsync(GenerateContentRequest request)
    {
        try
        {
            var url = $"{ApiUrl}?key={_apiKey}";
            
            // Usando opções personalizadas para garantir que o body seja serializado em camelCase
            var options = new JsonSerializerOptions 
            { 
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            var response = await _httpClient.PostAsJsonAsync(url, request, options);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                return $"Erro do Gemini ({response.StatusCode}): {errorContent}";
            }

            var result = await response.Content.ReadFromJsonAsync<GenerateContentResponse>(options);
            
            return result?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text 
                   ?? "Nenhuma resposta gerada pela inteligência artificial.";
        }
        catch (Exception ex)
        {
            return $"Erro de comunicação com Gemini: {ex.Message}";
        }
    }
}
