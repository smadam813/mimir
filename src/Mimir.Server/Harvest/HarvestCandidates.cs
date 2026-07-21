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
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var bodyStart = FrontmatterEnd(lines);
        var kind = KindOf(lines[..bodyStart]);

        var candidates = new List<HarvestCandidate>();
        var section = new List<string>();

        void Flush()
        {
            var text = string.Join('\n', section).Trim();
            section.Clear();
            // The capped text is what must be non-empty: a cap of 1 landing on a surrogate
            // pair caps to nothing, and an empty candidate has no business at the gate.
            if (text.Length > 0 && Capped(text, cap) is { Length: > 0 } capped)
            {
                candidates.Add(new HarvestCandidate(kind, capped));
            }
        }

        FenceRun? fence = null;
        foreach (var line in lines[bodyStart..])
        {
            if (FenceRun.Of(line) is { } run)
            {
                if (fence is null)
                {
                    fence = run;
                }
                else if (run.Closes(fence.Value))
                {
                    fence = null;
                }
            }
            else if (fence is null && IsSectionHeading(line))
            {
                Flush();
            }

            section.Add(line);
        }

        Flush();
        return candidates;
    }

    /// <summary>
    /// H1/H2 start a new candidate (§5); deeper headings stay inside their section. Up to three
    /// leading spaces still make a heading; four make an indented code block (CommonMark).
    /// </summary>
    private static bool IsSectionHeading(string line)
    {
        var indent = LeadingSpaces(line);
        if (indent > 3)
        {
            return false;
        }

        var rest = line.AsSpan(indent);
        return rest.StartsWith("# ") || rest.StartsWith("## ");
    }

    /// <summary>
    /// A backtick or tilde code-fence delimiter line: fences only close on the same delimiter at
    /// the opening run's length or more, so nested fences of differing length and tilde fences
    /// both keep their <c>#</c> lines from splitting sections.
    /// </summary>
    private readonly record struct FenceRun(char Delimiter, int Length, bool Bare)
    {
        public static FenceRun? Of(string line)
        {
            var indent = LeadingSpaces(line);
            if (indent > 3 || indent == line.Length || (line[indent] != '`' && line[indent] != '~'))
            {
                return null;
            }

            var delimiter = line[indent];
            var end = indent;
            while (end < line.Length && line[end] == delimiter)
            {
                end++;
            }

            return end - indent >= 3
                ? new FenceRun(delimiter, end - indent, line[end..].Trim().Length == 0)
                : null;
        }

        /// <summary>A closing fence is bare (no info string), matching the opener's delimiter.</summary>
        public bool Closes(FenceRun open)
            => Bare && Delimiter == open.Delimiter && Length >= open.Length;
    }

    private static int LeadingSpaces(string line)
    {
        var i = 0;
        while (i < line.Length && line[i] == ' ')
        {
            i++;
        }

        return i;
    }

    /// <summary>
    /// The first body line: past the closing <c>---</c> of a leading frontmatter block, or 0 when
    /// there is none. Only a block of YAML-mapping-shaped lines counts as frontmatter — a file
    /// that merely opens with a <c>---</c> horizontal rule is all body, never silently swallowed.
    /// (An unclosed block is body too.)
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

            if (lines[i].Trim().Length > 0 && !FrontmatterLine().IsMatch(lines[i]))
            {
                return 0;
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

    /// <summary>A key or list item, the two line shapes real frontmatter is made of.</summary>
    [GeneratedRegex(@"^\s*([A-Za-z0-9_.-]+\s*:(\s|$)|-\s)")]
    private static partial Regex FrontmatterLine();
}
