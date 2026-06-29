using Api.Services;

namespace Tests;

/// <summary>Test double for <see cref="IAiChatClient"/> that returns queued responses.</summary>
public class FakeAiChatClient : IAiChatClient
{
    private readonly Queue<string> _responses;
    public int CallCount { get; private set; }
    public List<string> Prompts { get; } = new();

    public FakeAiChatClient(params string[] responses) => _responses = new Queue<string>(responses);

    public Task<AiResult> CompleteAsync(string prompt, bool jsonMode, CancellationToken ct = default)
    {
        CallCount++;
        Prompts.Add(prompt);
        var text = _responses.Count > 0 ? _responses.Dequeue() : "{}";
        return Task.FromResult(new AiResult(text, 100, 50));
    }
}
