namespace Api.Services;

/// <summary>Result of a single chat completion, including token accounting.</summary>
public record AiResult(string Text, int InputTokens, int OutputTokens)
{
    public int TotalTokens => InputTokens + OutputTokens;
}

/// <summary>
/// Abstraction over the chat model so the meal-planning logic can be unit tested
/// with a stub instead of calling OpenAI.
/// </summary>
public interface IAiChatClient
{
    Task<AiResult> CompleteAsync(string prompt, bool jsonMode, CancellationToken ct = default);
}
