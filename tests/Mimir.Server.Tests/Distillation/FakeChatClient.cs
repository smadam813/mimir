using Microsoft.Extensions.AI;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// A scripted stand-in for qwen3:8b: replies are dequeued in order, and every call's messages and
/// options are captured so tests can assert on the prompt the arbiter actually sent.
/// </summary>
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
