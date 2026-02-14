namespace Shared.Messages;

public class ConversationStateChange(ConversationState state)
{
    public ConversationState State { get; } = state;
}

public class PanelStateChange(AiPanelistState state)
{
    public AiPanelistState State { get; } = state;
}
