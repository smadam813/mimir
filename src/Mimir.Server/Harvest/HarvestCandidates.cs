using System.Text.RegularExpressions;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Harvest;

internal sealed record HarvestCandidate(WisdomKind Kind, string Text);

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

        var length = char.IsHighSurrogate(text[cap - 1]) ? cap - 1 : cap;
        return text[..length];
    }

    [GeneratedRegex(@"^\s*type:\s*(.+)$")]
    private static partial Regex TypeLine();

    [GeneratedRegex(@"^\s*([A-Za-z0-9_.-]+\s*:(\s|$)|-\s)")]
    private static partial Regex FrontmatterLine();
}
