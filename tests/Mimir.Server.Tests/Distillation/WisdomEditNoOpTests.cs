using Mimir.Server.Distillation;

namespace Mimir.Server.Tests.Distillation;

public sealed class WisdomEditNoOpTests
{
    [Fact]
    public void BlankText_IsANoOp_WhateverTheWisdomSays()
    {
        MergeGate.NoOpOf("", "a standing line").ShouldBe(WisdomEditNoOp.Blank);
        MergeGate.NoOpOf("   \r\n  ", "a standing line").ShouldBe(WisdomEditNoOp.Blank);

        MergeGate.NoOpOf("  ", current: null).ShouldBe(WisdomEditNoOp.Blank);
    }

    [Fact]
    public void AnIdNothingAnswersTo_IsANoOp()
        => MergeGate.NoOpOf("a rewording", current: null).ShouldBe(WisdomEditNoOp.Unknown);

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
