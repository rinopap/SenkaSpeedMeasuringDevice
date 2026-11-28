using System.Globalization;

namespace SenkaSpeedMeasuringDevice.Core;

public sealed record ShipExpRateBucket(
    DateTimeOffset LocalStart,
    DateTimeOffset LocalEnd,
    long GainedExp,
    double? ExpPerHour,
    bool FullyCovered,
    double CoveredHours,
    bool HasUncertainIssue = false);

/// <summary>Calculates API-reported fleet EXP over only known-readable capture intervals.</summary>
public static class ShipExpGainRateCalculator
{
    private static readonly TimeSpan AdjacentTolerance = TimeSpan.FromMilliseconds(2);

    public static IReadOnlyList<ShipExpRateBucket> CalculateThreeHours(
        IEnumerable<ShipExpGainEvent> events,
        IEnumerable<CaptureCoverageSegment> coverage,
        DateTimeOffset now,
        IEnumerable<DateTimeOffset>? issueTimestamps = null,
        IEnumerable<ShipExpIssueTimeWindow>? uncertainIssueWindows = null)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local);
        var localHour = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, 0, 0, DateTimeKind.Unspecified);
        var coverageRanges = MergeCoverage(coverage);
        var orderedEvents = events.OrderBy(x => x.ObservedAt).ThenBy(x => x.SourceRecordId).ToArray();
        var issues = issueTimestamps?.Select(x => x.ToUniversalTime()).ToArray() ?? [];
        var uncertain = uncertainIssueWindows?.ToArray() ?? [];
        var buckets = new List<ShipExpRateBucket>(3);
        for (var back = 2; back >= 0; back--)
        {
            var fromLocal = localHour.AddHours(-back);
            var toLocal = fromLocal.AddHours(1);
            var from = LocalBoundary(fromLocal);
            var fullEnd = LocalBoundary(toLocal);
            var evaluationEnd = back == 0 && now.ToUniversalTime() < fullEnd.ToUniversalTime() ? now : fullEnd;
            buckets.Add(CalculateBucket(orderedEvents, coverageRanges, issues, uncertain,
                from, evaluationEnd, fullEnd));
        }
        return buckets;
    }

    public static string FormatThreeHours(IEnumerable<ShipExpGainEvent> events,
        IEnumerable<CaptureCoverageSegment> coverage, DateTimeOffset now,
        IEnumerable<DateTimeOffset>? issueTimestamps = null,
        IEnumerable<ShipExpIssueTimeWindow>? uncertainIssueWindows = null)
    {
        return FormatBuckets(CalculateThreeHours(events, coverage, now, issueTimestamps, uncertainIssueWindows));
    }

    public static string FormatBuckets(IEnumerable<ShipExpRateBucket> buckets)
    {
        return string.Join(Environment.NewLine, buckets.Select(bucket =>
        {
            var label = $"・{bucket.LocalStart:HH\\:mm}~{bucket.LocalEnd:HH\\:mm} Exp時速 ";
            if (bucket.HasUncertainIssue) return label + "未確定";
            if (bucket.CoveredHours <= 0 || bucket.ExpPerHour is null) return label + "未計測";
            var value = Math.Round(bucket.ExpPerHour.Value, MidpointRounding.AwayFromZero)
                .ToString("N0", CultureInfo.GetCultureInfo("en-US"));
            return label + value;
        }));
    }

    /// <summary>Provides diagnostics for the tooltip while keeping the compact rate display uncluttered.</summary>
    public static string? FormatCoverageDiagnostics(IEnumerable<ShipExpRateBucket> buckets)
    {
        var details = buckets.Select(bucket =>
        {
            var period = $"{bucket.LocalStart:HH\\:mm}~{bucket.LocalEnd:HH\\:mm}";
            if (bucket.HasUncertainIssue) return $"{period}: 記録時刻不明issueの影響範囲を確定できません";
            if (bucket.ExpPerHour is null && bucket.CoveredHours <= 0)
                return $"{period}: 計算に必要な計測範囲がありません";
            if (!bucket.FullyCovered)
                return $"{period}: 部分計測（取得済み {bucket.CoveredHours:0.##} 時間）";
            return null;
        }).Where(value => value is not null).ToArray();
        return details.Length == 0 ? null : string.Join(Environment.NewLine, details!);
    }

    private static ShipExpRateBucket CalculateBucket(ShipExpGainEvent[] events,
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> coverage,
        DateTimeOffset[] issueTimestamps,
        ShipExpIssueTimeWindow[] uncertainIssueWindows,
        DateTimeOffset start, DateTimeOffset end, DateTimeOffset fullHourEnd)
    {
        if (end <= start) return new ShipExpRateBucket(start, end, 0, null, false, 0);
        if (uncertainIssueWindows.Any(window => window.Overlaps(start, end)))
            return new ShipExpRateBucket(start, end, 0, null, false, 0, HasUncertainIssue: true);
        var covered = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var range in coverage)
        {
            var pieceStart = range.Start > start ? range.Start : start;
            var pieceEnd = range.End < end ? range.End : end;
            if (pieceEnd > pieceStart) covered.Add((pieceStart, pieceEnd));
        }
        covered = Merge(covered);
        var coveredTicks = covered.Aggregate(0L, (sum, range) => checked(sum + (range.End - range.Start).Ticks));
        if (coveredTicks <= 0) return new ShipExpRateBucket(start, end, 0, null, false, 0);

        var hasKnownIssue = issueTimestamps.Any(issue => issue >= start && issue < end);
        var fullyCovered = !hasKnownIssue && end >= fullHourEnd && covered.Count == 1 &&
                           covered[0].Start <= start && covered[0].End >= fullHourEnd;
        long gained = 0;
        try
        {
            foreach (var item in events)
                if (covered.Any(range => item.ObservedAt >= range.Start && item.ObservedAt < range.End))
                    gained = checked(gained + item.GainedExp);
        }
        catch (OverflowException)
        {
            return new ShipExpRateBucket(start, end, gained, null, fullyCovered,
                TimeSpan.FromTicks(coveredTicks).TotalHours);
        }

        var durationHours = fullyCovered
            ? (fullHourEnd.ToUniversalTime() - start.ToUniversalTime()).TotalHours
            : TimeSpan.FromTicks(coveredTicks).TotalHours;
        double? rate = durationHours > 0 ? gained / durationHours : null;
        return new ShipExpRateBucket(start, end, gained, rate, fullyCovered, durationHours);
    }

    private static DateTimeOffset LocalBoundary(DateTime local)
    {
        var zone = TimeZoneInfo.Local;
        var offset = zone.GetUtcOffset(local);
        if (zone.IsInvalidTime(local))
        {
            // Local clock jumps over this boundary. Keep it explicitly unavailable rather than inventing a zero hour.
            return new DateTimeOffset(local, offset);
        }
        if (zone.IsAmbiguousTime(local)) offset = zone.GetAmbiguousTimeOffsets(local).Max();
        return new DateTimeOffset(local, offset);
    }

    private static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> MergeCoverage(
        IEnumerable<CaptureCoverageSegment> segments) => Merge(segments
            .Where(x => x.EndedAtUtc > x.StartedAtUtc)
            .Select(x => (x.StartedAtUtc.ToUniversalTime(), x.EndedAtUtc.ToUniversalTime()))
            .OrderBy(x => x.Item1)
            .ToList());

    private static List<(DateTimeOffset Start, DateTimeOffset End)> Merge(
        IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> ranges)
    {
        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var range in ranges.OrderBy(x => x.Start))
        {
            if (merged.Count == 0 || range.Start - merged[^1].End > AdjacentTolerance)
            {
                merged.Add(range);
                continue;
            }
            if (range.End > merged[^1].End) merged[^1] = (merged[^1].Start, range.End);
        }
        return merged;
    }
}
