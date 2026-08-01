using Microsoft.Extensions.AI;

namespace Mimir.Server.Tests.Distillation;

internal sealed class FakeChatClient : IChatClient
{
    private readonly Queue<string> _replies = new();

    public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

    public void Reply(string text) => _replies.Enqueue(text);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((messages.ToList(), options));
        if (_replies.Count == 0)
        {
            throw new InvalidOperationException("no scripted reply left");
        }

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _replies.Dequeue())));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the arbiter never streams");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
