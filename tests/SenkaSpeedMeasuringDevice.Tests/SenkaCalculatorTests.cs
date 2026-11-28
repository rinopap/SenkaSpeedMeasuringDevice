using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class SenkaCalculatorTests
{
    [Fact]
    public void CalculatesReferenceSenkaRate()
    {
        var start = Sample("08:00", 1_000_000);
        var end = Sample("08:30", 1_030_000);
        Assert.Equal(42.0, SenkaCalculator.Rate(start, end, senka: true));
        Assert.Equal(60_000.0, SenkaCalculator.Rate(start, end, senka: false));
    }

    [Fact]
    public void RejectsInsufficientNonIncreasingOrDecreasingSamples()
    {
        var start = Sample("08:00", 100);
        Assert.Null(SenkaCalculator.Rate(null, start, senka: true));
        Assert.Null(SenkaCalculator.Rate(start, Sample("08:00", 200), senka: true));
        Assert.Null(SenkaCalculator.Rate(start, Sample("08:30", 99), senka: true));
    }

    [Fact]
    public void FormatsThreeHourDisplayAndReportsMissingData()
    {
        var start = Sample("08:00", 1_000_000);
        var end = Sample("08:30", 1_030_000);
        Assert.Equal(42.0, SenkaCalculator.Rate(start, end, senka: true));
        Assert.Contains("08:00~08:30 戦果時速 42.00", SenkaCalculator.FormatThreeHours([start, end],
            DateTimeOffset.Parse("2026-11-26T08:30:00+09:00"), ship: false));
        Assert.Equal("データなし", SenkaCalculator.FormatThreeHours([], DateTimeOffset.Now, ship: false));
    }

    [Fact]
    public void DoesNotJoinSamplesAcrossExplicitMeasurementSeries()
    {
        var previousSeries = new ExperienceSample(
            DateTimeOffset.Parse("2026-11-26T09:10:00+09:00"), 2_000_000, "account-a");
        var currentSeries = new ExperienceSample(
            DateTimeOffset.Parse("2026-11-26T10:10:00+09:00"), 2_000_100, "account-b");

        var result = SenkaCalculator.FormatThreeHours([previousSeries, currentSeries],
            DateTimeOffset.Parse("2026-11-26T10:30:00+09:00"), ship: false);

        Assert.Contains("09:00~10:00 戦果時速 記録不足", result);
    }

    private static ExperienceSample Sample(string time, long exp) => new(
        DateTimeOffset.Parse($"2026-11-26T{time}:00+09:00"), exp);
}
