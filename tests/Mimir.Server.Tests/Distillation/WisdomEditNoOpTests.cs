using Mimir.Server.Distillation;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// The §8.1 edit's no-op set, which is exactly three (§6) and now has one statement rather than
/// two: <see cref="MergeGate.EditAsync"/> settles each of its three decision points here, and the
/// Save button's wording reads the same answer. Pure, and deliberately not on
/// <see cref="MergeGateTests"/>: that class needs Postgres, and a rule that decides whether a
/// button is clickable must be pinned where it still runs on a machine without one.
/// </summary>
public sealed class WisdomEditNoOpTests
{
    [Fact]
    public void BlankText_IsANoOp_WhateverTheWisdomSays()
    {
        MergeGate.NoOpOf("", "a standing line").ShouldBe(WisdomEditNoOp.Blank);
        MergeGate.NoOpOf("   \r\n  ", "a standing line").ShouldBe(WisdomEditNoOp.Blank);

        // The gate asks with no row read, so that it can settle this one before touching the
        // database: blank has to answer Blank rather than Unknown there.
        MergeGate.NoOpOf("  ", current: null).ShouldBe(WisdomEditNoOp.Blank);
    }

    [Fact]
    public void AnIdNothingAnswersTo_IsANoOp()
        => MergeGate.NoOpOf("a rewording", current: null).ShouldBe(WisdomEditNoOp.Unknown);

    /// <summary>
    /// The draft is trimmed and the stored text is not, because only the edit path trims what it
    /// writes: a Wisdom whose text carries whitespace has an edit that would legitimately strip it,
    /// and trimming both sides would call that edit a no-op and refuse it.
    /// </summary>
    [Fact]
    public void TextAlreadySaying_ThisIsANoOp_ComparedAgainstWhatIsStored()
    {
        MergeGate.NoOpOf("a standing line", "a standing line").ShouldBe(WisdomEditNoOp.Unchanged);
        MergeGate.NoOpOf("  a standing line  ", "a standing line").ShouldBe(WisdomEditNoOp.Unchanged);
        MergeGate.NoOpOf("a standing line", "a standing line  ").ShouldBeNull();
    }

    [Fact]
    public void ARealRewording_IsNotANoOp()
        => MergeGate.NoOpOf("a better line", "a standing line").ShouldBeNull();
}
