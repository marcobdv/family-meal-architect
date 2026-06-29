using OpenAI;
using OpenAI.Chat;

namespace Api.Services;

/// <summary>OpenAI-backed implementation of <see cref="IAiChatClient"/>.</summary>
public class OpenAiChatClient(OpenAIClient openAiClient, IConfiguration config) : IAiChatClient
{
    private readonly OpenAIClient _openAiClient = openAiClient;
    private readonly IConfiguration _config = config;

    public async Task<AiResult> CompleteAsync(string prompt, bool jsonMode, CancellationToken ct = default)
    {
        var modelName = _config["OpenAI:ModelName"] ?? "gpt-4o-mini";
        var chatClient = _openAiClient.GetChatClient(modelName);

        var options = new ChatCompletionOptions();
        if (jsonMode)
        {
            options.ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat();
        }

        var response = await chatClient.CompleteChatAsync([new UserChatMessage(prompt)], options, ct);
        var usage = response.Value.Usage;
        return new AiResult(
            response.Value.Content[0].Text,
            usage?.InputTokenCount ?? 0,
            usage?.OutputTokenCount ?? 0);
    }
}
