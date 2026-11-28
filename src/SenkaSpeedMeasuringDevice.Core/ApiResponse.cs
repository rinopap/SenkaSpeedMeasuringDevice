namespace SenkaSpeedMeasuringDevice.Core;

/// <summary>A single response already received by a supported capture source.</summary>
public sealed record ApiResponse(
    string ApiPath,
    DateTimeOffset Timestamp,
    ReadOnlyMemory<byte> ResponseBody,
    string EventId,
    long? SourceRecordId = null,
    string? SourceGenerationId = null,
    string? MeasurementSeriesId = null);
