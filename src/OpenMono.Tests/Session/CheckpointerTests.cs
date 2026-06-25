using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using OpenMono.Llm;
using OpenMono.Session;

namespace OpenMono.Tests.Session;

public class CheckpointerTests
{
    // ──────────────────────────────────────────────
    // BuildContextWindow — no checkpoint yet
    // ──────────────────────────────────────────────

    [Fact]
    public void BuildContextWindow_NoCheckpoints_ReturnsAllMessages()
    {
        var session = BuildSession(userTurns: 2);
        var checkpointer = new Checkpointer(new NoOpLlm(), contextSize: 8192);

        var window = checkpointer.BuildContextWindow(session);

        window.Should().BeEquivalentTo(session.Messages, o => o.WithStrictOrdering());
    }

    // ──────────────────────────────────────────────
    // BuildContextWindow — checkpoint message is System role
    // ──────────────────────────────────────────────

    [Fact]
    public void BuildContextWindow_WithCheckpoint_InjectsSystemMessage()
    {
        var session = BuildSession(userTurns: 6);
        AddCheckpoint(session, cutoffIndex: 3, summary: "Summary text.");
        var checkpointer = new Checkpointer(new NoOpLlm(), contextSize: 8192);

        var window = checkpointer.BuildContextWindow(session);

        // At least one injected system message must contain the summary
        window.Should().Contain(m =>
            m.Role == MessageRole.System &&
            m.Content != null &&
            m.Content.Contains("Summary text."));
    }

    [Fact]
    public void BuildContextWindow_WithCheckpoint_DoesNotInjectFakeAssistantMessage()
    {
        var session = BuildSession(userTurns: 6);
        AddCheckpoint(session, cutoffIndex: 3, summary: "Some summary.");
        var checkpointer = new Checkpointer(new NoOpLlm(), contextSize: 8192);

        var window = checkpointer.BuildContextWindow(session);

        // The injected system message should NOT be immediately followed by a fake assistant ack
        window.Should().NotContain(m =>
            m.Role == MessageRole.Assistant &&
            m.Content != null &&
            m.Content.Contains("Understood"));
    }

    [Fact]
    public void BuildContextWindow_WithCheckpoint_CheckpointMessageIsNotUserRole()
    {
        var session = BuildSession(userTurns: 4);
        AddCheckpoint(session, cutoffIndex: 2, summary: "Brief summary.");
        var checkpointer = new Checkpointer(new NoOpLlm(), contextSize: 8192);

        var window = checkpointer.BuildContextWindow(session);

        // The injected checkpoint message must NOT be a User message
        window.Should().NotContain(m =>
            m.Role == MessageRole.User &&
            m.Content != null &&
            m.Content.Contains("[Checkpoint"));
    }

    [Fact]
    public void BuildContextWindow_WithCheckpoint_ContainsExactlyOneInjectedCheckpointMessage()
    {
        var session = BuildSession(userTurns: 6);
        AddCheckpoint(session, cutoffIndex: 3, summary: "Summary.");
        var checkpointer = new Checkpointer(new NoOpLlm(), contextSize: 8192);

        var window = checkpointer.BuildContextWindow(session);

        window.Count(m =>
            m.Role == MessageRole.System &&
            m.Content != null &&
            m.Content.Contains("[Checkpoint")).Should().Be(1);
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private static SessionState BuildSession(int userTurns)
    {
        var session = new SessionState();
        session.AddMessage(new Message { Role = MessageRole.System, Content = "System prompt" });
        for (var i = 1; i <= userTurns; i++)
        {
            session.AddMessage(new Message { Role = MessageRole.User, Content = $"User turn {i}" });
            session.AddMessage(new Message { Role = MessageRole.Assistant, Content = $"Assistant reply {i}" });
        }
        return session;
    }

    private static void AddCheckpoint(SessionState session, int cutoffIndex, string summary)
    {
        var entry = new CheckpointEntry
        {
            Id = "test0001",
            CreatedAt = DateTime.UtcNow,
            TurnIndex = 1,
            CutoffMessageIndex = cutoffIndex,
            Summary = summary,
            MessagesCompressed = cutoffIndex,
        };
        session.Checkpoints.Add(entry);
        session.CheckpointCutoffIndex = cutoffIndex;
    }

    private sealed class NoOpLlm : ILlmClient
    {
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages,
            JsonElement? tools,
            LlmOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new StreamChunk { TextDelta = "summary", IsComplete = false };
            yield return new StreamChunk { IsComplete = true };
            await Task.Yield();
        }

        public void Dispose() { }
    }
}
