using System.Text;
using Microsoft.Data.Sqlite;
using SenkaSpeedMeasuringDevice.Capture;
using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class AdmiralObservationStoreTests
{
    [Fact]
    public void MeasurementSessionBoundarySurvivesRestartAndCanBeExplicitlyAdvanced()
    {
        using var fixture = new StoreFixture();
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        var generation = store.GetOrCreateGeneration("db-identity", 100);

        var first = store.GetOrCreateActiveMeasurementSession(generation, 100);
        var afterRestart = store.GetOrCreateActiveMeasurementSession(generation, 150);
        var second = store.StartNewMeasurementSession(generation, 160,
            DateTimeOffset.Parse("2026-11-28T12:00:00Z"));
        var afterSecondRestart = store.GetOrCreateActiveMeasurementSession(generation, 200);

        Assert.Equal(first, afterRestart);
        Assert.Equal(100, first.StartAfterId);
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(160, second.StartAfterId);
        Assert.Equal(second, afterSecondRestart);
    }

    [Fact]
    public void DeferredCombinedBattleIsMarkedOutOfScopeAndScopedAsAnIssue()
    {
        using var fixture = new StoreFixture();
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        var generation = store.GetOrCreateGeneration("db-identity", 42);
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-5);
        var issue = new SqliteApiFileIssue(42, "out-of-scope-api", timestamp, "run:0");

        store.CommitShipGainBatch(generation, "run", "api_req_combined_battle/battleresult",
            new SqliteApiFileBatch([], [issue], 42, 0, 0));

        Assert.Equal(1, store.GetOutOfScopeEventCount(generation, "run"));
        Assert.Equal(1, store.GetIssueCount(generation, "run"));
        Assert.Equal(timestamp, Assert.Single(store.LoadShipExpGainIssueTimes("run")));
        Assert.Empty(store.LoadShipExpGainEvents("run"));
    }

    [Fact]
    public void UnknownIssueIsBoundedBySurroundingKnownResultRows()
    {
        using var fixture = new StoreFixture();
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        var generation = store.GetOrCreateGeneration("db-identity", 43);
        var before = DateTimeOffset.Parse("2026-11-28T12:15:00Z");
        var after = DateTimeOffset.Parse("2026-11-28T12:45:00Z");
        var body = "{\"api_result\":1,\"api_data\":{\"api_get_ship_exp\":[0,100,0,0,0,0,0]}}";
        var first = new ApiResponse("/kcsapi/api_req_sortie/battleresult", before,
            Encoding.UTF8.GetBytes(body), "first", 42, generation, "run:0");
        var last = new ApiResponse("/kcsapi/api_req_sortie/battleresult", after,
            Encoding.UTF8.GetBytes(body), "last", 44, generation, "run:0");
        store.CommitShipGainBatch(generation, "run", "api_req_sortie/battleresult",
            new SqliteApiFileBatch([first, last], [new SqliteApiFileIssue(43, "unknown-time-issue", null, "run:0")],
                44, 0, 0));

        var window = Assert.Single(store.LoadUnknownShipExpGainIssueWindows("run:0"));
        Assert.Equal(before, window.StartUtcInclusive);
        Assert.Equal(after, window.EndUtcExclusive);
    }

    [Fact]
    public void UnknownIssueWithoutSurroundingRecordsRemainsUnbounded()
    {
        using var fixture = new StoreFixture();
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        var generation = store.GetOrCreateGeneration("db-identity", 43);
        store.CommitShipGainBatch(generation, "run", "api_req_sortie/battleresult",
            new SqliteApiFileBatch([], [new SqliteApiFileIssue(43, "unknown-time-issue", null, "run:0")],
                43, 0, 0));

        var window = Assert.Single(store.LoadUnknownShipExpGainIssueWindows("run:0"));
        Assert.Null(window.StartUtcInclusive);
        Assert.Null(window.EndUtcExclusive);
    }

    [Fact]
    public void ResultRecordIsAssignedToTheLatestPortObservedAccountSeries()
    {
        using var fixture = new StoreFixture();
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        var generation = store.GetOrCreateGeneration("db-identity", 250);
        var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
        store.CommitPortBatch(generation, "run", Batch(100, PortResponse(100, 1000, cutoff.AddMinutes(1))), cutoff);
        store.CommitPortBatch(generation, "run", Batch(200, PortResponse(200, 500, cutoff.AddMinutes(2))), cutoff);

        Assert.Equal("run:0", store.GetMeasurementSeriesForApiRecord(generation, "run", 150));
        Assert.Equal("run:1", store.GetMeasurementSeriesForApiRecord(generation, "run", 250));
        Assert.Equal("run:0", store.GetMeasurementSeriesForApiRecord(generation, "run", 99));
    }

    [Fact]
    public void ParseIssuesFromAnotherMeasurementSeriesDoNotPolluteCurrentSeries()
    {
        using var fixture = new StoreFixture();
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        var generation = store.GetOrCreateGeneration("db-identity", 10);
        var issueTime = DateTimeOffset.UtcNow.AddMinutes(-4);
        store.CommitShipGainBatch(generation, "run", "api_req_sortie/battleresult",
            new SqliteApiFileBatch([], [new SqliteApiFileIssue(10, "invalid-payload", issueTime, "run:1")],
                10, 0, 0));

        Assert.Equal(0, store.GetIssueCount(generation, "run:0"));
        Assert.Equal(1, store.GetIssueCount(generation, "run:1"));
        Assert.Empty(store.LoadShipExpGainIssueTimes("run:0"));
        Assert.Equal(issueTime, Assert.Single(store.LoadShipExpGainIssueTimes("run:1")));
    }

    [Fact]
    public void PortObservationAndCursorAreDurableAndReplayIsIdempotent()
    {
        using var fixture = new StoreFixture();
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        store.ImportLegacyCsvOnce([]);
        var generation = store.GetOrCreateGeneration("db-identity-a", highWaterId: 42);
        const string series = "launch-a";
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-10);
        var batch = Batch(42, PortResponse(42, 8_765_432, timestamp));

        store.CommitPortBatch(generation, series, batch, DateTimeOffset.UtcNow.AddHours(-3));
        store.CommitPortBatch(generation, series, batch, DateTimeOffset.UtcNow.AddHours(-3));

        Assert.Equal(42, store.GetCursor(generation, series));
        var sample = Assert.Single(store.LoadAdmiralSamples(series + ":"));
        Assert.Equal(8_765_432, sample.Experience);
        Assert.Equal(series + ":0", sample.SeriesId);
    }

    [Fact]
    public void JsonObservationsCanContinueAnExplicitlySelectedSqliteAdmiralSeries()
    {
        using var fixture = new StoreFixture();
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        store.ImportLegacyCsvOnce([]);
        var generation = store.GetOrCreateGeneration("db-identity", 20);
        var start = DateTimeOffset.UtcNow.AddMinutes(-20);
        const string sqliteSeries = "active-session:0";
        store.CommitPortBatch(generation, "active-session",
            Batch(10, PortResponse(10, 100_000, start)) with { ScannedThroughId = 10 },
            DateTimeOffset.UtcNow.AddHours(-3));
        Assert.True(store.RecordJsonObservation("json-one", new ExperienceSample(start.AddMinutes(10), 100_700), sqliteSeries));
        Assert.True(store.RecordJsonObservation("json-two", new ExperienceSample(start.AddMinutes(11), 100_700), sqliteSeries));
        Assert.False(store.RecordJsonObservation("json-two", new ExperienceSample(start.AddMinutes(11), 100_700), sqliteSeries));

        var sameSeries = store.LoadAdmiralSamples(sqliteSeries);
        Assert.Equal(3, sameSeries.Count);
        Assert.All(sameSeries, sample => Assert.Equal(sqliteSeries, sample.SeriesId));
        Assert.Equal(2, sameSeries.Count(sample => sample.Experience == 100_700));
        Assert.Equal(2.94, SenkaCalculator.Rate(sameSeries[0], sameSeries[1], senka: true)!.Value, 2);
    }

    [Fact]
    public void JsonSeriesMigrationMovesOnlyJsonOriginObservationsAndPreservesDistinctSamples()
    {
        using var fixture = new StoreFixture();
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        store.ImportLegacyCsvOnce([]);
        var generation = store.GetOrCreateGeneration("db-identity", 3);
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-5);
        const string from = "json-session";
        const string to = "sqlite-session:0";
        store.CommitPortBatch(generation, "sqlite-session",
            Batch(3, PortResponse(3, 20_000, timestamp.AddMinutes(1))), DateTimeOffset.UtcNow.AddHours(-3));
        store.RecordJsonObservation("response-a", new ExperienceSample(timestamp, 19_000), from);
        store.RecordJsonObservation("response-b", new ExperienceSample(timestamp.AddMinutes(1), 20_000), from);

        Assert.Equal(2, store.MoveAdmiralObservationsToSeries(from, to));
        Assert.Empty(store.LoadAdmiralSamples(from));
        var samples = store.LoadAdmiralSamples(to);
        Assert.Equal(3, samples.Count);
        Assert.Equal(2, samples.Count(sample => sample.Experience == 20_000));
        Assert.Equal(0, store.MoveAdmiralObservationsToSeries(from, to));
    }

    [Fact]
    public void JsonShipHistoryIsSeriesScopedAndCsvExportIsReplaySafe()
    {
        using var fixture = new StoreFixture();
        var csv = Path.Combine(fixture.Directory, "ship_exp_log.csv");
        File.WriteAllText(csv, "timestamp,ship_total_exp\r\n2026-11-28T10:00:00,1500\r\n");
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        store.ImportLegacyJsonShipCsvOnce(csv, "json-active");
        store.RecordJsonShipObservation("response-1",
            new ExperienceSample(DateTimeOffset.Parse("2026-11-28T10:10:00+09:00"), 1700), "json-active");
        store.RecordJsonShipObservation("response-2",
            new ExperienceSample(DateTimeOffset.Parse("2026-11-28T10:11:00+09:00"), 1700), "json-other");

        Assert.Equal(2, store.LoadJsonShipSamples("json-active").Count);
        Assert.Single(store.LoadJsonShipSamples("json-other"));
        store.ExportJsonShipCsv(csv);
        store.ExportJsonShipCsv(csv);
        Assert.Equal(3, CsvStorage.Load(csv, "ship_total_exp").Count);
        Assert.Contains("2026-11-28T10:00:00,1500", File.ReadAllText(csv));
    }

    [Fact]
    public void JsonPortObservationStoresBothMetricsAndDeduplicatesTheSameResponse()
    {
        using var fixture = new StoreFixture();
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        store.ImportLegacyCsvOnce([]);
        store.ImportLegacyJsonShipCsvOnce(Path.Combine(fixture.Directory, "missing.csv"), "ship-series");
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-1);
        var admiral = new ExperienceSample(timestamp, 12_345);
        var ships = new ExperienceSample(timestamp, 98_765);

        Assert.Equal((true, true), store.RecordJsonPortObservation("response-one", admiral,
            "admiral-series", ships, "ship-series"));
        Assert.Equal((false, false), store.RecordJsonPortObservation("response-one", admiral,
            "admiral-series", ships, "ship-series"));
        Assert.Equal(12_345, Assert.Single(store.LoadAdmiralSamples("admiral-series")).Experience);
        Assert.Equal(98_765, Assert.Single(store.LoadJsonShipSamples("ship-series")).Experience);
    }

    [Fact]
    public void SkippingPortRowsAtConfirmedSourceHandoffKeepsTheExistingAccountSeries()
    {
        using var fixture = new StoreFixture();
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        var generation = store.GetOrCreateGeneration("db-identity", 42);
        var now = DateTimeOffset.UtcNow;
        store.CommitPortBatch(generation, "run", Batch(42, PortResponse(42, 10_000, now)),
            now.AddHours(-3));

        store.SkipPortBacklog(generation, "run", 50, "handoff-1");
        store.SkipPortBacklog(generation, "run", 60, "handoff-1");
        Assert.Equal(50, store.GetCursor(generation, "run"));
        Assert.Equal("run:0", store.GetCurrentPortMeasurementSeriesId(generation, "run"));
        store.CommitPortBatch(generation, "run", Batch(51, PortResponse(51, 10_100, now.AddMinutes(1))),
            now.AddHours(-3));
        Assert.Equal("run:0", store.GetCurrentPortMeasurementSeriesId(generation, "run"));
        Assert.Equal(2, store.LoadAdmiralSamples("run:0").Count);
    }

    [Fact]
    public void DifferentDatabaseGenerationsDoNotDeduplicateMatchingRowIds()
    {
        using var fixture = new StoreFixture();
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        store.ImportLegacyCsvOnce([]);
        var firstGeneration = store.GetOrCreateGeneration("db-identity-a", 1);
        var secondGeneration = store.GetOrCreateGeneration("db-identity-b", 1);
        Assert.NotEqual(firstGeneration, secondGeneration);

        store.CommitPortBatch(firstGeneration, "session", Batch(1, PortResponse(1, 100,
            DateTimeOffset.UtcNow.AddMinutes(-2))), DateTimeOffset.UtcNow.AddHours(-3));
        store.CommitPortBatch(secondGeneration, "session", Batch(1, PortResponse(1, 100,
            DateTimeOffset.UtcNow.AddMinutes(-1))), DateTimeOffset.UtcNow.AddHours(-3));

        Assert.Equal(2, store.LoadAdmiralSamples().Count);
    }

    [Fact]
    public void SameDatabaseEventCanBeReplayedInANewSessionWithoutDuplicatingCsvRows()
    {
        using var fixture = new StoreFixture();
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        store.ImportLegacyCsvOnce([]);
        var generation = store.GetOrCreateGeneration("db-identity", 12);
        var time = DateTimeOffset.UtcNow.AddMinutes(-1);
        var batch = Batch(12, PortResponse(12, 500, time));

        store.CommitPortBatch(generation, "run-one", batch, DateTimeOffset.UtcNow.AddHours(-3));
        store.CommitPortBatch(generation, "run-two", batch, DateTimeOffset.UtcNow.AddHours(-3));
        var output = Path.Combine(fixture.Directory, "exp_log_sqlite.csv");
        store.ExportSqliteAdmiralCsv(output);

        Assert.Single(store.LoadAdmiralSamples("run-one:"));
        Assert.Single(store.LoadAdmiralSamples("run-two:"));
        Assert.Single(CsvStorage.Load(output, "admiral_exp"));
    }

    [Fact]
    public void SQLiteCsvIsSeparatedFromUnverifiedLegacyHistory()
    {
        using var fixture = new StoreFixture();
        var legacyCsv = Path.Combine(fixture.Directory, "exp_log.csv");
        File.WriteAllText(legacyCsv, "timestamp,admiral_exp\r\n2026-11-28T10:00:00,1000\r\n");
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        store.ImportLegacyCsvOnce(CsvStorage.Load(legacyCsv, "admiral_exp"), legacyCsv);
        var generation = store.GetOrCreateGeneration("db-identity", 1);
        store.CommitPortBatch(generation, "sqlite-run", Batch(1, PortResponse(1, 1200,
            DateTimeOffset.UtcNow.AddMinutes(-1))), DateTimeOffset.UtcNow.AddHours(-3));
        var sqliteCsv = Path.Combine(fixture.Directory, "exp_log_sqlite.csv");

        store.ExportAdmiralCsv(legacyCsv);
        store.ExportSqliteAdmiralCsv(sqliteCsv);

        Assert.Single(CsvStorage.Load(legacyCsv, "admiral_exp"));
        Assert.Single(CsvStorage.Load(sqliteCsv, "admiral_exp"));
        Assert.Equal(1000, CsvStorage.Load(legacyCsv, "admiral_exp")[0].Experience);
        Assert.Equal(1200, CsvStorage.Load(sqliteCsv, "admiral_exp")[0].Experience);
    }

    [Fact]
    public void DatabaseIdRollbackCreatesNewGeneration()
    {
        using var fixture = new StoreFixture();
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        store.ImportLegacyCsvOnce([]);
        var first = store.GetOrCreateGeneration("db-identity", 100);
        store.CommitPortBatch(first, "session", Batch(100, PortResponse(100, 1000,
            DateTimeOffset.UtcNow.AddMinutes(-2))), DateTimeOffset.UtcNow.AddHours(-3));
        var afterRestore = store.GetOrCreateGeneration("db-identity", 10);
        Assert.NotEqual(first, afterRestore);
        Assert.Null(store.GetCursor(afterRestore, "session"));
    }

    [Fact]
    public void ShipGainEventsAreDurableAndDeduplicatedByStableEventIdAcrossReplays()
    {
        using var fixture = new StoreFixture();
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        store.ImportLegacyCsvOnce([]);
        var generation = store.GetOrCreateGeneration("db-identity", 100);
        var first = ShipBatch(100, ShipResponse("/kcsapi/api_req_sortie/battleresult", 100,
            "{\"api_result\":1,\"api_data\":{\"api_get_ship_exp\":[0,100,200,0,0,0,0]}}"));

        store.CommitShipGainBatch(generation, "run-a", "api_req_sortie/battleresult", first);
        store.CommitShipGainBatch(generation, "run-b", "api_req_sortie/battleresult", first);

        Assert.Equal(100, store.GetCursor(generation, "run-a", "api_req_sortie/battleresult"));
        Assert.Equal(100, store.GetCursor(generation, "run-b", "api_req_sortie/battleresult"));
        var saved = Assert.Single(store.LoadShipExpGainEvents("run-a"));
        Assert.Equal(300, saved.GainedExp);
        Assert.Empty(store.LoadShipExpGainEvents("run-b"));
    }

    [Fact]
    public void BaseMeasurementSessionLoadsItsCurrentAccountSubseries()
    {
        using var fixture = new StoreFixture();
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        store.ImportLegacyCsvOnce([]);
        var generation = store.GetOrCreateGeneration("db-identity", 100);
        var response = ShipResponse("/kcsapi/api_req_sortie/battleresult", 100,
            "{\"api_result\":1,\"api_data\":{\"api_get_ship_exp\":[0,10,0,0,0,0,0]}}") with
        {
            MeasurementSeriesId = "run:1"
        };
        store.CommitShipGainBatch(generation, "run", "api_req_sortie/battleresult",
            ShipBatch(100, response));
        store.RecordCoverageSuccess(generation, "run:1", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(8));

        Assert.Equal("run:1", Assert.Single(store.LoadShipExpGainEvents("run")).MeasurementSeriesId);
        Assert.Single(store.LoadCoverageSegments("run"));
    }

    [Fact]
    public void ShipGainApiCursorsAreIndependentFromPortAndEachOther()
    {
        using var fixture = new StoreFixture();
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        store.ImportLegacyCsvOnce([]);
        var generation = store.GetOrCreateGeneration("db-identity", 100);
        store.CommitPortBatch(generation, "run", Batch(10, PortResponse(10, 500,
            DateTimeOffset.UtcNow)), DateTimeOffset.UtcNow.AddHours(-3));
        store.CommitShipGainBatch(generation, "run", "api_req_sortie/battleresult",
            ShipBatch(90, ShipResponse("/kcsapi/api_req_sortie/battleresult", 90,
                "{\"api_result\":1,\"api_data\":{\"api_get_ship_exp\":[0,5,0,0,0,0,0]}}")));

        Assert.Equal(10, store.GetCursor(generation, "run", "api_port/port"));
        Assert.Equal(90, store.GetCursor(generation, "run", "api_req_sortie/battleresult"));
        Assert.Null(store.GetCursor(generation, "run", "api_req_mission/result"));
    }

    [Fact]
    public void ShipGainCsvIsRegeneratedWithoutChangingLegacyCsvFiles()
    {
        using var fixture = new StoreFixture();
        var admiralCsv = Path.Combine(fixture.Directory, "exp_log.csv");
        var shipTotalCsv = Path.Combine(fixture.Directory, "ship_exp_log.csv");
        File.WriteAllText(admiralCsv, "timestamp,admiral_exp\r\n2026-11-28T10:00:00,10\r\n");
        File.WriteAllText(shipTotalCsv, "timestamp,ship_total_exp\r\n2026-11-28T10:00:00,20\r\n");
        var admiralBefore = File.ReadAllBytes(admiralCsv);
        var shipTotalBefore = File.ReadAllBytes(shipTotalCsv);
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        store.ImportLegacyCsvOnce(CsvStorage.Load(admiralCsv, "admiral_exp"), admiralCsv);
        var generation = store.GetOrCreateGeneration("db-identity", 20);
        store.CommitShipGainBatch(generation, "run", "api_req_mission/result",
            ShipBatch(20, ShipResponse("/kcsapi/api_req_mission/result", 20,
                "{\"api_result\":1,\"api_data\":{\"api_get_ship_exp\":[8,9]}}")));
        var output = Path.Combine(fixture.Directory, "ship_exp_gain_log.csv");

        store.ExportShipExpGainCsv(output);

        Assert.Equal(admiralBefore, File.ReadAllBytes(admiralCsv));
        Assert.Equal(shipTotalBefore, File.ReadAllBytes(shipTotalCsv));
        var lines = File.ReadAllLines(output);
        Assert.Equal("timestamp,source_id,event_id,kind,gained_exp,meaning", lines[0]);
        Assert.Contains(",Expedition,17,ApiReportedShipExp", lines[1]);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void InvalidShipEventIsRecordedAndCursorCommitsAtomicallyWithoutZeroGain()
    {
        using var fixture = new StoreFixture();
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        store.ImportLegacyCsvOnce([]);
        var generation = store.GetOrCreateGeneration("db-identity", 7);
        var response = ShipResponse("/kcsapi/api_req_sortie/battleresult", 7,
            "{\"api_result\":1,\"api_data\":{\"api_get_ship_exp\":[0,1,-2,3,4,5,6]}}");

        store.CommitShipGainBatch(generation, "run", "api_req_sortie/battleresult", ShipBatch(7, response));

        Assert.Equal(7, store.GetCursor(generation, "run", "api_req_sortie/battleresult"));
        Assert.Empty(store.LoadShipExpGainEvents("run"));
        Assert.Equal(1, store.GetIssueCount(generation));
    }

    [Fact]
    public void InvalidPortExperienceIsTrackedAndDoesNotBecomeAZeroObservation()
    {
        using var fixture = new StoreFixture();
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        store.ImportLegacyCsvOnce([]);
        var generation = store.GetOrCreateGeneration("db-identity", 7);
        var invalid = new ApiResponse("/kcsapi/api_port/port", DateTimeOffset.UtcNow,
            Encoding.UTF8.GetBytes("{\"api_result\":1,\"api_data\":{\"api_basic\":{\"api_experience\":-1}}}"),
            "unstable", 7);

        store.CommitPortBatch(generation, "session", Batch(7, invalid), DateTimeOffset.UtcNow.AddHours(-3));

        Assert.Equal(7, store.GetCursor(generation, "session"));
        Assert.Empty(store.LoadAdmiralSamples());
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DatabasePath, Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT State,ErrorCode FROM Events WHERE GenerationId=$generation;";
        command.Parameters.AddWithValue("$generation", generation);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("invalid", reader.GetString(0));
        Assert.Equal("invalid-port-experience", reader.GetString(1));
    }

    [Fact]
    public void LegacyCsvIsImportedOnceAndKeepsTheOriginalTwoColumnSchema()
    {
        using var fixture = new StoreFixture();
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        var first = new ExperienceSample(DateTimeOffset.Parse("2026-11-28T10:00:00+09:00"), 1234);
        store.ImportLegacyCsvOnce([first]);
        store.ImportLegacyCsvOnce([]);
        var output = Path.Combine(fixture.Directory, "exp_log.csv");

        store.ExportAdmiralCsv(output);

        Assert.Equal("timestamp,admiral_exp", File.ReadLines(output).First());
        Assert.Single(CsvStorage.Load(output, "admiral_exp"));
        Assert.DoesNotContain("EventId", File.ReadAllText(output));
    }

    [Fact]
    public void LegacyCsvRowsIncludingMalformedRowsArePreservedVerbatim()
    {
        using var fixture = new StoreFixture();
        var csv = Path.Combine(fixture.Directory, "exp_log.csv");
        var valid = "2026-11-28T10:00:00,1234";
        File.WriteAllText(csv, $"timestamp,admiral_exp\r\n{valid}\r\nnot-a-valid-row\r\n\r\n");
        using var store = new AdmiralObservationStore(fixture.DatabasePath);
        store.ImportLegacyCsvOnce(CsvStorage.Load(csv, "admiral_exp"), csv);

        store.ExportAdmiralCsv(csv);

        var lines = File.ReadAllLines(csv);
        Assert.Equal("timestamp,admiral_exp", lines[0]);
        Assert.Contains(valid, lines);
        Assert.Contains("not-a-valid-row", lines);
        Assert.Contains(string.Empty, lines);
    }

    [Fact]
    public void IncompatibleLegacyHeaderIsRejectedWithoutChangingTheFile()
    {
        using var fixture = new StoreFixture();
        var csv = Path.Combine(fixture.Directory, "exp_log.csv");
        var original = "timestamp,unexpected_column\r\nkeep-this-data\r\n";
        File.WriteAllText(csv, original);
        var before = File.ReadAllBytes(csv);
        using var store = new AdmiralObservationStore(fixture.DatabasePath);

        Assert.Throws<InvalidDataException>(() => store.ImportLegacyCsvOnce([], csv));

        Assert.Equal(before, File.ReadAllBytes(csv));
    }

    private static SqliteApiFileBatch Batch(long through, params ApiResponse[] responses) =>
        new(responses, [], through, 0, 0);

    private static SqliteApiFileBatch ShipBatch(long through, params ApiResponse[] responses) =>
        new(responses, [], through, 0, 0);

    private static ApiResponse ShipResponse(string path, long id, string body) => new(path,
        DateTimeOffset.UtcNow, Encoding.UTF8.GetBytes(body), $"generation-a:{path}:{id}", id,
        "generation-a", "run-a");

    private static ApiResponse PortResponse(long id, long experience, DateTimeOffset timestamp) => new(
        "/kcsapi/api_port/port", timestamp,
        Encoding.UTF8.GetBytes($"{{\"api_result\":1,\"api_data\":{{\"api_basic\":{{\"api_experience\":{experience}}},\"api_ship\":[]}}}}"),
        "temporary", id);

    private sealed class StoreFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"senka-store-{Guid.NewGuid():N}");
        public string DatabasePath => Path.Combine(Directory, "state.sqlite");
        public StoreFixture() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose()
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory))
                try { File.Delete(file); } catch (IOException) { }
            try { System.IO.Directory.Delete(Directory); } catch (IOException) { }
        }
    }
}
