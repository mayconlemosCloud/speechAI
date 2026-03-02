namespace MeetingTranslator.Models;

public enum Speaker
{
    Them,  // pessoa da reunião falando em inglês
    You    // usuário falando em português
}

public class ConversationEntry
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public Speaker Speaker { get; init; }
    public string OriginalText { get; init; } = "";
    public string TranslatedText { get; init; } = "";

    public string TimeLabel => Timestamp.LocalDateTime.ToString("HH:mm");
}
