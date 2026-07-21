using System.Text.RegularExpressions;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Harvest;

/// <summary>One §5 mechanical candidate: a section of a memory file, bound for the Merge Gate.</summary>
internal sealed record HarvestCandidate(WisdomKind Kind, string Text);

/// <summary>
/// The §5 mechanical candidate conversion — no LLM. A changed HarvestedItem splits on markdown
/// H1/H2 sections (a headingless file is one candidate), hard-capped per candidate; the kind comes
/// from the frontmatter <c>type</c> and applies to the whole file. The Merge Gate's LLM rewrite is
/// what eventually compacts oversized text — here it is simply cut.
/// </summary>
internal static partial class HarvestCandidates
{
    public static IReadOnlyList<HarvestCandidate> Of(string content, int cap)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var bodyStart = FrontmatterEnd(lines);
        var kind = KindOf(lines[..bodyStart]);

        var candidates = new List<HarvestCandidate>();
        var section = new List<string>();

        void Flush()
        {
            var text = string.Join('\n', section).Trim();
            section.Clear();
            if (text.Length > 0)
            {
                candidates.Add(new HarvestCandidate(kind, Capped(text, cap)));
            }
        }

        var inFence = false;
        foreach (var line in lines[bodyStart..])
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }
            else if (!inFence && IsSectionHeading(line))
            {
                Flush();
            }

            section.Add(line);
        }

        Flush();
        return candidates;
    }

    /// <summary>H1/H2 start a new candidate (§5); deeper headings stay inside their section.</summary>
    private static bool IsSectionHeading(string line)
        => line.StartsWith("# ", StringComparison.Ordinal)
            || line.StartsWith("## ", StringComparison.Ordinal);

    /// <summary>
    /// The first body line: past the closing <c>---</c> of a leading frontmatter block, or 0 when
    /// there is none (an unclosed fence is body, not frontmatter that swallows the file).
    /// </summary>
    private static int FrontmatterEnd(string[] lines)
    {
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return 0;
        }

        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// The §5 kind mapping over the frontmatter <c>type</c>, wherever it sits — auto-memory nests
    /// it under <c>metadata:</c>, so any indented <c>type:</c> line counts. Unknown or absent
    /// falls to Fact.
    /// </summary>
    private static WisdomKind KindOf(string[] frontmatter)
    {
        var type = frontmatter
            .Select(line => TypeLine().Match(line))
            .FirstOrDefault(m => m.Success)?.Groups[1].Value.Trim().Trim('"', '\'');

        return type?.ToLowerInvariant() switch
        {
            "user" => WisdomKind.Preference,
            "feedback" => WisdomKind.Lesson,
            "project" or "reference" => WisdomKind.Fact,
            _ => WisdomKind.Fact,
        };
    }

    private static string Capped(string text, int cap)
    {
        if (text.Length <= cap)
        {
            return text;
        }

        // Never cut a surrogate pair in half — a lone surrogate is not encodable UTF-8.
        var length = char.IsHighSurrogate(text[cap - 1]) ? cap - 1 : cap;
        return text[..length];
    }

    [GeneratedRegex(@"^\s*type:\s*(.+)$")]
    private static partial Regex TypeLine();
}
