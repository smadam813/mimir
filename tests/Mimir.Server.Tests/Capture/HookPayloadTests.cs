using System.Text.Json;
using Mimir.Server.Capture;

namespace Mimir.Server.Tests.Capture;

/// <summary>
/// Spec §4: the payload is the hook's untouched stdin JSON, so every read of it has to survive a
/// malformed hook — absent, null, not an object, or not a string all answer null.
/// </summary>
public sealed class HookPayloadTests
{
    [Fact]
    public void APresentStringField_IsRead()
    {
        Read("""{"reason":"exit"}""", "reason").ShouldBe("exit");
    }

    [Fact]
    public void AnAbsentField_ReadsAsNull()
    {
        Read("""{"prompt":"hello"}""", "reason").ShouldBeNull();
    }

    [Fact]
    public void AJsonNullField_ReadsAsNull()
    {
        Read("""{"reason":null}""", "reason").ShouldBeNull();
    }

    [Fact]
    public void ANonStringField_ReadsAsNull()
    {
        Read("""{"reason":42}""", "reason").ShouldBeNull();
        Read("""{"reason":{"nested":"exit"}}""", "reason").ShouldBeNull();
    }

    [Theory]
    [InlineData("\"just a string\"")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("7")]
    public void APayloadThatIsNotAnObject_ReadsAsNull(string payloadJson)
    {
        Read(payloadJson, "reason").ShouldBeNull();
    }

    private static string? Read(string payloadJson, string name)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.StringProperty(name);
    }
}
