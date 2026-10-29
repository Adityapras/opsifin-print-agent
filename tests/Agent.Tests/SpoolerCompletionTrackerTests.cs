using Agent.Printing;

namespace Agent.Tests;

public sealed class SpoolerCompletionTrackerTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HealthyJob_MustRemainHealthyForFullVerificationWindow()
    {
        var tracker = new SpoolerCompletionTracker(TimeSpan.FromSeconds(60));

        Assert.Null(tracker.Observe(Present(pagesPrinted: 2), StartedAt));
        Assert.Null(tracker.Observe(Missing(), StartedAt.AddSeconds(1)));
        Assert.Null(tracker.Observe(Missing(), StartedAt.AddSeconds(60)));

        var result = tracker.Observe(Missing(), StartedAt.AddSeconds(61));

        Assert.NotNull(result);
        Assert.Equal(SpoolerMonitorOutcome.Completed, result.Outcome);
        Assert.Equal(2u, result.PagesPrinted);
    }

    [Fact]
    public void JobThatWasNeverObserved_IsNotAssumedCompleted()
    {
        var tracker = new SpoolerCompletionTracker(TimeSpan.FromSeconds(60));

        var result = tracker.Observe(Missing(), StartedAt);

        Assert.NotNull(result);
        Assert.Equal(SpoolerMonitorOutcome.NotObserved, result.Outcome);
    }

    [Theory]
    [InlineData(0x00000002)] // ERROR
    [InlineData(0x00000004)] // DELETING
    [InlineData(0x00000100)] // DELETED
    public void FatalJobStatus_IsRememberedAfterJobDisappears(uint jobStatus)
    {
        var tracker = new SpoolerCompletionTracker(TimeSpan.FromSeconds(60));
        Assert.Null(tracker.Observe(Present(jobStatus), StartedAt));

        var result = tracker.Observe(Missing(), StartedAt.AddSeconds(1));

        Assert.NotNull(result);
        Assert.Equal(SpoolerMonitorOutcome.Failed, result.Outcome);
        Assert.Equal(jobStatus, result.LastJobStatus);
    }

    [Theory]
    [InlineData(0x00000008, 0)] // PAPER_JAM
    [InlineData(0x00000010, 0)] // PAPER_OUT
    [InlineData(0x00000080, 0)] // OFFLINE
    [InlineData(0, 0x00000400)] // WORK_OFFLINE
    public void PrinterErrorDuringVerification_IsPrintedUnverified(
        uint printerStatus,
        uint printerAttributes)
    {
        var tracker = new SpoolerCompletionTracker(TimeSpan.FromSeconds(60));
        Assert.Null(tracker.Observe(Present(), StartedAt));
        Assert.Null(tracker.Observe(Missing(), StartedAt.AddSeconds(1)));

        var result = tracker.Observe(
            Missing(printerStatus, printerAttributes),
            StartedAt.AddSeconds(20));

        Assert.NotNull(result);
        Assert.Equal(SpoolerMonitorOutcome.PrintedUnverified, result.Outcome);
        Assert.Equal(printerStatus, result.LastPrinterStatus);
    }

    [Fact]
    public void PrinterErrorWhileJobStillExists_DoesNotEndMonitoring()
    {
        var tracker = new SpoolerCompletionTracker(TimeSpan.Zero);

        Assert.Null(tracker.Observe(Present(printerStatus: 0x00000010), StartedAt));
        var result = tracker.Observe(Missing(), StartedAt.AddSeconds(1));

        Assert.NotNull(result);
        Assert.Equal(SpoolerMonitorOutcome.Completed, result.Outcome);
    }

    [Fact]
    public void NegativeVerificationWindow_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SpoolerCompletionTracker(TimeSpan.FromSeconds(-1)));
    }

    private static SpoolerObservation Present(
        uint jobStatus = 0,
        uint printerStatus = 0,
        uint printerAttributes = 0,
        uint pagesPrinted = 0) =>
        new(true, jobStatus, printerStatus, printerAttributes, pagesPrinted, 2);

    private static SpoolerObservation Missing(
        uint printerStatus = 0,
        uint printerAttributes = 0) =>
        new(false, 0, printerStatus, printerAttributes, 0, 0);
}
