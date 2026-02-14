namespace Shared;

public record ConversationState(
    string RollingTranscript,
    string CurrentSummary,
    bool IsSpeaking,
    bool IsDisabled
);

public enum AiPanelistState
{
    Idle,
    Thinking,
    Speaking
}