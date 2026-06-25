using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using OpenMono.Llm;
using OpenMono.Session;

namespace OpenMono.Tests.Session;

public class CompactorTests
{
    // ──────────────────────────────────────────────
    // Summary message role
    // ──────────────────────────────────────────────

    [Fact]
    public async Task CompactAsync_SummaryMessage_IsSystemRole()
    {
        var session = BuildSession(userTurns: 10);
        var compactor = new Compactor(new FakeLlm("This is a summary."), contextSize: 8192);

        var (compacted, _) = await compactor.CompactAsync(session, ct: CancellationToken.None);

        compacted.Messages.Should().Contain(m =>
            m.Role == MessageRole.System &&
            m.Content != null &&
            m.Content.Contains("This is a summary."));
    }

    [Fact]
    public async Task CompactAsync_SummaryMessage_IsNotUserRole()
    {
        var session = BuildSession(userTurns: 10);
        var compactor = new Compactor(new FakeLlm("Summary content."), contextSize: 8192);

        var (compacted, _) = await compactor.CompactAsync(session, ct: CancellationToken.None);

        compacted.Messages.Should().NotContain(m =>
            m.Role == MessageRole.User &&
            m.Content != null &&
            m.Content.Contains("[Conversation summary"));
    }

    [Fact]
    public async Task CompactAsync_DoesNotInjectFakeAssistantAcknowledgement()
    {
        var session = BuildSession(userTurns: 10);
        var compactor = new Compactor(new FakeLlm("Summary content."), contextSize: 8192);

        var (compacted, _) = await compactor.CompactAsync(session, ct: CancellationToken.None);

        compacted.Messages.Should().NotContain(m =>
            m.Role == MessageRole.Assistant &&
            m.Content != null &&
            m.Content.Contains("Understood"));
    }

    // ──────────────────────────────────────────────
    // Structure after compaction
    // ──────────────────────────────────────────────

    [Fact]
    public async Task CompactAsync_PreservesSystemMessagesAtHead()
    {
        var session = BuildSession(userTurns: 10);
        var compactor = new Compactor(new FakeLlm("Summary."), contextSize: 8192);

        var (compacted, _) = await compactor.CompactAsync(session, ct: CancellationToken.None);

        compacted.Messages.First().Role.Should().Be(MessageRole.System);
    }

    [Fact]
    public async Task CompactAsync_ReportReflectsCompression()
    {
        var session = BuildSession(userTurns: 10);
        var compactor = new Compactor(new FakeLlm("Summary."), contextSize: 8192);

        var (_, report) = await compactor.CompactAsync(session, ct: CancellationToken.None);

        report.MessagesCompressed.Should().BeGreaterThan(0);
        report.MessagesAfter.Should().BeLessThan(report.MessagesBefore);
    }

    [Fact]
    public async Task CompactAsync_TooFewMessages_ReturnsEmptyReport()
    {
        // Only 2 non-system, non-recent messages — below the threshold of 4
        var session = new SessionState();
        session.AddMessage(new Message { Role = MessageRole.System, Content = "Sys" });
        session.AddMessage(new Message { Role = MessageRole.User, Content = "Q1" });
        session.AddMessage(new Message { Role = MessageRole.Assistant, Content = "A1" });

        var compactor = new Compactor(new FakeLlm("Summary."), contextSize: 8192);

        var (_, report) = await compactor.CompactAsync(session, ct: CancellationToken.None);

        report.MessagesCompressed.Should().Be(0);
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
            session.AddMessage(new Message { Role = MessageRole.Assistant, Content = $"Reply {i}" });
        }
        return session;
    }

    private sealed class FakeLlm(string summaryText) : ILlmClient
    {
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages,
            JsonElement? tools,
            LlmOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new StreamChunk { TextDelta = summaryText, IsComplete = false };
            yield return new StreamChunk { IsComplete = true };
            await Task.Yield();
        }

        public void Dispose() { }
    }
}
