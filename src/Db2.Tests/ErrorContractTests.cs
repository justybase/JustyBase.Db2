using JustyBase.Db2;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class ErrorContractTests
{
    [Fact]
    public void ConfirmedInterruptIsAnOperationCancellation()
    {
        var sql = new Db2SqlException(-952, "57014", "DB2 SQLCODE=-952 SQLSTATE=57014");
        Assert.Equal(Db2ErrorKind.OperationCancelled, Assert.IsAssignableFrom<IDb2Error>(sql).ErrorKind);
        Assert.Equal(Db2OperationOutcome.Confirmed, Assert.IsAssignableFrom<IDb2Error>(sql).Outcome);

        var diagnostic = new Db2ServerErrorException("SQLERRP:SQLRI2D0 SQLCODE:-952 SQLSTATE:57014");
        Assert.Equal(Db2ErrorKind.OperationCancelled, Assert.IsAssignableFrom<IDb2Error>(diagnostic).ErrorKind);
        Assert.Equal(Db2OperationOutcome.Confirmed, Assert.IsAssignableFrom<IDb2Error>(diagnostic).Outcome);
    }

    [Fact]
    public void NonCancellationSqlErrorKeepsSqlKind()
    {
        var error = new Db2SqlException(-204, "42704", "DB2 SQLCODE=-204 SQLSTATE=42704");
        Assert.Equal(Db2ErrorKind.SqlError, Assert.IsAssignableFrom<IDb2Error>(error).ErrorKind);
        Assert.Equal(Db2OperationOutcome.Confirmed, Assert.IsAssignableFrom<IDb2Error>(error).Outcome);
    }

    [Fact]
    public void TimeoutOutcomeFollowsRequestWritePhase()
    {
        var beforeWrite = new Db2CommandTimeoutException(TimeSpan.FromSeconds(1), null, Db2OperationOutcome.NotStarted);
        Assert.Equal(Db2ErrorKind.Timeout, Assert.IsAssignableFrom<IDb2Error>(beforeWrite).ErrorKind);
        Assert.Equal(Db2OperationOutcome.NotStarted, Assert.IsAssignableFrom<IDb2Error>(beforeWrite).Outcome);

        var afterWrite = new Db2CommandTimeoutException(TimeSpan.FromSeconds(1), null, Db2OperationOutcome.Unknown);
        Assert.Equal(Db2OperationOutcome.Unknown, Assert.IsAssignableFrom<IDb2Error>(afterWrite).Outcome);

        var confirmed = new Db2CommandTimeoutException(
            TimeSpan.FromSeconds(1),
            new Db2SqlException(-952, "57014", "DB2 SQLCODE=-952 SQLSTATE=57014"),
            Db2OperationOutcome.Unknown);
        Assert.Equal(Db2ErrorKind.Timeout, Assert.IsAssignableFrom<IDb2Error>(confirmed).ErrorKind);
        Assert.Equal(Db2OperationOutcome.Confirmed, Assert.IsAssignableFrom<IDb2Error>(confirmed).Outcome);
        Assert.Equal(-952, Assert.IsAssignableFrom<IDb2Error>(confirmed).SqlCode);
    }

    [Fact]
    public void BatchExecutionOutcomeFollowsTheFailingSet()
    {
        var setup = new Db2BatchExecutionException(
            null, 0, 0, new Db2SqlException(-301, "42884", "DB2 SQLCODE=-301 SQLSTATE=42884"));
        Assert.Equal(Db2OperationOutcome.Confirmed, Assert.IsAssignableFrom<IDb2Error>(setup).Outcome);

        var conversion = new Db2BatchExecutionException(
            2, 2, 2, new Db2UnsupportedFeatureException("unsupported parameter type"));
        Assert.Equal(Db2OperationOutcome.NotStarted, Assert.IsAssignableFrom<IDb2Error>(conversion).Outcome);

        var uncertain = new Db2BatchExecutionException(
            2, 2, 2, new Db2ConnectionClosedException("transport closed", Db2OperationOutcome.Unknown));
        Assert.Equal(Db2ErrorKind.BatchExecution, Assert.IsAssignableFrom<IDb2Error>(uncertain).ErrorKind);
        Assert.Equal(Db2OperationOutcome.Unknown, Assert.IsAssignableFrom<IDb2Error>(uncertain).Outcome);
    }

    [Fact]
    public void BatchCancellationOutcomeSeparatesIdleAndInFlightSets()
    {
        using var source = new CancellationTokenSource();
        var idle = new Db2BatchCancellationException(source.Token, 0, null, 0);
        Assert.Equal(Db2ErrorKind.OperationCancelled, Assert.IsAssignableFrom<IDb2Error>(idle).ErrorKind);
        Assert.Equal(Db2OperationOutcome.NotStarted, Assert.IsAssignableFrom<IDb2Error>(idle).Outcome);

        var inFlight = new Db2BatchCancellationException(source.Token, 1, 1, 1);
        Assert.Equal(Db2OperationOutcome.Unknown, Assert.IsAssignableFrom<IDb2Error>(inFlight).Outcome);
    }

    [Fact]
    public void DiscardedAfterUnconfirmedSettlementIsUnknown()
    {
        var error = new Db2ConnectionClosedException("DB2 SQLINTR could not be confirmed.", Db2OperationOutcome.Unknown);
        Assert.Equal(Db2ErrorKind.ConnectionClosed, Assert.IsAssignableFrom<IDb2Error>(error).ErrorKind);
        Assert.Equal(Db2OperationOutcome.Unknown, Assert.IsAssignableFrom<IDb2Error>(error).Outcome);
    }
}
