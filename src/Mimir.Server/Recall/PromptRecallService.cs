using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Recall;

internal sealed class PromptRecallService(
    QueryRanking ranking,
    InjectionLog injections,
    IOptions<RecallOptions> options)
{
    public async Task<string> ComposeInjectionAsync(
        string sessionId, Guid projectId, string prompt, CancellationToken cancellationToken)
    {
        var ranked = await ranking.RankAmbientAsync(prompt, projectId, cancellationToken);

        if (!ranked.Any(r => r.Cosine >= options.Value.PromptGateCosine))
        {
            return "";
        }

        return await injections.RenderAndRecordAsync(
            new InjectionContext(InjectionLane.Prompt, sessionId, projectId, prompt),
            ranked.Select(r => r.ToInjectionEntry()),
            options.Value.PromptBudgetChars,
            notice: null,
            cancellationToken);
    }
}
