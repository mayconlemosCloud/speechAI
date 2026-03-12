using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using MeetingTranslator.Services.Google;

namespace MeetingTranslator.ViewModels;

public partial class MainViewModel
{
    private GeminiService? _geminiService;

    private string _aiPrompt = "";
    public string AiPrompt
    {
        get => _aiPrompt;
        set { _aiPrompt = value; OnPropertyChanged(); }
    }

    private string? _pendingScreenshotBase64;
    public string? PendingScreenshotBase64
    {
        get => _pendingScreenshotBase64;
        set { _pendingScreenshotBase64 = value; OnPropertyChanged(); }
    }

    private bool _isAiProcessing;
    public bool IsAiProcessing
    {
        get => _isAiProcessing;
        set { _isAiProcessing = value; OnPropertyChanged(); }
    }

    private int _selectedSettingsTab = 0;
    public int SelectedSettingsTab
    {
        get => _selectedSettingsTab;
        set { _selectedSettingsTab = value; OnPropertyChanged(); }
    }

    private GeminiService Gemini => _geminiService ??= new GeminiService();

    private void InitializeAnalysisCommands()
    {
        // Commands can be mapped here if we used DelegateCommand, but we are using code-behind click handlers for UI.
    }

    public async Task SendAiMessageAsync()
    {
        if (IsAiProcessing || (string.IsNullOrWhiteSpace(AiPrompt) && string.IsNullOrEmpty(PendingScreenshotBase64))) 
            return;

        var prompt = AiPrompt;
        var base64Image = PendingScreenshotBase64;

        // Limpa a UI para o próximo envio
        AiPrompt = "";
        PendingScreenshotBase64 = null;

        // Adiciona a mensagem do usuário ao histórico localmente
        History.Add(new Models.ConversationEntry
        {
            Speaker = Models.Speaker.You,
            TranslatedText = string.IsNullOrWhiteSpace(prompt) ? "(Imagem enviada)" : prompt,
            OriginalText = "Prompt Enviado",
            AttachedImageBase64 = base64Image
        });

        IsAiProcessing = true;
        
        try
        {
            string response;
            if (!string.IsNullOrEmpty(base64Image))
            {
                var finalPrompt = string.IsNullOrWhiteSpace(prompt) ? "Analise e descreva esta imagem." : prompt;
                response = await Gemini.AnalyzeImageAsync(finalPrompt, base64Image);
            }
            else
            {
                // Se for só texto, queremos passar o contexto das conversas?
                // Vamos enviar o prompt junto com todo o histórico recente como contexto
                var sb = new StringBuilder();
                sb.AppendLine("Contexto prévio do chat:");
                foreach (var item in History.TakeLast(20))
                {
                    bool isAi = item.Speaker == Models.Speaker.AI;
                    bool isUser = item.Speaker == Models.Speaker.You;
                    string role = isAi ? "IA" : isUser ? "Usuário" : "Participante";
                    sb.AppendLine($"[{role}]: {item.TranslatedText}");
                }
                sb.AppendLine();
                sb.AppendLine($"Comando atual do Usuário: {prompt}");

                response = await Gemini.AnalyzeTextAsync(sb.ToString());
            }

            History.Add(new Models.ConversationEntry
            {
                Speaker = Models.Speaker.AI,
                TranslatedText = response
            });
        }
        catch (Exception ex)
        {
            History.Add(new Models.ConversationEntry
            {
                Speaker = Models.Speaker.AI,
                TranslatedText = $"⚠️ Erro ao consultar Gemini: {ex.Message}"
            });
        }
        finally
        {
            IsAiProcessing = false;
        }
    }

    public async Task AnalyzeMeetingHistoryAsync()
    {
        if (IsAiProcessing) return;

        if (History.Count == 0)
        {
            History.Add(new Models.ConversationEntry
            {
                Speaker = Models.Speaker.AI,
                TranslatedText = "O histórico da reunião está vazio."
            });
            return;
        }

        IsAiProcessing = true;
        
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Histórico da Reunião:");
            foreach (var item in History)
            {
                if (item.Speaker == Models.Speaker.Them)
                    sb.AppendLine($"[Inglês: {item.OriginalText}] -> [Português: {item.TranslatedText}]");
                else if (item.Speaker == Models.Speaker.You && item.OriginalText != "Prompt Enviado")
                    sb.AppendLine($"[Você em Pt: {item.TranslatedText}] -> [En: {item.OriginalText}]");
            }
            sb.AppendLine();
            sb.AppendLine("Instrução: Por favor, resuma os principais pontos discutidos nesta reunião e crie uma lista de tarefas se houver.");

            var response = await Gemini.AnalyzeTextAsync(sb.ToString());
            
            History.Add(new Models.ConversationEntry
            {
                Speaker = Models.Speaker.AI,
                TranslatedText = response
            });
        }
        catch (Exception ex)
        {
             History.Add(new Models.ConversationEntry
            {
                Speaker = Models.Speaker.AI,
                TranslatedText = $"⚠️ Erro ao consultar Gemini: {ex.Message}"
            });
        }
        finally
        {
            IsAiProcessing = false;
        }
    }
}
