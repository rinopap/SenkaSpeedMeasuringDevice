using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class ShipExpGainRateCalculatorTests
{
    [Fact]
    public void CalculatesClosedHoursAndCurrentPartialHourUsingLocalHourBoundaries()
    {
        var startLocal = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Unspecified);
        var now = AtLocal(14, 30);
        var start = AtLocal(12, 0);
        var end = now.ToUniversalTime();
        var coverage = new[] { Segment(start, end) };
        var events = new[]
        {
            Event(AtLocal(12, 30), 100, 1),
            Event(AtLocal(13, 0), 200, 2),
            Event(AtLocal(14, 0), 300, 3)
        };

        var buckets = ShipExpGainRateCalculator.CalculateThreeHours(events, coverage, now);

        Assert.Equal(startLocal.Hour, buckets[0].LocalStart.Hour);
        Assert.True(buckets[0].FullyCovered);
        Assert.Equal(100, buckets[0].GainedExp);
        Assert.Equal(100, buckets[0].ExpPerHour);
        Assert.True(buckets[1].FullyCovered);
        Assert.Equal(200, buckets[1].GainedExp);
        Assert.Equal(200, buckets[1].ExpPerHour);
        Assert.False(buckets[2].FullyCovered);
        Assert.Equal(300, buckets[2].GainedExp);
        Assert.Equal(600, buckets[2].ExpPerHour);
    }

    [Fact]
    public void NormalNoEventCoverageProducesZeroButUnavailableCoverageDoesNot()
    {
        var now = AtLocal(12, 30);
        var start = AtLocal(12, 0);
        var covered = ShipExpGainRateCalculator.CalculateThreeHours([], [Segment(start, now.ToUniversalTime())], now);
        Assert.Equal(0, covered[2].ExpPerHour);
        Assert.True(covered[2].CoveredHours > 0);

        var unavailable = ShipExpGainRateCalculator.CalculateThreeHours([], [], now);
        Assert.Null(unavailable[2].ExpPerHour);
        Assert.Equal(0, unavailable[2].CoveredHours);
    }

    [Fact]
    public void GapIsExcludedAndCannotBeReportedAsACompleteHour()
    {
        var start = AtLocal(13, 0);
        var now = AtLocal(14, 0);
        var coverage = new[]
        {
            Segment(start, AtLocal(13, 30).ToUniversalTime()),
            Segment(AtLocal(13, 45), now.ToUniversalTime())
        };
        var events = new[]
        {
            Event(AtLocal(13, 15), 90, 1),
            Event(AtLocal(13, 35), 500, 2),
            Event(AtLocal(13, 50), 60, 3)
        };

        var bucket = ShipExpGainRateCalculator.CalculateThreeHours(events, coverage, now)[1];

        Assert.False(bucket.FullyCovered);
        Assert.Equal(150, bucket.GainedExp);
        Assert.Equal(200, bucket.ExpPerHour);
    }

    [Fact]
    public void BoundaryEventBelongsToExactlyOneHalfOpenLocalHour()
    {
        var start = AtLocal(12, 0);
        var now = AtLocal(14, 20);
        var buckets = ShipExpGainRateCalculator.CalculateThreeHours(
            [Event(AtLocal(13, 0), 123, 1)], [Segment(start, now.ToUniversalTime())], now);
        Assert.Equal(0, buckets[0].GainedExp);
        Assert.Equal(123, buckets[1].GainedExp);
        Assert.Equal(0, buckets[2].GainedExp);
    }

    [Fact]
    public void SevenDigitExpRateIsFormattedWithoutTruncation()
    {
        var now = AtLocal(14, 30);
        var coverage = new[] { Segment(AtLocal(12, 0), now.ToUniversalTime()) };
        var events = new[] { Event(AtLocal(12, 30), 1_234_567, 100) };

        var buckets = ShipExpGainRateCalculator.CalculateThreeHours(events, coverage, now);
        var output = ShipExpGainRateCalculator.FormatBuckets(buckets);

        Assert.Equal(1_234_567, buckets[0].ExpPerHour);
        Assert.Contains("1,234,567", output);
    }

    [Fact]
    public void LateArrivingEventChangesItsOriginalTimestampBucket()
    {
        var start = AtLocal(12, 0);
        var now = AtLocal(14, 0);
        var coverage = new[] { Segment(start, now.ToUniversalTime()) };
        var before = ShipExpGainRateCalculator.CalculateThreeHours([], coverage, now);
        var after = ShipExpGainRateCalculator.CalculateThreeHours([Event(AtLocal(12, 45), 321, 9)], coverage, now);
        Assert.Equal(0, before[0].GainedExp);
        Assert.Equal(321, after[0].GainedExp);
    }

    [Fact]
    public void KnownUnparseableApiRecordMakesItsHourPartial()
    {
        var start = AtLocal(13, 0);
        var now = AtLocal(14, 0);
        var bucket = ShipExpGainRateCalculator.CalculateThreeHours([], [Segment(start, now.ToUniversalTime())], now,
            [AtLocal(13, 27)])[1];

        Assert.False(bucket.FullyCovered);
        Assert.Equal(0, bucket.GainedExp);
        Assert.Equal(0, bucket.ExpPerHour);
    }

    [Fact]
    public void BoundedUnknownIssueMakesOnlyItsPossibleHourUncertain()
    {
        var now = AtLocal(14, 30);
        var uncertain = new ShipExpIssueTimeWindow(AtLocal(13, 20), AtLocal(13, 40));
        var buckets = ShipExpGainRateCalculator.CalculateThreeHours([], [Segment(AtLocal(11, 0), now)], now,
            uncertainIssueWindows: [uncertain]);
        Assert.True(buckets[1].HasUncertainIssue);
        Assert.Null(buckets[1].ExpPerHour);
        Assert.False(buckets[2].HasUncertainIssue);

        var output = ShipExpGainRateCalculator.FormatThreeHours([], [Segment(AtLocal(11, 0), now)], now,
            uncertainIssueWindows: [uncertain]);
        Assert.Contains("Exp時速 未確定", output);
        Assert.DoesNotContain("艦娘API獲得", output);
        Assert.DoesNotContain("部分計測", output);
        Assert.Contains("記録時刻不明issue", ShipExpGainRateCalculator.FormatCoverageDiagnostics(buckets));
    }

    [Fact]
    public void UnboundedUnknownIssueRemainsUncertainAfterThreeHours()
    {
        var now = AtLocal(18, 30);
        var buckets = ShipExpGainRateCalculator.CalculateThreeHours([], [Segment(AtLocal(10, 0), now)], now,
            uncertainIssueWindows: [new ShipExpIssueTimeWindow(null, null)]);

        Assert.All(buckets, bucket => Assert.True(bucket.HasUncertainIssue));
        Assert.All(buckets, bucket => Assert.Null(bucket.ExpPerHour));
    }

    [Fact]
    public void PartialCoverageRemainsMarkedInternallyAndIsAvailableAsTooltipDiagnostic()
    {
        var now = AtLocal(14, 30);
        var bucket = ShipExpGainRateCalculator.CalculateThreeHours([], [Segment(AtLocal(14, 0), now)], now)[2];

        Assert.False(bucket.FullyCovered);
        Assert.Equal(0, bucket.ExpPerHour);
        var text = ShipExpGainRateCalculator.FormatThreeHours([], [Segment(AtLocal(14, 0), now)], now);
        Assert.DoesNotContain("部分計測", text);
        Assert.Contains("部分計測", ShipExpGainRateCalculator.FormatCoverageDiagnostics([bucket]));
    }

    private static DateTimeOffset AtLocal(int hour, int minute)
    {
        var local = new DateTime(2026, 11, 28, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static CaptureCoverageSegment Segment(DateTimeOffset start, DateTimeOffset end) =>
        new("generation-a", "series-a", start.ToUniversalTime(), end.ToUniversalTime());

    private static ShipExpGainEvent Event(DateTimeOffset at, long gain, long recordId) =>
        new($"event-{recordId}", "generation-a", "series-a", recordId, at.ToUniversalTime(), ShipExpGainKind.Sortie, gain);
}
