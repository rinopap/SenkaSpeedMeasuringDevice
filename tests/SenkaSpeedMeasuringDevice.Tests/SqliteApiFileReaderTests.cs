using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SenkaSpeedMeasuringDevice.Capture;
using SenkaSpeedMeasuringDevice.Core;
using Xunit.Abstractions;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class SqliteApiFileReaderTests
{
    private readonly ITestOutputHelper _output;

    public SqliteApiFileReaderTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ReadsOnlyAllowlistedResponsesAndLeavesStaticDatabaseUnchanged()
    {
        using var fixture = new DatabaseFixture();
        fixture.Insert("api_port/port", "{\"api_data\":{\"api_basic\":{\"api_experience\":123}}}", fileType: 1);
        fixture.Insert("api_port/port", "{\"request\":true}", fileType: 0);
        fixture.Insert("api_login/start", "{\"private\":true}", fileType: 1);
        fixture.Insert("api_req_sortie/battleresult", "{\"api_data\":{\"api_get_exp\":22}}", fileType: 1);
        var before = SHA256.HashData(File.ReadAllBytes(fixture.Path));
        var reader = new SqliteApiFileReader(fixture.Path);

        var highWater = await reader.ReadHighWaterIdAsync();
        var batch = await reader.ReadBatchAsync(0, highWater, SqliteApiFileReader.DefaultMaximumRows);

        Assert.Equal(4, highWater);
        Assert.Equal(2, batch.Responses.Count);
        Assert.Equal("/kcsapi/api_port/port", batch.Responses[0].ApiPath);
        Assert.Equal(TimeSpan.Zero, batch.Responses[0].Timestamp.Offset);
        Assert.Equal("/kcsapi/api_req_sortie/battleresult", batch.Responses[1].ApiPath);
        Assert.Equal(4, batch.ScannedThroughId);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(fixture.Path)));
        Assert.False(File.Exists(fixture.Path + "-wal"));
        Assert.False(File.Exists(fixture.Path + "-shm"));

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.Path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ApiFiles SET Name='changed' WHERE Id=1;";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public async Task PagesRowsWithinCapturedHighWaterAndDoesNotReadLaterInsert()
    {
        using var fixture = new DatabaseFixture();
        fixture.Insert("api_port/port", "{}", 1);
        fixture.Insert("api_req_sortie/battleresult", "{}", 1);
        var reader = new SqliteApiFileReader(fixture.Path);
        var highWater = await reader.ReadHighWaterIdAsync();
        fixture.Insert("api_req_mission/result", "{}", 1);

        var first = await reader.ReadBatchAsync(0, highWater, maximumRows: 1);
        var second = await reader.ReadBatchAsync(first.ScannedThroughId, highWater, maximumRows: 1);
        var currentHighWater = await reader.ReadHighWaterIdAsync();
        var third = await reader.ReadBatchAsync(second.ScannedThroughId, currentHighWater, maximumRows: 1);

        Assert.Single(first.Responses);
        Assert.Single(second.Responses);
        Assert.Single(third.Responses);
        Assert.Equal(1, first.ScannedThroughId);
        Assert.Equal(2, second.ScannedThroughId);
        Assert.Equal(3, third.ScannedThroughId);
    }

    [Fact]
    public async Task TimestampFilterStillAdvancesOnlyThePortStreamCursor()
    {
        using var fixture = new DatabaseFixture();
        fixture.Insert("api_port/port", "{}", 1);
        fixture.Insert("api_req_sortie/battleresult", "{}", 1);
        var reader = new SqliteApiFileReader(fixture.Path);
        var highWater = await reader.ReadHighWaterIdAsync();

        var batch = await reader.ReadBatchAsync(0, highWater, SqliteApiFileReader.DefaultMaximumRows,
            CancellationToken.None, "api_port/port", DateTimeOffset.Parse("2026-11-29T00:00:00Z"));

        Assert.Empty(batch.Responses);
        Assert.Equal(highWater, batch.ScannedThroughId);
    }

    [Fact]
    public async Task DeferredCombinedBattleScanUsesOnlyMetadataEvenWhenPayloadIsInvalid()
    {
        using var fixture = new DatabaseFixture();
        fixture.Insert("api_req_combined_battle/battleresult", "{}", 1);
        using (var connection = fixture.OpenReadWrite())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE ApiFiles SET Content=x'00ff' WHERE Id=1;";
            command.ExecuteNonQuery();
        }
        var reader = new SqliteApiFileReader(fixture.Path);

        var batch = await reader.ReadMetadataBatchAsync(0, 1);

        var record = Assert.Single(batch.Records);
        Assert.Equal(1, record.RowId);
        Assert.Equal("api_req_combined_battle/battleresult", record.Name);
        Assert.NotNull(record.TimestampUtc);
        Assert.Equal(1, batch.ScannedThroughId);
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadBatchAsync(0, 1,
            SqliteApiFileReader.DefaultMaximumRows, CancellationToken.None,
            "api_req_combined_battle/battleresult"));
    }

    [Fact]
    public async Task DatabaseIdentityFingerprintStaysStableWhenApiRowsAreAppended()
    {
        using var fixture = new DatabaseFixture();
        fixture.Insert("api_port/port", "{}", 1);
        var reader = new SqliteApiFileReader(fixture.Path);

        var before = await reader.ReadIdentityFingerprintAsync();
        fixture.Insert("api_port/port", "{\"later\":true}", 1);
        var after = await reader.ReadIdentityFingerprintAsync();

        Assert.Equal(before, after);
    }

    [Fact]
    public void IdRangeQueryUsesPrimaryKeyRangeSearch()
    {
        using var fixture = new DatabaseFixture();
        using var connection = fixture.OpenReadWrite();
        using var command = connection.CreateCommand();
        command.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT Id FROM ApiFiles
            WHERE Id > 0 AND Id <= 100 AND ApiFileType = 1
              AND Name = 'api_port/port' AND TimeStamp >= '2026-11-28 09:00:00'
            ORDER BY Id LIMIT 4;
            """;
        using var reader = command.ExecuteReader();
        var details = new List<string>();
        while (reader.Read()) details.Add(reader.GetString(3));

        Assert.Contains(details, detail => detail.Contains("INTEGER PRIMARY KEY", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RejectsUnknownStoredVersionWithoutReturningBody()
    {
        using var fixture = new DatabaseFixture();
        fixture.Insert("api_port/port", "{}", 1, version: 99);
        var reader = new SqliteApiFileReader(fixture.Path);

        var batch = await reader.ReadBatchAsync(0, 1, SqliteApiFileReader.DefaultMaximumRows);

        Assert.Empty(batch.Responses);
        Assert.Equal("unsupported-version", Assert.Single(batch.Issues).Code);
    }

    [Fact]
    public async Task EnforcesExpandedPayloadLimitWhileDecompressing()
    {
        using var fixture = new DatabaseFixture();
        fixture.Insert("api_port/port", "{\"payload\":\"" + new string('x', 512) + "\"}", 1);
        var reader = new SqliteApiFileReader(fixture.Path, maximumCompressedBytes: 2048,
            maximumExpandedBytes: 128, maximumBatchBytes: 2048);

        var batch = await reader.ReadBatchAsync(0, 1, SqliteApiFileReader.DefaultMaximumRows);
        Assert.Empty(batch.Responses);
        Assert.Equal("invalid-payload", Assert.Single(batch.Issues).Code);
    }

    [Fact]
    public async Task ReaderDoesNotPreventSyntheticWalWriterFromSaving()
    {
        using var baseline = new DatabaseFixture();
        baseline.SetWalMode();
        var baselineWatch = System.Diagnostics.Stopwatch.StartNew();
        const int writeCount = 200;
        baseline.InsertMany(writeCount);
        baselineWatch.Stop();

        using var fixture = new DatabaseFixture();
        fixture.SetWalMode();
        var reader = new SqliteApiFileReader(fixture.Path, defaultTimeoutSeconds: 1);
        var writerWatch = System.Diagnostics.Stopwatch.StartNew();
        var writerTask = Task.Run(() => fixture.InsertMany(writeCount));
        var readCalls = 0;
        var conflicts = 0;
        long cursor = 0;
        while (!writerTask.IsCompleted)
        {
            try
            {
                var highWater = await reader.ReadHighWaterIdAsync();
                if (highWater > cursor)
                {
                    var batch = await reader.ReadBatchAsync(cursor, highWater, maximumRows: 4);
                    cursor = batch.ScannedThroughId;
                    readCalls++;
                }
            }
            catch (SqliteException) { conflicts++; }
            await Task.Yield();
        }
        await writerTask;
        writerWatch.Stop();
        using var connection = fixture.OpenReadWrite();
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM ApiFiles;";
        var count = Convert.ToInt32(countCommand.ExecuteScalar());

        Assert.Equal(writeCount, count);
        Assert.True(writerWatch.Elapsed < TimeSpan.FromSeconds(20));
        _output.WriteLine("Synthetic WAL writer-only elapsed: {0:F1} ms for {1} rows", baselineWatch.Elapsed.TotalMilliseconds, writeCount);
        _output.WriteLine("Synthetic WAL reader+writer elapsed: {0:F1} ms for {1} rows; reader polls: {2}; lock errors: {3}; rows present: {4}",
            writerWatch.Elapsed.TotalMilliseconds, writeCount, readCalls, conflicts, count);
    }

    [Fact]
    public async Task Configured74EnDatabaseCanBeReadWithoutChangingTheMainFile()
    {
        var databasePath = Environment.GetEnvironmentVariable("SENKA_EO_SQLITE_TEST_PATH");
        if (string.IsNullOrWhiteSpace(databasePath)) return;
        Assert.True(File.Exists(databasePath), "Configured 74EN database does not exist.");
        var before = new FileInfo(databasePath);
        var beforeLength = before.Length;
        var beforeWrite = before.LastWriteTimeUtc;
        var reader = new SqliteApiFileReader(databasePath);
        var highWater = await reader.ReadHighWaterIdAsync();
        var identity = await reader.ReadIdentityFingerprintAsync();
        var startId = SqliteApiFileReader.GetInitialHistoryStartId(highWater);
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"Senka74EnProbe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var responses = 0;
        var valid = 0;
        var issues = 0;
        DateTimeOffset? earliest = null;
        DateTimeOffset? latest = null;
        try
        {
            using var store = new AdmiralObservationStore(System.IO.Path.Combine(directory, "state.sqlite"));
            store.ImportLegacyCsvOnce([]);
            var generation = store.GetOrCreateGeneration(identity, highWater);
            var series = Guid.NewGuid().ToString("N");
            var cursor = startId;
            store.CommitPortBatch(generation, series, new SqliteApiFileBatch([], [], cursor, 0, 0),
                DateTimeOffset.UtcNow.AddHours(-3));
            while (cursor < highWater)
            {
                var batch = await reader.ReadBatchAsync(cursor, highWater, SqliteApiFileReader.DefaultMaximumRows,
                    CancellationToken.None, "api_port/port", DateTimeOffset.UnixEpoch);
                responses += batch.Responses.Count;
                issues += batch.Issues.Count;
                foreach (var response in batch.Responses)
                {
                    var parsed = ApiResponseParser.ParseAdmiralPort(response);
                    if (parsed?.AdmiralExperience is null) continue;
                    valid++;
                    earliest = earliest is null || parsed.Timestamp < earliest.Value ? parsed.Timestamp : earliest;
                    latest = latest is null || parsed.Timestamp > latest.Value ? parsed.Timestamp : latest;
                }
                store.CommitPortBatch(generation, series, batch with
                {
                    Responses = batch.Responses.Select(response => response with
                    {
                        EventId = $"{generation}:api_port/port:{response.SourceRecordId}",
                        SourceGenerationId = generation
                    }).ToArray()
                }, DateTimeOffset.UnixEpoch);
                Assert.True(batch.ScannedThroughId > cursor, "Reader did not advance within its captured high-water mark.");
                cursor = batch.ScannedThroughId;
            }
            Assert.True(valid > 0, "No valid admiral port records were found in the bounded ID range.");
            Assert.True(store.LoadAdmiralSamples().Count > 0, "No admiral observations were persisted.");
            _output.WriteLine("74EN DB read-only: maxId={0}, idWindow={1}..{0}, portRows={2}, valid={3}, issues={4}, time={5:O}..{6:O}",
                highWater, startId, responses, valid, issues, earliest, latest);
        }
        finally
        {
            var resolved = System.IO.Path.GetFullPath(directory);
            Assert.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), resolved,
                StringComparison.OrdinalIgnoreCase);
            System.IO.Directory.Delete(resolved, recursive: true);
        }

        var after = new FileInfo(databasePath);
        Assert.Equal(beforeLength, after.Length);
        Assert.Equal(beforeWrite, after.LastWriteTimeUtc);
    }

    [Fact]
    public async Task Configured74EnDatabaseShipGainRowsEmitOnlySanitizedShapeSummary()
    {
        var databasePath = Environment.GetEnvironmentVariable("SENKA_EO_SQLITE_TEST_PATH");
        if (string.IsNullOrWhiteSpace(databasePath)) return;
        Assert.True(File.Exists(databasePath));
        var apiNames = new[]
        {
            "api_req_sortie/battleresult",
            "api_req_combined_battle/battleresult",
            "api_req_practice/battle_result",
            "api_req_mission/result"
        };
        var reader = new SqliteApiFileReader(databasePath);
        var highWater = await reader.ReadHighWaterIdAsync();
        var start = SqliteApiFileReader.GetInitialHistoryStartId(highWater);
        var summary = new List<string>();
        foreach (var apiName in apiNames)
        {
            var cursor = start;
            var rows = 0;
            var valid = 0;
            var invalid = 0;
            var shapes = new SortedDictionary<string, int>(StringComparer.Ordinal);
            while (cursor < highWater)
            {
                if (apiName == "api_req_combined_battle/battleresult")
                {
                    var metadata = await reader.ReadMetadataBatchAsync(cursor, highWater,
                        SqliteApiFileReader.DefaultMaximumRows, CancellationToken.None, apiName);
                    rows += metadata.Records.Count;
                    Assert.True(metadata.ScannedThroughId > cursor, "The metadata-only API cursor did not advance.");
                    cursor = metadata.ScannedThroughId;
                    continue;
                }
                var batch = await reader.ReadBatchAsync(cursor, highWater, SqliteApiFileReader.DefaultMaximumRows,
                    CancellationToken.None, apiName, DateTimeOffset.UnixEpoch);
                rows += batch.Responses.Count + batch.Issues.Count;
                invalid += batch.Issues.Count;
                foreach (var response in batch.Responses)
                {
                    var tagged = response with
                    {
                        EventId = $"shape-check:{apiName}:{response.SourceRecordId}",
                        SourceGenerationId = "shape-check",
                        MeasurementSeriesId = "shape-check"
                    };
                    var parsed = ShipExpGainParser.Parse(tagged);
                    if (parsed.Event is null) invalid++;
                    else valid++;
                    foreach (var shape in ReadShipExpArrayShapes(response, apiName))
                        shapes[shape] = shapes.GetValueOrDefault(shape) + 1;
                }
                Assert.True(batch.ScannedThroughId > cursor, "The allowlisted API cursor did not advance.");
                cursor = batch.ScannedThroughId;
            }
            var shapeText = shapes.Count == 0 ? "not-read" : string.Join("/", shapes.Select(kv => $"{kv.Key}:{kv.Value}"));
            var inspection = apiName == "api_req_combined_battle/battleresult" ? "metadata-only" : "payload-parsed";
            summary.Add($"{apiName}: rows={rows}, parsed={valid}, issues={invalid}, array-lengths={shapeText}, inspection={inspection}");
        }

        // The output contains only API names, parser counts, and array lengths. No body, IDs,
        // ship IDs, account fields, or experience values are written to test logs.
        foreach (var line in summary) _output.WriteLine(line);
        Assert.Equal(apiNames.Length, summary.Count);
    }

    private static IReadOnlyList<string> ReadShipExpArrayShapes(ApiResponse response, string apiName)
    {
        var shapes = new List<string>();
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(response.ResponseBody);
            if (!document.RootElement.TryGetProperty("api_data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Object)
                return shapes;
            var fields = apiName == "api_req_combined_battle/battleresult"
                ? new[] { "api_get_ship_exp", "api_get_ship_exp_combined" }
                : ["api_get_ship_exp"];
            foreach (var field in fields)
            {
                if (!data.TryGetProperty(field, out var value)) shapes.Add(field + "=missing");
                else if (value.ValueKind == System.Text.Json.JsonValueKind.Array) shapes.Add(field + "=" + value.GetArrayLength());
                else shapes.Add(field + "=nonarray");
            }
        }
        catch (System.Text.Json.JsonException)
        {
            shapes.Add("invalid-json");
        }
        return shapes;
    }

    [Fact]
    public async Task CaptureSourceSkipsPreSessionHistoryAndResumesFromDurableCursor()
    {
        using var fixture = new DatabaseFixture();
        fixture.Insert("api_port/port", PortBody(1), 1);
        await using var source = new SqliteCaptureSource(fixture.Path, fixture.CursorPath);
        var firstObserved = new TaskCompletionSource<ApiResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ApiResponse> firstHandler = (_, response) =>
        {
            if (Encoding.UTF8.GetString(response.ResponseBody.Span).Contains("\"api_experience\":2", StringComparison.Ordinal))
                firstObserved.TrySetResult(response);
        };
        source.ResponseReceived += firstHandler;

        await source.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => File.Exists(fixture.CursorPath), TimeSpan.FromSeconds(3));
        fixture.Insert("api_port/port", PortBody(2), 1, timestamp: DateTimeOffset.UtcNow);
        var first = await firstObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("\"api_experience\":2", Encoding.UTF8.GetString(first.ResponseBody.Span));
        source.ResponseReceived -= firstHandler;
        await source.StopAsync(CancellationToken.None);

        var secondObserved = new TaskCompletionSource<ApiResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ApiResponse> secondHandler = (_, response) =>
        {
            if (Encoding.UTF8.GetString(response.ResponseBody.Span).Contains("\"api_experience\":3", StringComparison.Ordinal))
                secondObserved.TrySetResult(response);
        };
        source.ResponseReceived += secondHandler;
        await source.StartAsync(CancellationToken.None);
        fixture.Insert("api_port/port", PortBody(3), 1, timestamp: DateTimeOffset.UtcNow);
        var second = await secondObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("\"api_experience\":3", Encoding.UTF8.GetString(second.ResponseBody.Span));
        source.ResponseReceived -= secondHandler;
        await source.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CaptureSourcePersistsBeforeNotificationSoDataSurvivesMissingConsumer()
    {
        using var fixture = new DatabaseFixture();
        fixture.Insert("api_port/port", PortBody(1), 1);
        using var store = new AdmiralObservationStore(System.IO.Path.Combine(fixture.DirectoryPath, "journal.sqlite"));
        store.ImportLegacyCsvOnce([]);
        await using var source = new SqliteCaptureSource(fixture.Path, fixture.CursorPath, store);
        await source.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => GetCursorId(fixture.CursorPath) == 1, TimeSpan.FromSeconds(3));

        fixture.Insert("api_port/port", PortBody(2), 1, timestamp: DateTimeOffset.UtcNow);
        await WaitUntilAsync(() => GetCursorId(fixture.CursorPath) == 2, TimeSpan.FromSeconds(3));
        Assert.Contains(store.LoadAdmiralSamples(), sample => sample.Experience == 2);
        await source.StopAsync(CancellationToken.None);
    }

    private static string PortBody(long experience) =>
        $"{{\"api_result\":1,\"api_data\":{{\"api_basic\":{{\"api_experience\":{experience}}},\"api_ship\":[]}}}}";

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var start = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (start.Elapsed > timeout) throw new TimeoutException("Synthetic capture did not initialize in time.");
            await Task.Delay(25);
        }
    }

    private static long GetCursorId(string cursorPath)
    {
        if (!File.Exists(cursorPath)) return -1;
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(cursorPath));
        return document.RootElement.GetProperty("LastId").GetInt64();
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(AppContext.BaseDirectory, "SenkaSqliteTests", Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public string DirectoryPath => _directory;
        public string CursorPath => System.IO.Path.Combine(_directory, "capture-cursor.json");

        public DatabaseFixture()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "ElectronicObserver.sqlite");
            using var connection = OpenReadWrite();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE ApiFiles (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ApiFileType INTEGER NOT NULL,
                    Content BLOB NOT NULL,
                    ExpeditionRecordId INTEGER NULL,
                    Name TEXT NOT NULL,
                    SortieRecordId INTEGER NULL,
                    TimeStamp TEXT NOT NULL,
                    Version INTEGER NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        public SqliteConnection OpenReadWrite()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = 2
            }.ToString());
            connection.Open();
            return connection;
        }

        public void SetWalMode()
        {
            using var connection = OpenReadWrite();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            Assert.Equal("wal", command.ExecuteScalar()?.ToString(), ignoreCase: true);
        }

        public void Insert(string name, string body, int fileType, int version = 1, DateTimeOffset? timestamp = null) =>
            Insert(null, name, body, fileType, version, timestamp);

        public void InsertMany(int count)
        {
            using var connection = OpenReadWrite();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ApiFiles(ApiFileType, Content, Name, TimeStamp, Version)
                VALUES(1, $content, 'api_port/port', '2026-11-28 12:00:00.0000000', 1);
                """;
            command.Parameters.AddWithValue("$content", Compress("{}"));
            for (var i = 0; i < count; i++)
            {
                using var transaction = connection.BeginTransaction();
                command.Transaction = transaction;
                command.ExecuteNonQuery();
                transaction.Commit();
                Thread.Sleep(2);
            }
        }

        public void Insert(SqliteConnection? existing, string name, string body, int fileType, int version = 1,
            DateTimeOffset? timestamp = null)
        {
            if (existing is not null)
            {
                InsertCore(existing, name, body, fileType, version, timestamp);
                return;
            }

            using var connection = OpenReadWrite();
            InsertCore(connection, name, body, fileType, version, timestamp);
        }

        private static void InsertCore(SqliteConnection connection, string name, string body, int fileType, int version,
            DateTimeOffset? timestamp)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ApiFiles(ApiFileType, Content, Name, TimeStamp, Version)
                VALUES($type, $content, $name, $timestamp, $version);
                """;
            command.Parameters.AddWithValue("$type", fileType);
            command.Parameters.AddWithValue("$content", Compress(body));
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$timestamp", timestamp?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture)
                ?? "2026-11-28 12:00:00.0000000");
            command.Parameters.AddWithValue("$version", version);
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(_directory))
                try { File.Delete(file); } catch (IOException) { }
            try { Directory.Delete(_directory, recursive: false); } catch (IOException) { }
        }

        private static byte[] Compress(string body)
        {
            using var output = new MemoryStream();
            using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes(body)))
                input.CopyTo(brotli);
            return output.ToArray();
        }
    }
}
