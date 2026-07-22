using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// The §6 Distiller against a scripted model: the prompt carries the labelled Event stream and
/// <c>/no_think</c>; the answer's candidates come back as <see cref="WisdomCandidate"/>s with
/// kind, scope, capped text, and provenance Event ids mapped from the <c>[eN]</c> references.
/// </summary>
public class EpisodeDistillerTests
{
    private static readonly Guid ProjectId = Guid.CreateVersion7();

    private readonly FakeChatClient _chat = new();

    private readonly Episode _episode = new()
    {
        Id = Guid.CreateVersion7(),
        SessionId = $"session-{Guid.NewGuid():N}",
        ProjectId = ProjectId,
        StartedAt = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero),
        Cwd = @"C:\git\mimir",
    };

    [Fact]
    public async Task Candidates_CarryKindScopeText_AndTheReferencedEventIds()
    {
        var prompt = NewEvent(1, EventType.UserPromptSubmit, """{"prompt":"fix the flaky test"}""");
        var stop = NewEvent(2, EventType.Stop, """{"reason":"done"}""");
        _chat.Reply("""
            {"candidates":[
              {"kind":"lesson","scope":"project","text":"The flaky test needs the fake clock.","events":[1,2]},
              {"kind":"preference","scope":"global","text":"Prefers rebase over merge.","events":[1]}
            ]}
            """);

        var candidates = await DistillAsync(prompt, stop);

        candidates.Count.ShouldBe(2);
        var lesson = candidates[0];
        lesson.Kind.ShouldBe(WisdomKind.Lesson);
        lesson.ScopeProjectId.ShouldBe(ProjectId);
        lesson.Text.ShouldBe("The flaky test needs the fake clock.");
        lesson.EpisodeId.ShouldBe(_episode.Id);
        lesson.EventIds.ShouldBe([prompt.Id, stop.Id]);
        lesson.HarvestedItemId.ShouldBeNull();

        var preference = candidates[1];
        preference.Kind.ShouldBe(WisdomKind.Preference);
        preference.ScopeProjectId.ShouldBe(Project.GlobalId, "scope global maps to the Global pseudo-project");
        preference.EventIds.ShouldBe([prompt.Id]);
    }

    [Fact]
    public async Task ThePrompt_LabelsEventsBySeq_AndSpeaksNoThink()
    {
        var evt = NewEvent(7, EventType.PostToolUse, """{"tool_name":"Bash"}""");
        _chat.Reply("""{"candidates":[]}""");

        await DistillAsync(evt);

        var (messages, options) = _chat.Calls.ShouldHaveSingleItem();
        var user = messages.Single(m => m.Role == ChatRole.User).Text;
        user.ShouldContain("[e7] PostToolUse");
        user.ShouldContain("""{"tool_name":"Bash"}""");
        user.ShouldContain("identity/of/project");
        user.TrimEnd().ShouldEndWith("/no_think");
        options.ShouldNotBeNull().Temperature.ShouldBe(0);
        options.AdditionalProperties!["num_ctx"].ShouldBe(16384);
    }

    [Fact]
    public async Task ARememberEvent_IsMarkedAsADeliberateSave()
    {
        _chat.Reply("""{"candidates":[]}""");

        await DistillAsync(NewEvent(3, EventType.Remember, """{"content":"always pin the SDK"}"""));

        var user = _chat.Calls.Single().Messages.Single(m => m.Role == ChatRole.User).Text;
        user.ShouldContain("[e3] Remember — deliberate save");
    }

    [Fact]
    public async Task HallucinatedEventRefs_AreDropped_AndNoRealRefsMeansEpisodeProvenance()
    {
        var evt = NewEvent(1, EventType.Stop, "{}");
        _chat.Reply("""
            {"candidates":[
              {"kind":"fact","scope":"project","text":"Real ref survives.","events":[1,99]},
              {"kind":"fact","scope":"project","text":"No real refs at all.","events":[42]}
            ]}
            """);

        var candidates = await DistillAsync(evt);

        candidates[0].EventIds.ShouldBe([evt.Id]);
        candidates[1].EventIds.ShouldBeNull("the gate then writes the Episode-level Provenance row");
        candidates[1].EpisodeId.ShouldBe(_episode.Id);
    }

    [Fact]
    public async Task BlankCandidates_AreSkipped_AndLongTextIsCappedAt500()
    {
        _chat.Reply($$"""
            {"candidates":[
              {"kind":"fact","scope":"project","text":"   ","events":[]},
              {"kind":"fact","scope":"project","text":"{{new string('y', 600)}}","events":[]}
            ]}
            """);

        var candidates = await DistillAsync(NewEvent(1, EventType.Stop, "{}"));

        var kept = candidates.ShouldHaveSingleItem("a blank note is the model failing to decline");
        kept.Text.Length.ShouldBe(500);
    }

    [Fact]
    public async Task AnOversizedEpisode_IsDistilledPerChunk()
    {
        // Two chunks at this budget; each chat call must see only its own chunk's labels and the
        // second chunk's [eN] references must map to the second chunk's Event ids.
        var events = Enumerable.Range(1, 4)
            .Select(seq => NewEvent(seq, EventType.PostToolUse, new string('x', 700)))
            .ToList();
        _chat.Reply("""{"candidates":[{"kind":"fact","scope":"project","text":"From chunk one.","events":[1]}]}""");
        _chat.Reply("""{"candidates":[{"kind":"fact","scope":"project","text":"From chunk two.","events":[4]}]}""");

        var candidates = await DistillAsync(chunkTokens: 400, events.ToArray());

        _chat.Calls.Count.ShouldBe(2);
        _chat.Calls[0].Messages.Single(m => m.Role == ChatRole.User).Text.ShouldContain("[e1]");
        _chat.Calls[1].Messages.Single(m => m.Role == ChatRole.User).Text.ShouldNotContain("[e1]");
        candidates.Select(c => c.Text).ShouldBe(["From chunk one.", "From chunk two."]);
        candidates[0].EventIds.ShouldBe([events[0].Id]);
        candidates[1].EventIds.ShouldBe([events[3].Id]);
    }

    [Fact]
    public async Task AnUnparseableAnswer_ThrowsADistillerException()
    {
        _chat.Reply("the model rambles instead of answering");

        await Should.ThrowAsync<DistillerException>(() => DistillAsync(NewEvent(1, EventType.Stop, "{}")));
    }

    [Fact]
    public async Task NoEvents_MeansNoModelCalls_AndNoCandidates()
    {
        (await DistillAsync()).ShouldBeEmpty();
        _chat.Calls.ShouldBeEmpty();
    }

    private Task<IReadOnlyList<WisdomCandidate>> DistillAsync(params Event[] events)
        => DistillAsync(chunkTokens: 12_288, events);

    private async Task<IReadOnlyList<WisdomCandidate>> DistillAsync(int chunkTokens, params Event[] events)
    {
        var distiller = new EpisodeDistiller(
            _chat, Options.Create(new DistillationOptions { ChunkTokens = chunkTokens }));
        return await distiller.DistillAsync(
            _episode, "identity/of/project", events, TestContext.Current.CancellationToken);
    }

    private Event NewEvent(int seq, EventType type, string payload) => new()
    {
        Id = Guid.CreateVersion7(),
        EpisodeId = _episode.Id,
        Seq = seq,
        Type = type,
        At = _episode.StartedAt.AddMinutes(seq),
        Payload = payload,
        Salient = type == EventType.Remember,
    };
}
