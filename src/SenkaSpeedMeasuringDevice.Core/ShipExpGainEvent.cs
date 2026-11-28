namespace SenkaSpeedMeasuringDevice.Core;

/// <summary>Kind of allowlisted 74EN result API that reported fleet ship experience.</summary>
public enum ShipExpGainKind
{
    Sortie,
    CombinedBattle,
    Practice,
    Expedition
}

/// <summary>
/// The value is the sum reported in the API's ship-exp arrays. It is not asserted
/// to equal each ship's actual cumulative experience delta at the level cap.
/// </summary>
public sealed record ShipExpGainEvent(
    string EventId,
    string SourceGenerationId,
    string MeasurementSeriesId,
    long SourceRecordId,
    DateTimeOffset ObservedAt,
    ShipExpGainKind Kind,
    long GainedExp,
    string Meaning = "ApiReportedShipExp");

public sealed record ShipExpParseResult(ShipExpGainEvent? Event, string? IssueCode)
{
    public bool IsValid => Event is not null;
    public static ShipExpParseResult Invalid(string code) => new(null, code);
}

/// <summary>Time range during which every supported SQLite response stream was observable.</summary>
public sealed record CaptureCoverageSegment(
    string SourceGenerationId,
    string MeasurementSeriesId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc);

/// <summary>Possible time interval for a result record whose source timestamp is unavailable.</summary>
public sealed record ShipExpIssueTimeWindow(DateTimeOffset? StartUtcInclusive, DateTimeOffset? EndUtcExclusive)
{
    public bool Overlaps(DateTimeOffset startUtc, DateTimeOffset endUtc) =>
        (StartUtcInclusive is null || StartUtcInclusive.Value < endUtc) &&
        (EndUtcExclusive is null || EndUtcExclusive.Value > startUtc);
}
