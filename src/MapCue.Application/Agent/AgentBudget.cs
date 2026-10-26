using System.Text;
using MapCue.Application.Configuration;
using MapCue.Application.Ports;
using MapCue.Domain.Graph;

namespace MapCue.Application.Agent;

/// <summary>
/// Reasoner budget of a run (architecture §10.2: <c>agent.maxCalls</c>, <c>agent.maxTokens</c>, <c>agent.maxCostUsd</c>). A call is
/// made only if it fits. A reasoner that reports no tokens is charged an estimate (about 4 bytes per token of request and answer),
/// so the token limit works for every provider; a call without a reported cost is counted as unknown cost.
/// </summary>
public sealed class AgentBudget
{
    private readonly AgentSettings _settings;
    private readonly Lock _gate = new();
    private AgentUsageTotals _used;

    public AgentBudget(AgentSettings settings, AgentUsageTotals? alreadyUsed = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _used = alreadyUsed ?? AgentUsageTotals.Zero;
    }

    public AgentUsageTotals Used
    {
        get
        {
            lock (_gate)
            {
                return _used;
            }
        }
    }

    public static long EstimateTokens(string? text) => text is null ? 0 : (Encoding.UTF8.GetByteCount(text) + 3) / 4;

    /// <summary>Null when a call with about <paramref name="estimatedTokens"/> tokens fits; otherwise which limit stops it.</summary>
    public string? Check(long estimatedTokens)
    {
        lock (_gate)
        {
            if (_used.Calls >= _settings.MaxCalls)
            {
                return $"agent.maxCalls ({_settings.MaxCalls}) reached";
            }

            if (_used.Tokens + estimatedTokens > _settings.MaxTokens)
            {
                return $"agent.maxTokens ({_settings.MaxTokens}) would be exceeded";
            }

            return _used.CostUsd >= _settings.MaxCostUsd ? $"agent.maxCostUsd ({_settings.MaxCostUsd}) reached" : null;
        }
    }

    /// <summary>Charges a finished call; <paramref name="estimatedTokens"/> is used when the reasoner reported no tokens.</summary>
    public void Record(ReasonerUsage usage, long estimatedTokens)
    {
        ArgumentNullException.ThrowIfNull(usage);
        lock (_gate)
        {
            var tokens = usage.InputTokens is null && usage.OutputTokens is null ? estimatedTokens : usage.Tokens;
            _used = new AgentUsageTotals(
                _used.Calls + Math.Max(usage.Calls, 1),
                _used.Tokens + tokens,
                _used.CostUsd + (usage.CostUsd ?? 0m),
                _used.UnknownCostCalls + (usage.CostUsd is null ? 1 : 0));
        }
    }
}
