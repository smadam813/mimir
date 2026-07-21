using Mimir.Server.Harvest;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Harvest;

/// <summary>
/// The §5 mechanical conversion rules: H1/H2 sections, frontmatter <c>type</c> → kind, the
/// 2,000-char hard cap. No LLM anywhere — what goes in is what comes out, just partitioned.
/// </summary>
public class HarvestCandidatesTests
{
    private const int Cap = 2000;

    [Fact]
    public void HeadinglessFile_IsOneCandidate()
    {
        var candidates = HarvestCandidates.Of("just two lines\nof plain text", Cap);

        var candidate = candidates.ShouldHaveSingleItem();
        candidate.Text.ShouldBe("just two lines\nof plain text");
        candidate.Kind.ShouldBe(WisdomKind.Fact);
    }

    [Fact]
    public void H1AndH2_StartNewCandidates_WithThePreambleFirst()
    {
        var content = """
            preamble before any heading
            # First section
            body one
            ## Second section
            body two
            """;

        var candidates = HarvestCandidates.Of(content, Cap);

        candidates.Select(c => c.Text).ShouldBe(
        [
            "preamble before any heading",
            "# First section\nbody one",
            "## Second section\nbody two",
        ]);
    }

    [Fact]
    public void H3AndDeeper_StayInsideTheirSection()
    {
        var content = """
            # Section
            ### Sub-detail
            body
            """;

        var candidates = HarvestCandidates.Of(content, Cap);

        candidates.ShouldHaveSingleItem().Text.ShouldBe("# Section\n### Sub-detail\nbody");
    }

    [Fact]
    public void HashWithoutSpace_IsNotAHeading()
    {
        var candidates = HarvestCandidates.Of("#!/usr/bin/env bash\n#no space", Cap);

        candidates.ShouldHaveSingleItem();
    }

    [Fact]
    public void HeadingsInsideCodeFences_DoNotSplit()
    {
        var content = """
            # Section
            ```bash
            # a bash comment, not a heading
            ```
            after
            """;

        var candidates = HarvestCandidates.Of(content, Cap);

        candidates.ShouldHaveSingleItem()
            .Text.ShouldBe("# Section\n```bash\n# a bash comment, not a heading\n```\nafter");
    }

    [Theory]
    [InlineData("user", WisdomKind.Preference)]
    [InlineData("feedback", WisdomKind.Lesson)]
    [InlineData("project", WisdomKind.Fact)]
    [InlineData("reference", WisdomKind.Fact)]
    [InlineData("something-else", WisdomKind.Fact)]
    public void FrontmatterType_MapsToKind(string type, WisdomKind expected)
    {
        var content = $"""
            ---
            name: some-memory
            metadata:
              type: {type}
            ---

            The remembered fact.
            """;

        var candidate = HarvestCandidates.Of(content, Cap).ShouldHaveSingleItem();

        candidate.Kind.ShouldBe(expected);
        candidate.Text.ShouldBe("The remembered fact.");
    }

    [Fact]
    public void TopLevelFrontmatterType_AlsoMaps()
    {
        var content = """
            ---
            type: feedback
            ---
            Lesson learned.
            """;

        HarvestCandidates.Of(content, Cap).ShouldHaveSingleItem().Kind.ShouldBe(WisdomKind.Lesson);
    }

    [Fact]
    public void TheKind_AppliesToEverySectionOfTheFile()
    {
        var content = """
            ---
            type: user
            ---
            # One
            a
            # Two
            b
            """;

        HarvestCandidates.Of(content, Cap).ShouldAllBe(c => c.Kind == WisdomKind.Preference);
    }

    [Fact]
    public void UnclosedFrontmatter_IsTreatedAsBody()
    {
        var content = "---\ntype: user\nno closing fence";

        var candidate = HarvestCandidates.Of(content, Cap).ShouldHaveSingleItem();

        candidate.Kind.ShouldBe(WisdomKind.Fact);
        candidate.Text.ShouldBe(content);
    }

    [Fact]
    public void BlankSections_ProduceNoCandidates()
    {
        var content = """
            # Empty


            # Full
            body
            """;

        var candidates = HarvestCandidates.Of("\n\n", Cap);
        candidates.ShouldBeEmpty();

        HarvestCandidates.Of(content, Cap).Select(c => c.Text)
            .ShouldBe(["# Empty", "# Full\nbody"]);
    }

    [Fact]
    public void OversizedSections_AreHardCappedAtTheLimit()
    {
        var candidates = HarvestCandidates.Of(new string('x', 250), cap: 100);

        candidates.ShouldHaveSingleItem().Text.Length.ShouldBe(100);
    }

    [Fact]
    public void WindowsLineEndings_AreHandled()
    {
        var content = "---\r\ntype: user\r\n---\r\n# A\r\nbody\r\n# B\r\nmore\r\n";

        var candidates = HarvestCandidates.Of(content, Cap);

        candidates.Count.ShouldBe(2);
        candidates[0].Kind.ShouldBe(WisdomKind.Preference);
        candidates[0].Text.ShouldBe("# A\nbody");
    }
}
