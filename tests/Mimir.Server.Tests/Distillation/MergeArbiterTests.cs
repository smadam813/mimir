using Microsoft.Extensions.AI;
using Mimir.Server.Distillation;
using Mimir.Server.Storage.Entities;
using Pgvector;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// The arbiter's contract with qwen3:8b: a JSON-mode prompt carrying both texts and the
/// <c>/no_think</c> switch in, a verdict object out — parsed strictly, with rewrites capped at
/// 500 chars. Unusable answers throw <see cref="MergeArbiterException"/> so admission retries
/// instead of guessing.
/// </summary>
public sealed class MergeArbiterTests
{
    private readonly FakeChatClient _chat = new();

    [Fact]
    public async Task AnAgreementVerdict_YieldsTheMergedRewrite()
    {
        _chat.Reply("""{"verdict":"agreement","merged_text":"Use tabs everywhere; the linter enforces it."}""");

        var ruling = await RuleAsync();

        ruling.ShouldBeOfType<MergeRuling.Agreement>()
            .MergedText.ShouldBe("Use tabs everywhere; the linter enforces it.");
    }

    [Fact]
    public async Task ASupersedeVerdict_CarriesNoText()
    {
        _chat.Reply("""{"verdict":"supersede"}""");

        (await RuleAsync()).ShouldBeOfType<MergeRuling.Supersede>();
    }

    [Fact]
    public async Task AScopeSplitVerdict_CarriesBothTexts()
    {
        _chat.Reply("""{"verdict":"scope_split","global_text":"Tabs by default.","project_text":"This repo uses spaces."}""");

        var split = (await RuleAsync()).ShouldBeOfType<MergeRuling.ScopeSplit>();
        split.GlobalText.ShouldBe("Tabs by default.");
        split.ProjectText.ShouldBe("This repo uses spaces.");
    }

    [Fact]
    public async Task AnOverlongRewrite_IsCappedAtFiveHundredChars()
    {
        _chat.Reply($$"""{"verdict":"agreement","merged_text":"{{new string('x', 700)}}"}""");

        var ruling = (await RuleAsync()).ShouldBeOfType<MergeRuling.Agreement>();
        ruling.MergedText.Length.ShouldBe(500);
    }

    [Fact]
    public async Task AFencedJsonAnswer_StillParses()
    {
        _chat.Reply("""
            ```json
            {"verdict":"supersede"}
            ```
            """);

        (await RuleAsync()).ShouldBeOfType<MergeRuling.Supersede>();
    }

    [Theory]
    [InlineData("""{"verdict":"shrug"}""")]
    [InlineData("""{"verdict":"agreement"}""")]
    [InlineData("""{"verdict":"agreement","merged_text":"   "}""")]
    [InlineData("""{"verdict":"scope_split","global_text":"only one side"}""")]
    [InlineData("not json at all")]
    public async Task AnUnusableAnswer_Throws(string reply)
    {
        _chat.Reply(reply);

        await Should.ThrowAsync<MergeArbiterException>(async () => await RuleAsync());
    }

    [Fact]
    public async Task ThePrompt_CarriesBothTexts_NoThink_AndTheVerdictSchema()
    {
        _chat.Reply("""{"verdict":"supersede"}""");

        await RuleAsync(existingText: "old lesson text", candidateText: "new lesson text");

        var (messages, options) = _chat.Calls.ShouldHaveSingleItem();
        var prompt = string.Concat(messages.Select(m => m.Text));
        prompt.ShouldContain("old lesson text");
        prompt.ShouldContain("new lesson text");
        prompt.ShouldContain("/no_think");
        options.ShouldNotBeNull();
        options.Temperature.ShouldBe(0);

        // Structured output: the schema rides the response format, so Ollama constrains
        // generation to it instead of being merely asked for JSON.
        var format = options.ResponseFormat.ShouldBeOfType<ChatResponseFormatJson>();
        format.Schema.ShouldNotBeNull();
        format.Schema.Value.GetRawText().ShouldContain("scope_split");
    }

    private async Task<MergeRuling> RuleAsync(
        string existingText = "existing wisdom", string candidateText = "candidate text")
    {
        var projectId = Guid.CreateVersion7();
        var existing = new Wisdom
        {
            Id = Guid.CreateVersion7(),
            Kind = WisdomKind.Preference,
            ScopeProjectId = projectId,
            Text = existingText,
            Embedding = new Vector(TestVectors.Basis),
        };
        var candidate = new WisdomCandidate(WisdomKind.Preference, projectId, candidateText);
        var arbiter = new MergeArbiter(_chat);
        return await arbiter.RuleAsync(existing, candidate, TestContext.Current.CancellationToken);
    }
}
