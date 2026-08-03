using System.Reflection;
using Microsoft.Extensions.Configuration;
using Mimir.Server.Configuration;

namespace Mimir.Server.Tests.Configuration;

/// <summary>
/// The shipped <c>appsettings.json</c> restates the §11 defaults that live in the options classes,
/// and this is what fails on drift between the two. Swept by convention rather than by a
/// hand-written list per section: a hand-written list covers only the knobs somebody remembered,
/// which is how <c>PromptBudgetChars</c>, <c>PromptGateCosine</c>, <c>AffinityBoost</c> and
/// <c>GoldenSetK</c> came to be asserted nowhere ([#140](https://github.com/smadam813/mimir/issues/140)).
/// A section or a knob added tomorrow is covered the day it lands.
/// <para>
/// Reads the file out of the test project's own output, so it issues no SQL and needs no Docker.
/// </para>
/// </summary>
public class AppSettingsTests
{
    private static readonly IConfiguration AppSettings =
        new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();

    /// <summary>
    /// Every §11 section, keyed by the name it binds under. The convention is the whole discovery
    /// rule: an options class sits in <c>Mimir.Server.Configuration</c> and carries a
    /// <c>SectionName</c> const, which is exactly what <c>MimirOptionsRegistration</c> binds it by.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Type> Sections = typeof(ServerOptions).Assembly
        .GetTypes()
        .Where(type =>
            type.Namespace == typeof(ServerOptions).Namespace && type is { IsClass: true, IsAbstract: false })
        .Select(type => (Type: type, Section: SectionNameOf(type)))
        .Where(candidate => candidate.Section is not null)
        .ToDictionary(candidate => candidate.Section!, candidate => candidate.Type, StringComparer.Ordinal);

    public static TheoryData<string> SectionNames => new(Sections.Keys.Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(SectionNames))]
    public void EveryShippedKnob_CarriesTheCodeDefault(string sectionName)
    {
        var options = Sections[sectionName];
        var shipped = AppSettings.GetSection(sectionName).Get(options).ShouldNotBeNull();
        var expected = Activator.CreateInstance(options).ShouldNotBeNull();

        foreach (var knob in Bound(options))
        {
            knob.GetValue(shipped).ShouldBe(knob.GetValue(expected), $"{sectionName}:{knob.Name} has drifted");
        }
    }

    /// <summary>
    /// Read as a set, in both directions. A knob the file omits binds to its code default and so
    /// passes the drift check above while shipping nothing a reader can find; a key the file keeps
    /// after the knob behind it is renamed is dead config that binds to nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(SectionNames))]
    public void EveryKnob_IsSpeltOutInTheShippedFile(string sectionName)
    {
        var keys = AppSettings.GetSection(sectionName).GetChildren().Select(child => child.Key);

        keys.ShouldBe(Bound(Sections[sectionName]).Select(knob => knob.Name), ignoreOrder: true);
    }

    private static string? SectionNameOf(Type type)
        => type.GetField(
                nameof(ServerOptions.SectionName),
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                is { IsLiteral: true } field
            && field.FieldType == typeof(string)
            ? (string?)field.GetRawConstantValue()
            : null;

    /// <summary>
    /// The knobs configuration binding actually writes — which is what the file has to restate.
    /// A get-only property (<c>ModelOptions.Provisioned</c>) is derived from them, never bound, so
    /// demanding a key for it would demand a key the binder ignores.
    /// </summary>
    private static IEnumerable<PropertyInfo> Bound(Type options)
        => options.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(knob => knob.CanWrite);
}
