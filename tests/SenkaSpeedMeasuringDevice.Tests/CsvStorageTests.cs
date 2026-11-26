using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class CsvStorageTests
{
    [Theory]
    [InlineData("exp_log.csv", "admiral_exp")]
    [InlineData("ship_exp_log.csv", "ship_total_exp")]
    public void AppendsExistingCompatibleCsvAndLoadsInTimestampOrder(string fileName, string column)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SenkaCsvTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, fileName);
            File.WriteAllText(path, $"timestamp,{column}\n2026-11-26T08:30:00,2000000000000\n2026-11-26T08:00:00,1000000000000\n");
            CsvStorage.Append(path, column, new ExperienceSample(DateTimeOffset.Parse("2026-11-26T09:00:00+09:00"), 3_000_000_000_000));

            var records = CsvStorage.Load(path, column);
            Assert.Equal(3, records.Count);
            Assert.Equal(1_000_000_000_000, records[0].Experience);
            Assert.Equal(3_000_000_000_000, records[^1].Experience);
            Assert.StartsWith($"timestamp,{column}\n2026-11-26T08:30:00,2000000000000", File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void IgnoresUnknownHeaderAndMalformedRows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"senka-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(path, "wrong,header\n2026-11-26T08:00:00,100\n");
            Assert.Empty(CsvStorage.Load(path, "admiral_exp"));
            File.WriteAllText(path, "timestamp,admiral_exp\nbad,100\n2026-11-26T08:00:00,broken\n2026-11-26T08:30:00,100\n");
            Assert.Single(CsvStorage.Load(path, "admiral_exp"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}

