using MapCue.Domain;
using MapCue.Domain.Graph;
using MapCue.Infrastructure.Sqlite;
using MapCue.Testing;

namespace MapCue.Infrastructure.Tests.Graph;

public class AgentCallStoreTests
{
    private static AgentCall Call(string id, string stateId, int n, AgentCallStatus status, long? input, decimal? cost) => new(
        id,
        "req_1",
        n,
        GraphHarness.Basic.ContextId,
        stateId,
        "task_1",
        "att_1",
        "fake",
        "mapcue.agent-prompt/1",
        "sha256:" + new string('1', 64),
        "sha256:" + new string('2', 64),
        status == AgentCallStatus.BudgetExhausted ? null : "sha256:" + new string('3', 64),
        status,
        status == AgentCallStatus.Accepted ? [] : ["schema /: is missing required property 'facts'"],
        [new AgentCandidateDecision("c1", CandidateVerdict.SkippedByPolicy, "sha256:" + new string('4', 64), RiskClass.Navigational, RiskClass.Financial, RiskClass.Financial, ["keyword:transfer*"])],
        input,
        input is null ? null : 7,
        cost,
        12,
        ManualClock.DefaultStart);

    [Fact]
    public async Task Agent_calls_round_trip_and_sum_usage()
    {
        await using var h = GraphHarness.Create();
        var home = await h.RecordAsync(GraphHarness.Home());
        var state = (await h.Committer.CommitObservationAsync(GraphHarness.Basic, home, CancellationToken.None)).StateId;
        var first = Call("call_1", state, 1, AgentCallStatus.SchemaInvalid, 100, 0.01m);
        var second = Call("call_2", state, 2, AgentCallStatus.Accepted, null, null);
        var skipped = Call("call_3", state, 3, AgentCallStatus.BudgetExhausted, null, null);

        await h.Sqlite.InTransactionAsync(
            async uow =>
            {
                Assert.True(await uow.AgentCalls.InsertIfAbsentAsync(first, CancellationToken.None));
                Assert.True(await uow.AgentCalls.InsertIfAbsentAsync(second, CancellationToken.None));
                Assert.True(await uow.AgentCalls.InsertIfAbsentAsync(skipped, CancellationToken.None));
                Assert.False(await uow.AgentCalls.InsertIfAbsentAsync(first, CancellationToken.None));
                return 0;
            },
            CancellationToken.None);

        var calls = await h.ReadAsync(uow => uow.AgentCalls.ListAsync(state, CancellationToken.None));
        Assert.Equal(["call_1", "call_2", "call_3"], calls.Select(c => c.CallId));
        Assert.Equal(first.Errors, calls[0].Errors);
        var decision = calls[0].Decisions.Single();
        Assert.Equal(first.Decisions.Single() with { Reasons = [] }, decision with { Reasons = [] });
        Assert.Equal(first.Decisions.Single().Reasons, decision.Reasons);
        Assert.Equal((0.01m, 100L, 7L), (calls[0].CostUsd!.Value, calls[0].InputTokens!.Value, calls[0].OutputTokens!.Value));
        Assert.Null(calls[1].CostUsd);

        // Budget-exhausted rows are records of calls that were not made.
        Assert.Equal(new AgentUsageTotals(2, 107, 0.01m, 1), await h.ReadAsync(uow => uow.AgentCalls.GetUsageTotalsAsync(CancellationToken.None)));
        Assert.True(SqliteMigrator.LatestVersion >= 3);
    }
}
