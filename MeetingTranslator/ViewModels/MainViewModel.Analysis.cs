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

    public void TriggerManualAutoWhisper()
    {
        _ = RunAutoWhisperAsync(force: true);
    }

    public async Task RunAutoWhisperAsync(bool force = false)
    {
        if (IsAiProcessing && force) return; // avoid spam if already processing a command

        // Only do this if active or forced
        if (!IsAutoWhisperActive && !force) return;

        // Get last few lines of conversation (expanded to 15 to give proper context)
        var recentHistory = History.Where(x => !x.IsThinking).TakeLast(15).ToList();
        if (recentHistory.Count == 0) return;
        
        // Smart Trigger heuristic for automatic mode
        if (!force)
        {
            var lastMsg = recentHistory.Last();
            
            // Opcional: Só reage se a última fala for do entrevistador, ou se for uma pergunta clara
            string textToCheck = (lastMsg.OriginalText + " " + lastMsg.TranslatedText).ToLowerInvariant();
            bool hasQuestion = textToCheck.Contains("?") || 
                               textToCheck.Contains("what is") || 
                               textToCheck.Contains("how ") || 
                               textToCheck.Contains("can you") ||
                               textToCheck.Contains("explain");
                               
            if (!hasQuestion) return;
        }

        System.Diagnostics.Debug.WriteLine($"[AI] Iniciando Auto-Sussurro (force: {force})...");
        AutoWhisperHint = "💡 Mágico: Analisando contexto...";

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Contexto prévio da entrevista:");
            foreach(var item in recentHistory)
            {
                 bool isAi = item.Speaker == Models.Speaker.AI;
                 bool isUser = item.Speaker == Models.Speaker.You;
                 string role = isAi ? "IA" : isUser ? "Eu (Candidato)" : "Entrevistador";
                 sb.AppendLine($"[{role}]: {item.TranslatedText}");
            }
            sb.AppendLine();
            sb.AppendLine("Atue como um copiloto invisível para o 'Eu (Candidato)'. Analise o fluxo da conversa acima atentamente.");
            sb.AppendLine("1. Se a última interação for uma saudação ('hello', 'how are you'), papo furado, ou não houver escopo técnico/pergunta na mesa, responda EXATAMENTE E APENAS com a palavra: IGNORAR");
            sb.AppendLine("2. Se o entrevistador acabou de fazer uma pergunta técnica ou pedir um exemplo de código/experiência, sugira a resposta ideal de forma ULTRA RESUMIDA em apenas 1 linha (máx 15 palavras). Direto ao ponto, sem dizer 'Diga que' ou 'A resposta é'. Entregue apenas a solução ou o termo que o candidato deve falar.");
            
            var response = await Gemini.AnalyzeTextAsync(sb.ToString());
            
            // Format response
            response = response.Replace("\n", " ").Replace("\r", "").Trim();

            if (response.Equals("IGNORAR", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"[AI] Ignorado por ser conversa social.");
                App.Current.Dispatcher.Invoke(() => AutoWhisperHint = "");
                return;
            }
            
            // UI Thread update
            App.Current.Dispatcher.Invoke(() => 
            {
                AutoWhisperHint = $"💡 {response}";

                // Register in History
                History.Add(new Models.ConversationEntry
                {
                    Speaker = Models.Speaker.AI,
                    TranslatedText = $"[Auto-Sussurro] {response}",
                    Timestamp = DateTimeOffset.UtcNow
                });
            });
            
            // Apaga a dica da barra principal após 20 segundos
            _ = Task.Run(async () => 
            {
                await Task.Delay(20000);
                App.Current.Dispatcher.Invoke(() => 
                {
                    if (AutoWhisperHint == $"💡 {response}")
                    {
                        AutoWhisperHint = "";
                    }
                });
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AI] Erro no Auto-Sussurro: {ex.Message}");
            App.Current.Dispatcher.Invoke(() => AutoWhisperHint = $"⚠️ Erro Auto-Sussurro");
        }
    }

    public async Task SendAiMessageAsync()
    {
        if (IsAiProcessing || (string.IsNullOrWhiteSpace(AiPrompt) && string.IsNullOrEmpty(PendingScreenshotBase64))) 
            return;

        System.Diagnostics.Debug.WriteLine($"[AI] Iniciando fluxo SendAiMessageAsync. Prompt length: {AiPrompt.Length}, HasImage: {!string.IsNullOrEmpty(PendingScreenshotBase64)}");

        var prompt = AiPrompt;
        var base64Image = PendingScreenshotBase64;

        if (!string.IsNullOrEmpty(base64Image))
        {
            System.Diagnostics.Debug.WriteLine($"[AI] Imagem detectada. Tamanho Base64: {base64Image.Length}");
        }

        // Limpa a UI para o próximo envio
        AiPrompt = "";

        // Adiciona a mensagem do usuário ao histórico localmente
        History.Add(new Models.ConversationEntry
        {
            Speaker = Models.Speaker.You,
            TranslatedText = string.IsNullOrWhiteSpace(prompt) ? "📸 Imagem enviada para análise" : prompt,
            OriginalText = "Prompt Enviado",
            AttachedImageBase64 = base64Image
        });

        // Agora limpamos a imagem pendente da UI central
        PendingScreenshotBase64 = null;

        IsAiProcessing = true;
        
        // Adiciona uma bolha de "pensando" da IA
        var thinkingEntry = new Models.ConversationEntry
        {
            Speaker = Models.Speaker.AI,
            IsThinking = true
        };
        History.Add(thinkingEntry);
        
        try
        {
            string response;
            if (!string.IsNullOrEmpty(base64Image))
            {
                var finalPrompt = string.IsNullOrWhiteSpace(prompt) ? "Analise e descreva esta imagem." : prompt;
                System.Diagnostics.Debug.WriteLine($"[AI] Chamando AnalyzeImageAsync com prompt: {finalPrompt}");
                response = await Gemini.AnalyzeImageAsync(finalPrompt, base64Image);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[AI] Chamando AnalyzeTextAsync...");
                var sb = new StringBuilder();
                sb.AppendLine("Contexto prévio do chat:");
                foreach (var item in History.TakeLast(20).Where(x => !x.IsThinking))
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

            System.Diagnostics.Debug.WriteLine($"[AI] Resposta recebida do Gemini (length: {response.Length})");

            // Atualiza a bolha que estava pensando com o texto real
            var index = History.IndexOf(thinkingEntry);
            if (index != -1)
            {
                History[index] = new Models.ConversationEntry
                {
                    Speaker = Models.Speaker.AI,
                    TranslatedText = response,
                    Timestamp = DateTimeOffset.UtcNow
                };
            }
        }
        catch (Exception ex)
        {
            string errorDetail = ex.InnerException != null ? $" | Interno: {ex.InnerException.Message}" : "";
            System.Diagnostics.Debug.WriteLine($"[AI] CRITICAL ERROR em SendAiMessageAsync: {ex.Message}{errorDetail}\nStack: {ex.StackTrace}");
            
            var index = History.IndexOf(thinkingEntry);
            if (index != -1)
            {
                History[index] = new Models.ConversationEntry
                {
                    Speaker = Models.Speaker.AI,
                    TranslatedText = $"⚠️ Erro ao consultar Gemini: {ex.Message}{errorDetail}"
                };
            }
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
        
        // Adiciona uma bolha de "pensando" da IA
        var thinkingEntry = new Models.ConversationEntry
        {
            Speaker = Models.Speaker.AI,
            IsThinking = true
        };
        History.Add(thinkingEntry);
        
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Histórico da Reunião:");
            foreach (var item in History.Where(x => !x.IsThinking))
            {
                if (item.Speaker == Models.Speaker.Them)
                    sb.AppendLine($"[Inglês: {item.OriginalText}] -> [Português: {item.TranslatedText}]");
                else if (item.Speaker == Models.Speaker.You && item.OriginalText != "Prompt Enviado")
                    sb.AppendLine($"[Você em Pt: {item.TranslatedText}] -> [En: {item.OriginalText}]");
            }
            sb.AppendLine();
            sb.AppendLine("Instrução: Por favor, resuma os principais pontos discutidos nesta reunião e crie uma lista de tarefas se houver.");

            var response = await Gemini.AnalyzeTextAsync(sb.ToString());
            
            // Atualiza a bolha que estava pensando com o resumo
            var index = History.IndexOf(thinkingEntry);
            if (index != -1)
            {
                History[index] = new Models.ConversationEntry
                {
                    Speaker = Models.Speaker.AI,
                    TranslatedText = response,
                    Timestamp = DateTimeOffset.UtcNow
                };
            }
        }
        catch (Exception ex)
        {
             var index = History.IndexOf(thinkingEntry);
             if (index != -1)
             {
                 History[index] = new Models.ConversationEntry
                 {
                     Speaker = Models.Speaker.AI,
                     TranslatedText = $"⚠️ Erro ao consultar Gemini: {ex.Message}"
                 };
             }
        }
        finally
        {
            IsAiProcessing = false;
        }
    }
}
