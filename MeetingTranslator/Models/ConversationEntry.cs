namespace MeetingTranslator.Models;

public enum Speaker
{
    Them,  // pessoa da reunião falando em inglês
    You,   // usuário falando em português
    AI     // assistente (Gemini) respondendo
}

public class ConversationEntry
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public Speaker Speaker { get; init; }
    public string OriginalText { get; init; } = "";
    public string TranslatedText { get; init; } = "";

    /// <summary>ID de speaker da diarização Azure (ex: "Guest-1"). Null = sem diarização.</summary>
    public string? SpeakerId { get; init; }

    /// <summary>Rótulo amigável para exibição: "Você", "Speaker 1", "Speaker 2", etc.</summary>
    public string SpeakerLabel
    {
        get
        {
            if (Speaker == Speaker.You) return "Você";
            if (Speaker == Speaker.AI)  return "IA";
            if (string.IsNullOrEmpty(SpeakerId)) return "Eles";
            // "Guest-1" → "Speaker 1", "Guest-2" → "Speaker 2"
            var num = SpeakerId.Replace("Guest-", "").Replace("guest-", "");
            return int.TryParse(num, out _) ? $"Speaker {num}" : SpeakerId;
        }
    }

    // Suporte a anexo visual no chat
    public string? AttachedImageBase64 { get; init; }

    public bool IsThinking { get; set; }

    public string TimeLabel => Timestamp.LocalDateTime.ToString("HH:mm");
}
