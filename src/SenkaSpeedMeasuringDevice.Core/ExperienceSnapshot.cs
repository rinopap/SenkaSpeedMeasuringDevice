namespace SenkaSpeedMeasuringDevice.Core;

public sealed record ExperienceSnapshot(
    DateTimeOffset Timestamp,
    long? AdmiralExperience,
    long? ShipTotalExperience,
    string EventId);

public sealed record ExperienceSample(DateTimeOffset Timestamp, long Experience);
