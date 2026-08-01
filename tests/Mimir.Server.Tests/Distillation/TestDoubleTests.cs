using Mimir.Server.Distillation;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Distillation;

public sealed class TestDoubleTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnUnscriptedDistillerCall_Throws_SoOneCallPerEpisodeStaysFalsifiable()
    {
        var distiller = new FakeDistiller();
        distiller.Enqueue();

        await distiller.DistillAsync(Episode(), "github.com/test/one", [], Token);

        await Should.ThrowAsync<InvalidOperationException>(
            () => distiller.DistillAsync(Episode(), "github.com/test/one", [], Token));
    }

    [Fact]
    public async Task AnEnqueueWithNoCandidates_IsAnEpisodeDistilledToNone_NotAnUnscriptedCall()
    {
        var distiller = new FakeDistiller();
        distiller.Enqueue();

        (await distiller.DistillAsync(Episode(), "github.com/test/one", [], Token)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnUnscriptedArbiterCall_RulesAgreementOnTheExistingText()
    {
        var existing = new Wisdom
        {
            Id = Guid.CreateVersion7(),
            Kind = WisdomKind.Fact,
            ScopeProjectId = Project.GlobalId,
            Text = "the existing wording",
            Embedding = new Pgvector.Vector(TestVectors.Basis),
            LastConfirmedAt = DateTimeOffset.UnixEpoch,
        };

        var ruling = await new FakeArbiter().RuleAsync(
            existing,
            new WisdomCandidate(
                WisdomKind.Fact, Project.GlobalId, "a candidate that says the same thing"),
            Token);

        ruling.ShouldBeOfType<MergeRuling.Agreement>()
            .MergedText.ShouldBe("the existing wording", "the merge that changes no wording");
    }

    [Fact]
    public async Task IdenticalTextEmbedsIdentically_AndUnrelatedTextsLandFarBelowTheGate()
    {
        var embeddings = new FakeEmbeddings();

        var vectors = await embeddings.GenerateAsync(
            ["a thing worth remembering", "a thing worth remembering", "something else entirely"],
            cancellationToken: Token);

        var same = Cosine(vectors[0].Vector.Span, vectors[1].Vector.Span);
        var other = Cosine(vectors[0].Vector.Span, vectors[2].Vector.Span);

        same.ShouldBe(1.0, 1e-5, "identical text must sit above the 0.80 gate every run");
        Math.Abs(other).ShouldBeLessThan(0.2, "and unrelated text far below it");
    }

    [Fact]
    public async Task OnGenerateFiresAsTheBatchIsServed_SoATestCanChangeTheWorldAsAnAdmissionBegins()
    {
        var embeddings = new FakeEmbeddings();
        var served = new List<IReadOnlyList<string>>();
        embeddings.OnGenerate = served.Add;

        await embeddings.GenerateAsync(["one"], cancellationToken: Token);
        await embeddings.GenerateAsync(["two", "three"], cancellationToken: Token);

        served.ShouldBe(
            [["one"], ["two", "three"]],
            "once per batch, with that batch's own texts — a test reads what the gate is holding "
            + "at the moment it embeds");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.85)]
    [InlineData(0.79)]
    [InlineData(0.0)]
    public void WithCosine_AnswersAUnitVectorAtExactlyThatCosineFromTheBasis(double cosine)
    {
        var vector = TestVectors.WithCosine(cosine);

        Cosine(vector, TestVectors.Basis).ShouldBe(cosine, 1e-6);
        Math.Sqrt(vector.Sum(v => (double)v * v)).ShouldBe(1.0, 1e-6);
    }

    private static Episode Episode() => new()
    {
        Id = Guid.CreateVersion7(),
        SessionId = "sess-double",
        ProjectId = Project.GlobalId,
        StartedAt = DateTimeOffset.UnixEpoch,
        Cwd = @"C:\git\mimir-tests",
    };

    private static double Cosine(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += (double)left[i] * right[i];
            leftNorm += (double)left[i] * left[i];
            rightNorm += (double)right[i] * right[i];
        }

        return dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }
}
