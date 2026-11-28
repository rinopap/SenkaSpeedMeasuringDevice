using System.Globalization;
using Microsoft.Data.Sqlite;
using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Capture;

/// <summary>Local durable journal for admiral-only observations and per-stream cursors.</summary>
public sealed class AdmiralObservationStore : IDisposable
{
    private const string PortStream = "api_port/port";
    private readonly string _path;

    public AdmiralObservationStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS Meta(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS LegacyCsvRows(Ordinal INTEGER PRIMARY KEY, Line TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Generations(
                IdentityKey TEXT NOT NULL, GenerationId TEXT NOT NULL PRIMARY KEY,
                CreatedUtc TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Generations_Identity ON Generations(IdentityKey, CreatedUtc);
            CREATE TABLE IF NOT EXISTS MeasurementSessions(
                SessionId TEXT NOT NULL PRIMARY KEY, GenerationId TEXT NOT NULL,
                StartedAtUtc TEXT NOT NULL, StartAfterId INTEGER NOT NULL,
                IsActive INTEGER NOT NULL CHECK(IsActive IN (0,1)));
            CREATE INDEX IF NOT EXISTS IX_MeasurementSessions_GenerationActive
                ON MeasurementSessions(GenerationId, IsActive, StartedAtUtc);
            CREATE TABLE IF NOT EXISTS CaptureCursors(
                GenerationId TEXT NOT NULL, Stream TEXT NOT NULL, SeriesId TEXT NOT NULL, LastId INTEGER NOT NULL,
                LastExperience INTEGER NULL, SessionNumber INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(GenerationId, Stream, SeriesId));
            CREATE TABLE IF NOT EXISTS Events(
                EventId TEXT PRIMARY KEY, GenerationId TEXT NULL, Stream TEXT NOT NULL,
                RowId INTEGER NULL, TimestampUtc TEXT NOT NULL, State TEXT NOT NULL,
                ErrorCode TEXT NULL, TimestampKnown INTEGER NOT NULL DEFAULT 1,
                DetectedAtUtc TEXT NULL);
            CREATE TABLE IF NOT EXISTS AdmiralObservations(
                EventId TEXT NOT NULL REFERENCES Events(EventId), SeriesId TEXT NOT NULL, TimestampUtc TEXT NOT NULL,
                Experience INTEGER NOT NULL CHECK(Experience >= 0),
                Source TEXT NOT NULL, PRIMARY KEY(EventId,SeriesId));
            CREATE INDEX IF NOT EXISTS IX_AdmiralObservations_Time ON AdmiralObservations(TimestampUtc, EventId);
            CREATE TABLE IF NOT EXISTS LegacyJsonShipRows(Ordinal INTEGER PRIMARY KEY, Line TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS JsonShipObservations(
                EventId TEXT NOT NULL, SeriesId TEXT NOT NULL, TimestampUtc TEXT NOT NULL,
                Experience INTEGER NOT NULL CHECK(Experience >= 0), Source TEXT NOT NULL,
                PRIMARY KEY(EventId,SeriesId));
            CREATE INDEX IF NOT EXISTS IX_JsonShipObservations_SeriesTime
                ON JsonShipObservations(SeriesId,TimestampUtc,EventId);
            CREATE TABLE IF NOT EXISTS ProcessingIssues(
                EventId TEXT PRIMARY KEY, GenerationId TEXT NOT NULL, Stream TEXT NOT NULL,
                RowId INTEGER NOT NULL, Code TEXT NOT NULL, SeriesId TEXT NULL);
            CREATE TABLE IF NOT EXISTS ShipExpGainEvents(
                EventId TEXT PRIMARY KEY, GenerationId TEXT NOT NULL, SeriesId TEXT NOT NULL,
                SourceRecordId INTEGER NOT NULL, TimestampUtc TEXT NOT NULL,
                Kind TEXT NOT NULL, GainedExp INTEGER NOT NULL CHECK(GainedExp >= 0),
                Meaning TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_ShipExpGainEvents_SeriesTime
                ON ShipExpGainEvents(SeriesId, TimestampUtc, EventId);
            CREATE TABLE IF NOT EXISTS CaptureCoverage(
                GenerationId TEXT NOT NULL, SeriesId TEXT NOT NULL,
                StartedAtUtc TEXT NOT NULL, EndedAtUtc TEXT NOT NULL,
                PRIMARY KEY(GenerationId, SeriesId, StartedAtUtc));
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "ProcessingIssues", "SeriesId", "TEXT NULL");
        EnsureColumn(connection, "Events", "TimestampKnown", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "Events", "DetectedAtUtc", "TEXT NULL");
    }

    public string DatabasePath => _path;

    public MeasurementSessionState GetOrCreateActiveMeasurementSession(string generationId, long highWaterId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = "SELECT SessionId,StartedAtUtc,StartAfterId FROM MeasurementSessions WHERE GenerationId=$generation AND IsActive=1 ORDER BY StartedAtUtc DESC LIMIT 1;";
        find.Parameters.AddWithValue("$generation", generationId);
        using var reader = find.ExecuteReader();
        if (reader.Read())
        {
            var state = new MeasurementSessionState(reader.GetString(0), generationId,
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), reader.GetInt64(2));
            reader.Close();
            transaction.Commit();
            return state;
        }
        reader.Close();
        var created = CreateMeasurementSession(connection, transaction, generationId, highWaterId, DateTimeOffset.UtcNow);
        transaction.Commit();
        return created;
    }

    public MeasurementSessionState StartNewMeasurementSession(string generationId, long highWaterId,
        DateTimeOffset? startedAtUtc = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var deactivate = connection.CreateCommand())
        {
            deactivate.Transaction = transaction;
            deactivate.CommandText = "UPDATE MeasurementSessions SET IsActive=0 WHERE GenerationId=$generation AND IsActive=1;";
            deactivate.Parameters.AddWithValue("$generation", generationId);
            deactivate.ExecuteNonQuery();
        }
        var created = CreateMeasurementSession(connection, transaction, generationId, highWaterId,
            startedAtUtc?.ToUniversalTime() ?? DateTimeOffset.UtcNow);
        transaction.Commit();
        return created;
    }

    private static MeasurementSessionState CreateMeasurementSession(SqliteConnection connection,
        SqliteTransaction transaction, string generationId, long highWaterId, DateTimeOffset startedAtUtc)
    {
        var state = new MeasurementSessionState(Guid.NewGuid().ToString("N"), generationId,
            startedAtUtc, highWaterId);
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO MeasurementSessions(SessionId,GenerationId,StartedAtUtc,StartAfterId,IsActive) VALUES($session,$generation,$started,$after,1);";
        insert.Parameters.AddWithValue("$session", state.SessionId);
        insert.Parameters.AddWithValue("$generation", generationId);
        insert.Parameters.AddWithValue("$started", state.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$after", highWaterId);
        insert.ExecuteNonQuery();
        return state;
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string declaration)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name=$column;";
        check.Parameters.AddWithValue("$table", table);
        check.Parameters.AddWithValue("$column", column);
        if (Convert.ToInt32(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) return;
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration};";
        alter.ExecuteNonQuery();
    }

    public void ImportLegacyCsvOnce(IReadOnlyList<ExperienceSample> samples, string? existingCsvPath = null)
    {
        var rawRows = existingCsvPath is null ? [] : CsvStorage.ReadRawRows(existingCsvPath, "admiral_exp");
        if (existingCsvPath is not null && rawRows is null)
            throw new InvalidDataException("既存のexp_log.csvのヘッダーが互換形式ではありません。元ファイルは変更していません。");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (GetMeta(connection, transaction, "legacy-admiral-imported") == "1") return;
        if (existingCsvPath is not null)
        {
            for (var i = 0; i < rawRows!.Count; i++)
            {
                using var line = connection.CreateCommand();
                line.Transaction = transaction;
                line.CommandText = "INSERT INTO LegacyCsvRows(Ordinal,Line) VALUES($ordinal,$line);";
                line.Parameters.AddWithValue("$ordinal", i);
                line.Parameters.AddWithValue("$line", rawRows[i]);
                line.ExecuteNonQuery();
            }
            SetMeta(connection, transaction, "legacy-csv-contains-imported-rows", File.Exists(existingCsvPath) ? "1" : "0");
        }
        else SetMeta(connection, transaction, "legacy-csv-contains-imported-rows", "0");
        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            InsertObservation(connection, transaction, $"legacy-admiral:{i:D12}", null,
                "legacy", null, sample.Timestamp, sample.Experience, "legacy", "legacy");
        }
        SetMeta(connection, transaction, "legacy-admiral-imported", "1");
        transaction.Commit();
    }

    public string GetOrCreateGeneration(string identityKey, long highWaterId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = """
            SELECT g.GenerationId, COALESCE(c.LastId, 0)
            FROM Generations g LEFT JOIN (
              SELECT GenerationId, MAX(LastId) AS LastId FROM CaptureCursors GROUP BY GenerationId
            ) c ON c.GenerationId=g.GenerationId
            WHERE g.IdentityKey=$identity ORDER BY g.CreatedUtc DESC LIMIT 1;
            """;
        lookup.Parameters.AddWithValue("$identity", identityKey);
        using var reader = lookup.ExecuteReader();
        string? generation = null;
        long lastId = 0;
        if (reader.Read()) { generation = reader.GetString(0); lastId = reader.GetInt64(1); }
        reader.Close();
        if (generation is null || highWaterId < lastId)
        {
            generation = Guid.NewGuid().ToString("N");
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO Generations(IdentityKey,GenerationId,CreatedUtc) VALUES($identity,$generation,$now);";
            insert.Parameters.AddWithValue("$identity", identityKey);
            insert.Parameters.AddWithValue("$generation", generation);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
        return generation;
    }

    public long? GetCursor(string generationId, string seriesId, string stream = PortStream)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT LastId FROM CaptureCursors WHERE GenerationId=$generation AND Stream=$stream AND SeriesId=$series;";
        command.Parameters.AddWithValue("$generation", generationId);
        command.Parameters.AddWithValue("$stream", stream);
        command.Parameters.AddWithValue("$series", seriesId);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Skips port rows observed by another source during a confirmed source handoff.</summary>
    public void SkipPortBacklog(string generationId, string seriesId, long highWaterId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (GetMeta(connection, transaction, "port-backlog-skip:" + token) == "1")
        {
            transaction.Commit();
            return;
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO CaptureCursors(GenerationId,Stream,SeriesId,LastId,LastExperience,SessionNumber)
            VALUES($generation,$stream,$series,$id,NULL,0)
            ON CONFLICT(GenerationId,Stream,SeriesId) DO UPDATE SET
              LastId=MAX(CaptureCursors.LastId,excluded.LastId);
            """;
        command.Parameters.AddWithValue("$generation", generationId);
        command.Parameters.AddWithValue("$stream", PortStream);
        command.Parameters.AddWithValue("$series", seriesId);
        command.Parameters.AddWithValue("$id", highWaterId);
        command.ExecuteNonQuery();
        SetMeta(connection, transaction, "port-backlog-skip:" + token, "1");
        transaction.Commit();
    }

    public string GetCurrentPortMeasurementSeriesId(string generationId, string cursorSeriesId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SessionNumber FROM CaptureCursors WHERE GenerationId=$generation AND Stream=$stream AND SeriesId=$series;";
        command.Parameters.AddWithValue("$generation", generationId);
        command.Parameters.AddWithValue("$stream", PortStream);
        command.Parameters.AddWithValue("$series", cursorSeriesId);
        var value = command.ExecuteScalar();
        var sessionNumber = value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        return $"{cursorSeriesId}:{sessionNumber.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Maps an API record to the latest port-observed series boundary at or before its row ID.</summary>
    public string GetMeasurementSeriesForApiRecord(string generationId, string cursorSeriesId, long sourceRecordId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.SeriesId
            FROM Events e JOIN AdmiralObservations a ON a.EventId=e.EventId
            WHERE e.GenerationId=$generation AND e.Stream=$stream AND e.RowId<=$record
              AND a.SeriesId LIKE $prefix
            ORDER BY e.RowId DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$generation", generationId);
        command.Parameters.AddWithValue("$stream", PortStream);
        command.Parameters.AddWithValue("$record", sourceRecordId);
        command.Parameters.AddWithValue("$prefix", cursorSeriesId + ":%");
        var series = command.ExecuteScalar() as string;
        return series ?? cursorSeriesId + ":0";
    }

    /// <summary>Stores parsed events, bounded issues and their stream cursor atomically.</summary>
    public void CommitPortBatch(string generationId, string seriesId, SqliteApiFileBatch batch,
        DateTimeOffset historyCutoffUtc)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var cursor = ReadCursor(connection, transaction, generationId, PortStream, seriesId);
        foreach (var response in batch.Responses.OrderBy(x => x.SourceRecordId ?? long.MaxValue))
        {
            var rowId = response.SourceRecordId ?? throw new InvalidDataException("SQLite response has no row id.");
            var eventId = CreateEventId(generationId, PortStream, rowId);
            var identityResponse = response with { EventId = eventId, SourceGenerationId = generationId };
            var snapshot = ApiResponseParser.ParseAdmiralPort(identityResponse);
            if (snapshot?.AdmiralExperience is not { } experience)
            {
                InsertIssue(connection, transaction, eventId, generationId, PortStream, rowId,
                    "invalid-port-experience", $"{seriesId}:{cursor.SessionNumber.ToString(CultureInfo.InvariantCulture)}");
                InsertEvent(connection, transaction, eventId, generationId, PortStream, rowId,
                    response.Timestamp, "invalid", "invalid-port-experience");
                continue;
            }
            if (ObservationExists(connection, transaction, eventId, seriesId)) continue;
            var sessionNumber = cursor.SessionNumber;
            if (cursor.LastExperience is { } prior && experience < prior) sessionNumber++;
            var observationSeries = $"{seriesId}:{sessionNumber.ToString(CultureInfo.InvariantCulture)}";
            var inWindow = response.Timestamp >= historyCutoffUtc;
            if (inWindow)
                InsertObservation(connection, transaction, eventId, generationId, PortStream, rowId,
                    response.Timestamp, experience, observationSeries, "sqlite");
            else
                InsertEvent(connection, transaction, eventId, generationId, PortStream, rowId,
                    response.Timestamp, "outside-history-window", null);
            cursor = new CursorValues(rowId, experience, sessionNumber);
        }

        foreach (var issue in batch.Issues)
        {
            var eventId = CreateEventId(generationId, PortStream, issue.RowId);
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            InsertIssue(connection, transaction, eventId, generationId, PortStream, issue.RowId, issue.Code,
                issue.MeasurementSeriesId);
            InsertEvent(connection, transaction, eventId, generationId, PortStream, issue.RowId,
                issue.TimestampUtc ?? DateTimeOffset.UtcNow, "issue", issue.Code,
                timestampKnown: issue.TimestampUtc is not null);
        }

        var nextId = Math.Max(batch.ScannedThroughId, cursor.LastId);
        using var save = connection.CreateCommand();
        save.Transaction = transaction;
        save.CommandText = """
            INSERT INTO CaptureCursors(GenerationId,Stream,SeriesId,LastId,LastExperience,SessionNumber)
            VALUES($generation,$stream,$series,$id,$experience,$session)
            ON CONFLICT(GenerationId,Stream,SeriesId) DO UPDATE SET
              LastId=MAX(CaptureCursors.LastId,excluded.LastId),
              LastExperience=COALESCE(excluded.LastExperience,CaptureCursors.LastExperience),
              SessionNumber=MAX(CaptureCursors.SessionNumber,excluded.SessionNumber);
            """;
        save.Parameters.AddWithValue("$generation", generationId);
        save.Parameters.AddWithValue("$stream", PortStream);
        save.Parameters.AddWithValue("$series", seriesId);
        save.Parameters.AddWithValue("$id", nextId);
        save.Parameters.AddWithValue("$experience", cursor.LastExperience is null ? DBNull.Value : cursor.LastExperience.Value);
        save.Parameters.AddWithValue("$session", cursor.SessionNumber);
        save.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>Stores ship-exp result events, parse issues, and one API cursor atomically.</summary>
    public void CommitShipGainBatch(string generationId, string seriesId, string stream, SqliteApiFileBatch batch)
    {
        if (!ShipExpGainParser.IsSupportedStream(stream) &&
            stream != "api_req_combined_battle/battleresult")
            throw new ArgumentException("API stream is not allowlisted.", nameof(stream));
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var cursor = ReadCursor(connection, transaction, generationId, stream, seriesId);
        var nextId = Math.Max(cursor.LastId, batch.ScannedThroughId);

        foreach (var response in batch.Responses.OrderBy(x => x.SourceRecordId ?? long.MaxValue))
        {
            var rowId = response.SourceRecordId ?? throw new InvalidDataException("SQLite response has no row id.");
            nextId = Math.Max(nextId, rowId);
            var eventId = CreateEventId(generationId, stream, rowId);
            var sourceResponse = response with
            {
                EventId = eventId,
                SourceGenerationId = generationId,
                MeasurementSeriesId = response.MeasurementSeriesId ?? seriesId
            };
            var parsed = ShipExpGainParser.Parse(sourceResponse);
            if (parsed.Event is not { } gain)
            {
                var code = parsed.IssueCode ?? "invalid-ship-exp-event";
                InsertEvent(connection, transaction, eventId, generationId, stream, rowId,
                    response.Timestamp, "invalid", code);
                InsertIssue(connection, transaction, eventId, generationId, stream, rowId, code,
                    sourceResponse.MeasurementSeriesId);
                continue;
            }

            InsertEvent(connection, transaction, eventId, generationId, stream, rowId,
                gain.ObservedAt, "accepted", null);
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO ShipExpGainEvents
                    (EventId,GenerationId,SeriesId,SourceRecordId,TimestampUtc,Kind,GainedExp,Meaning)
                VALUES($event,$generation,$series,$record,$timestamp,$kind,$gain,$meaning);
                """;
            insert.Parameters.AddWithValue("$event", gain.EventId);
            insert.Parameters.AddWithValue("$generation", gain.SourceGenerationId);
            insert.Parameters.AddWithValue("$series", gain.MeasurementSeriesId);
            insert.Parameters.AddWithValue("$record", gain.SourceRecordId);
            insert.Parameters.AddWithValue("$timestamp", gain.ObservedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$kind", gain.Kind.ToString());
            insert.Parameters.AddWithValue("$gain", gain.GainedExp);
            insert.Parameters.AddWithValue("$meaning", gain.Meaning);
            insert.ExecuteNonQuery();
        }

        foreach (var issue in batch.Issues)
        {
            nextId = Math.Max(nextId, issue.RowId);
            var eventId = CreateEventId(generationId, stream, issue.RowId);
            InsertIssue(connection, transaction, eventId, generationId, stream, issue.RowId, issue.Code,
                issue.MeasurementSeriesId);
            InsertEvent(connection, transaction, eventId, generationId, stream, issue.RowId,
                issue.TimestampUtc ?? DateTimeOffset.UtcNow,
                issue.Code == "out-of-scope-api" ? "out-of-scope" : "issue", issue.Code,
                timestampKnown: issue.TimestampUtc is not null);
        }

        using var save = connection.CreateCommand();
        save.Transaction = transaction;
        save.CommandText = """
            INSERT INTO CaptureCursors(GenerationId,Stream,SeriesId,LastId,LastExperience,SessionNumber)
            VALUES($generation,$stream,$series,$id,NULL,0)
            ON CONFLICT(GenerationId,Stream,SeriesId) DO UPDATE SET
              LastId=MAX(CaptureCursors.LastId,excluded.LastId);
            """;
        save.Parameters.AddWithValue("$generation", generationId);
        save.Parameters.AddWithValue("$stream", stream);
        save.Parameters.AddWithValue("$series", seriesId);
        save.Parameters.AddWithValue("$id", nextId);
        save.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>Extends a known-good interval, or starts a new segment after a polling gap.</summary>
    public void RecordCoverageSuccess(string generationId, string seriesId, DateTimeOffset timestampUtc,
        TimeSpan maximumContinuousGap)
    {
        if (maximumContinuousGap <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumContinuousGap));
        var now = timestampUtc.ToUniversalTime();
        var nowText = now.ToString("O", CultureInfo.InvariantCulture);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var latest = connection.CreateCommand();
        latest.Transaction = transaction;
        latest.CommandText = "SELECT StartedAtUtc,EndedAtUtc FROM CaptureCoverage WHERE GenerationId=$generation AND SeriesId=$series ORDER BY EndedAtUtc DESC LIMIT 1;";
        latest.Parameters.AddWithValue("$generation", generationId);
        latest.Parameters.AddWithValue("$series", seriesId);
        using var reader = latest.ExecuteReader();
        string? startedText = null;
        string? endedText = null;
        if (reader.Read()) { startedText = reader.GetString(0); endedText = reader.GetString(1); }
        reader.Close();

        if (startedText is not null && endedText is not null &&
            DateTimeOffset.TryParse(endedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ended) &&
            now >= ended && now - ended <= maximumContinuousGap)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE CaptureCoverage SET EndedAtUtc=$ended WHERE GenerationId=$generation AND SeriesId=$series AND StartedAtUtc=$started;";
            update.Parameters.AddWithValue("$ended", nowText);
            update.Parameters.AddWithValue("$generation", generationId);
            update.Parameters.AddWithValue("$series", seriesId);
            update.Parameters.AddWithValue("$started", startedText);
            update.ExecuteNonQuery();
        }
        else
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO CaptureCoverage(GenerationId,SeriesId,StartedAtUtc,EndedAtUtc) VALUES($generation,$series,$started,$ended);";
            insert.Parameters.AddWithValue("$generation", generationId);
            insert.Parameters.AddWithValue("$series", seriesId);
            insert.Parameters.AddWithValue("$started", nowText);
            insert.Parameters.AddWithValue("$ended", nowText);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<ShipExpGainEvent> LoadShipExpGainEvents(string seriesId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EventId,GenerationId,SeriesId,SourceRecordId,TimestampUtc,Kind,GainedExp,Meaning FROM ShipExpGainEvents WHERE SeriesId=$series OR SeriesId LIKE $prefix ORDER BY TimestampUtc,SourceRecordId;";
        command.Parameters.AddWithValue("$series", seriesId.TrimEnd(':'));
        command.Parameters.AddWithValue("$prefix", seriesId.EndsWith(':') ? seriesId + "%" : seriesId + ":%");
        using var reader = command.ExecuteReader();
        var events = new List<ShipExpGainEvent>();
        while (reader.Read())
        {
            if (!DateTimeOffset.TryParse(reader.GetString(4), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp) ||
                !Enum.TryParse<ShipExpGainKind>(reader.GetString(5), out var kind)) continue;
            events.Add(new ShipExpGainEvent(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), timestamp, kind, reader.GetInt64(6), reader.GetString(7)));
        }
        return events;
    }

    public IReadOnlyList<CaptureCoverageSegment> LoadCoverageSegments(string seriesId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT GenerationId,SeriesId,StartedAtUtc,EndedAtUtc FROM CaptureCoverage WHERE SeriesId=$series OR SeriesId LIKE $prefix ORDER BY StartedAtUtc;";
        command.Parameters.AddWithValue("$series", seriesId.TrimEnd(':'));
        command.Parameters.AddWithValue("$prefix", seriesId.EndsWith(':') ? seriesId + "%" : seriesId + ":%");
        using var reader = command.ExecuteReader();
        var segments = new List<CaptureCoverageSegment>();
        while (reader.Read())
        {
            if (!DateTimeOffset.TryParse(reader.GetString(2), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start) ||
                !DateTimeOffset.TryParse(reader.GetString(3), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var end)) continue;
            segments.Add(new CaptureCoverageSegment(reader.GetString(0), reader.GetString(1), start, end));
        }
        return segments;
    }

    /// <summary>Returns known bad or unsupported ship-result record timestamps; callers mark their hour incomplete.</summary>
    public IReadOnlyList<DateTimeOffset> LoadShipExpGainIssueTimes(string seriesId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.TimestampUtc FROM Events e
            JOIN ProcessingIssues i ON i.EventId=e.EventId
            WHERE i.Stream IN (
                'api_req_sortie/battleresult',
                'api_req_combined_battle/battleresult',
                'api_req_practice/battle_result',
              'api_req_mission/result')
              AND (i.SeriesId IS NULL OR i.SeriesId=$series OR i.SeriesId LIKE $prefix)
            ORDER BY e.TimestampUtc;
            """;
        command.Parameters.AddWithValue("$series", seriesId.TrimEnd(':'));
        command.Parameters.AddWithValue("$prefix", seriesId.EndsWith(':') ? seriesId + "%" : seriesId + ":%");
        using var reader = command.ExecuteReader();
        var result = new List<DateTimeOffset>();
        while (reader.Read())
            if (DateTimeOffset.TryParse(reader.GetString(0), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
                result.Add(timestamp);
        return result;
    }

    /// <summary>Maps timestamp-less result issues to the surrounding known result rows when possible.</summary>
    public IReadOnlyList<ShipExpIssueTimeWindow> LoadUnknownShipExpGainIssueWindows(string seriesId)
    {
        using var connection = Open();
        var issues = new List<(string Generation, string Stream, long RowId, string? Series)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT i.GenerationId,i.Stream,i.RowId,i.SeriesId
                FROM Events e JOIN ProcessingIssues i ON i.EventId=e.EventId
                WHERE e.TimestampKnown=0
                  AND i.Stream IN (
                    'api_req_sortie/battleresult',
                    'api_req_combined_battle/battleresult',
                    'api_req_practice/battle_result',
                    'api_req_mission/result')
                  AND (i.SeriesId IS NULL OR i.SeriesId=$series OR i.SeriesId LIKE $prefix)
                ORDER BY i.GenerationId,i.Stream,i.RowId;
                """;
            command.Parameters.AddWithValue("$series", seriesId.TrimEnd(':'));
            command.Parameters.AddWithValue("$prefix", seriesId.EndsWith(':') ? seriesId + "%" : seriesId + ":%");
            using var reader = command.ExecuteReader();
            while (reader.Read())
                issues.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        var result = new List<ShipExpIssueTimeWindow>(issues.Count);
        foreach (var issue in issues)
        {
            DateTimeOffset? previous = null;
            DateTimeOffset? next = null;
            using var previousCommand = connection.CreateCommand();
            previousCommand.CommandText = """
                SELECT g.TimestampUtc FROM ShipExpGainEvents g
                JOIN Events e ON e.EventId=g.EventId
                WHERE g.GenerationId=$generation AND g.SeriesId=$series
                  AND g.SourceRecordId<$row AND e.TimestampKnown=1
                ORDER BY g.SourceRecordId DESC LIMIT 1;
                """;
            previousCommand.Parameters.AddWithValue("$generation", issue.Generation);
            previousCommand.Parameters.AddWithValue("$series", issue.Series ?? seriesId.TrimEnd(':'));
            previousCommand.Parameters.AddWithValue("$row", issue.RowId);
            previous = ParseTimestamp(previousCommand.ExecuteScalar());

            using var nextCommand = connection.CreateCommand();
            nextCommand.CommandText = """
                SELECT g.TimestampUtc FROM ShipExpGainEvents g
                JOIN Events e ON e.EventId=g.EventId
                WHERE g.GenerationId=$generation AND g.SeriesId=$series
                  AND g.SourceRecordId>$row AND e.TimestampKnown=1
                ORDER BY g.SourceRecordId LIMIT 1;
                """;
            nextCommand.Parameters.AddWithValue("$generation", issue.Generation);
            nextCommand.Parameters.AddWithValue("$series", issue.Series ?? seriesId.TrimEnd(':'));
            nextCommand.Parameters.AddWithValue("$row", issue.RowId);
            next = ParseTimestamp(nextCommand.ExecuteScalar());

            if (previous is { } before && next is { } after && after < before)
            {
                // Non-monotonic stored timestamps cannot safely bound this issue.
                result.Add(new ShipExpIssueTimeWindow(null, null));
            }
            else result.Add(new ShipExpIssueTimeWindow(previous, next));
        }
        return result;
    }

    private static DateTimeOffset? ParseTimestamp(object? value) => value is string text &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)
            ? timestamp : null;

    public void ExportShipExpGainCsv(string path)
    {
        var events = LoadShipExpGainEventsForExport();
        CsvStorage.WriteAtomicShipGain(path, events);
    }

    private IReadOnlyList<ShipExpGainEvent> LoadShipExpGainEventsForExport()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EventId,GenerationId,SeriesId,SourceRecordId,TimestampUtc,Kind,GainedExp,Meaning FROM ShipExpGainEvents ORDER BY TimestampUtc,SourceRecordId;";
        using var reader = command.ExecuteReader();
        var events = new List<ShipExpGainEvent>();
        while (reader.Read())
        {
            if (!DateTimeOffset.TryParse(reader.GetString(4), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp) ||
                !Enum.TryParse<ShipExpGainKind>(reader.GetString(5), out var kind)) continue;
            events.Add(new ShipExpGainEvent(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), timestamp, kind, reader.GetInt64(6), reader.GetString(7)));
        }
        return events;
    }

    public bool RecordJsonObservation(string eventId, ExperienceSample sample, string seriesId = "json")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesId);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var changed = InsertObservation(connection, transaction, "json:" + eventId, null, "api_port/port", null,
            sample.Timestamp, sample.Experience, seriesId, "json");
        transaction.Commit();
        return changed;
    }

    public (bool AdmiralAdded, bool ShipAdded) RecordJsonPortObservation(string eventId,
        ExperienceSample? admiralSample, string admiralSeriesId,
        ExperienceSample? shipSample, string shipSeriesId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        if (admiralSample is null && shipSample is null) return (false, false);
        ArgumentException.ThrowIfNullOrWhiteSpace(admiralSeriesId);
        ArgumentException.ThrowIfNullOrWhiteSpace(shipSeriesId);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var admiralAdded = admiralSample is { } admiral &&
            InsertObservation(connection, transaction, "json:" + eventId, null, "api_port/port", null,
                admiral.Timestamp, admiral.Experience, admiralSeriesId, "json");
        var shipAdded = shipSample is { } ships &&
            InsertJsonShipObservation(connection, transaction, "json:" + eventId, shipSeriesId, ships, "json");
        transaction.Commit();
        return (admiralAdded, shipAdded);
    }

    public void ImportLegacyJsonShipCsvOnce(string path, string initialSeriesId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(initialSeriesId);
        var rawRows = CsvStorage.ReadRawRows(path, "ship_total_exp");
        if (rawRows is null)
            throw new InvalidDataException("既存のship_exp_log.csvのヘッダーが互換形式ではありません。元ファイルは変更していません。");
        var samples = CsvStorage.Load(path, "ship_total_exp");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (GetMeta(connection, transaction, "legacy-json-ship-imported") == "1") return;
        for (var i = 0; i < rawRows.Count; i++)
        {
            using var line = connection.CreateCommand();
            line.Transaction = transaction;
            line.CommandText = "INSERT INTO LegacyJsonShipRows(Ordinal,Line) VALUES($ordinal,$line);";
            line.Parameters.AddWithValue("$ordinal", i);
            line.Parameters.AddWithValue("$line", rawRows[i]);
            line.ExecuteNonQuery();
        }
        for (var i = 0; i < samples.Count; i++)
            InsertJsonShipObservation(connection, transaction, $"legacy-json-ship:{i:D12}", initialSeriesId,
                samples[i], "legacy");
        SetMeta(connection, transaction, "legacy-json-ship-imported", "1");
        transaction.Commit();
    }

    public bool RecordJsonShipObservation(string eventId, ExperienceSample sample, string seriesId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesId);
        if (sample.Experience < 0) throw new ArgumentOutOfRangeException(nameof(sample));
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var changed = InsertJsonShipObservation(connection, transaction, "json:" + eventId, seriesId, sample, "json");
        transaction.Commit();
        return changed;
    }

    public IReadOnlyList<ExperienceSample> LoadJsonShipSamples(string seriesId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TimestampUtc,Experience,SeriesId FROM JsonShipObservations WHERE SeriesId=$series ORDER BY TimestampUtc,EventId;";
        command.Parameters.AddWithValue("$series", seriesId);
        using var reader = command.ExecuteReader();
        var samples = new List<ExperienceSample>();
        while (reader.Read())
        {
            if (!DateTimeOffset.TryParse(reader.GetString(0), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)) continue;
            samples.Add(new ExperienceSample(timestamp.ToLocalTime(), reader.GetInt64(1), reader.GetString(2)));
        }
        return samples;
    }

    public void ExportJsonShipCsv(string path)
    {
        using var connection = Open();
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT Value FROM Meta WHERE Key='legacy-json-ship-imported';";
        if (check.ExecuteScalar() as string != "1")
            throw new InvalidOperationException("既存CSVの形式確認が終わるまでCSV出力を保留しています。");
        var rawRows = new List<string>();
        using (var raw = connection.CreateCommand())
        {
            raw.CommandText = "SELECT Line FROM LegacyJsonShipRows ORDER BY Ordinal;";
            using var rawReader = raw.ExecuteReader();
            while (rawReader.Read()) rawRows.Add(rawReader.GetString(0));
        }
        var newSamples = new List<ExperienceSample>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT MIN(TimestampUtc),MIN(Experience) FROM JsonShipObservations WHERE Source='json'
                GROUP BY EventId ORDER BY MIN(TimestampUtc),EventId;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (DateTimeOffset.TryParse(reader.GetString(0), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
                    newSamples.Add(new ExperienceSample(timestamp.ToLocalTime(), reader.GetInt64(1)));
        }
        CsvStorage.WriteAtomicPreservingRows(path, "ship_total_exp", rawRows, newSamples);
    }

    /// <summary>Moves one user-confirmed admiral series while keeping source and event provenance.</summary>
    public int MoveAdmiralObservationsToSeries(string sourceSeriesId, string destinationSeriesId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSeriesId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationSeriesId);
        if (string.Equals(sourceSeriesId, destinationSeriesId, StringComparison.Ordinal)) return 0;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        int sourceCount;
        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM AdmiralObservations WHERE SeriesId=$source;";
            count.Parameters.AddWithValue("$source", sourceSeriesId);
            sourceCount = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            INSERT OR IGNORE INTO AdmiralObservations(EventId,SeriesId,TimestampUtc,Experience,Source)
            SELECT EventId,$destination,TimestampUtc,Experience,Source FROM AdmiralObservations WHERE SeriesId=$source;
            DELETE FROM AdmiralObservations WHERE SeriesId=$source;
            """;
        update.Parameters.AddWithValue("$destination", destinationSeriesId);
        update.Parameters.AddWithValue("$source", sourceSeriesId);
        update.ExecuteNonQuery();
        transaction.Commit();
        return sourceCount;
    }

    public IReadOnlyList<ExperienceSample> LoadAdmiralSamples(string? seriesPrefix = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TimestampUtc,Experience,SeriesId FROM AdmiralObservations
            WHERE $prefix IS NULL OR substr(SeriesId,1,length($prefix))=$prefix
            GROUP BY EventId,SeriesId ORDER BY TimestampUtc,EventId;
            """;
        command.Parameters.AddWithValue("$prefix", (object?)seriesPrefix ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var result = new List<ExperienceSample>();
        while (reader.Read())
        {
            if (!DateTimeOffset.TryParse(reader.GetString(0), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)) continue;
            result.Add(new ExperienceSample(timestamp.ToLocalTime(), reader.GetInt64(1), reader.GetString(2)));
        }
        return result;
    }

    public void ExportAdmiralCsv(string path)
    {
        using var connection = Open();
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT Value FROM Meta WHERE Key='legacy-admiral-imported';";
        if (check.ExecuteScalar() as string != "1")
            throw new InvalidOperationException("既存CSVの形式確認が終わるまでCSV出力を保留しています。");
        var rawRows = new List<string>();
        using (var raw = connection.CreateCommand())
        {
            raw.CommandText = "SELECT Line FROM LegacyCsvRows ORDER BY Ordinal;";
            using var rawReader = raw.ExecuteReader();
            while (rawReader.Read()) rawRows.Add(rawReader.GetString(0));
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.TimestampUtc,a.Experience FROM AdmiralObservations a
            WHERE a.SeriesId=(SELECT MAX(b.SeriesId) FROM AdmiralObservations b WHERE b.EventId=a.EventId)
              AND a.Source<>'sqlite'
              AND ($legacyInBaseline=0 OR a.Source<>'legacy')
            ORDER BY a.TimestampUtc,a.EventId;
            """;
        command.Parameters.AddWithValue("$legacyInBaseline",
            GetMeta(connection, null, "legacy-csv-contains-imported-rows") == "1" ? 1 : 0);
        var rows = ReadSamples(command);
        CsvStorage.WriteAtomicPreservingRows(path, "admiral_exp", rawRows, rows);
    }

    public void ExportSqliteAdmiralCsv(string path)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.TimestampUtc,a.Experience FROM AdmiralObservations a
            WHERE a.Source='sqlite'
              AND a.SeriesId=(SELECT MAX(b.SeriesId) FROM AdmiralObservations b WHERE b.EventId=a.EventId)
            ORDER BY a.TimestampUtc,a.EventId;
            """;
        CsvStorage.WriteAtomic(path, "admiral_exp", ReadSamples(command));
    }

    public int GetIssueCount(string generationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM ProcessingIssues WHERE GenerationId=$generation) +
                   (SELECT COUNT(*) FROM Events e WHERE e.GenerationId=$generation AND e.State='invalid'
                     AND NOT EXISTS(SELECT 1 FROM ProcessingIssues i WHERE i.EventId=e.EventId));
            """;
        command.Parameters.AddWithValue("$generation", generationId);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public int GetIssueCount(string generationId, string seriesId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ProcessingIssues WHERE GenerationId=$generation AND (SeriesId IS NULL OR SeriesId=$series OR SeriesId LIKE $prefix);";
        command.Parameters.AddWithValue("$generation", generationId);
        command.Parameters.AddWithValue("$series", seriesId.TrimEnd(':'));
        command.Parameters.AddWithValue("$prefix", seriesId.EndsWith(':') ? seriesId + "%" : seriesId + ":%");
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public int GetOutOfScopeEventCount(string generationId, string seriesId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM Events e
            WHERE e.GenerationId=$generation AND e.State='out-of-scope'
              AND EXISTS(SELECT 1 FROM ProcessingIssues i WHERE i.EventId=e.EventId
                         AND (i.SeriesId IS NULL OR i.SeriesId=$series OR i.SeriesId LIKE $prefix));
            """;
        command.Parameters.AddWithValue("$generation", generationId);
        command.Parameters.AddWithValue("$series", seriesId.TrimEnd(':'));
        command.Parameters.AddWithValue("$prefix", seriesId.EndsWith(':') ? seriesId + "%" : seriesId + ":%");
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string CreateEventId(string generationId, string stream, long rowId) =>
        $"{generationId}:{stream}:{rowId.ToString(CultureInfo.InvariantCulture)}";

    private static IReadOnlyList<ExperienceSample> ReadSamples(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var rows = new List<ExperienceSample>();
        while (reader.Read())
        {
            if (DateTimeOffset.TryParse(reader.GetString(0), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
                rows.Add(new ExperienceSample(timestamp.ToLocalTime(), reader.GetInt64(1)));
        }
        return rows;
    }

    private static CursorValues ReadCursor(SqliteConnection connection, SqliteTransaction transaction,
        string generationId, string stream, string seriesId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT LastId,LastExperience,SessionNumber FROM CaptureCursors WHERE GenerationId=$generation AND Stream=$stream AND SeriesId=$series;";
        command.Parameters.AddWithValue("$generation", generationId);
        command.Parameters.AddWithValue("$stream", stream);
        command.Parameters.AddWithValue("$series", seriesId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new CursorValues(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetInt32(2))
            : new CursorValues(0, null, 0);
    }

    private static bool ObservationExists(SqliteConnection connection, SqliteTransaction transaction, string eventId, string seriesId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM AdmiralObservations WHERE EventId=$event AND SeriesId=$series LIMIT 1;";
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$series", seriesId);
        return command.ExecuteScalar() is not null;
    }

    private static void InsertEvent(SqliteConnection connection, SqliteTransaction transaction, string eventId,
        string? generation, string stream, long? rowId, DateTimeOffset timestamp, string state, string? error,
        bool timestampKnown = true)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO Events(EventId,GenerationId,Stream,RowId,TimestampUtc,State,ErrorCode,TimestampKnown,DetectedAtUtc) VALUES($event,$generation,$stream,$row,$timestamp,$state,$error,$known,$detected);";
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$generation", (object?)generation ?? DBNull.Value);
        command.Parameters.AddWithValue("$stream", stream);
        command.Parameters.AddWithValue("$row", (object?)rowId ?? DBNull.Value);
        command.Parameters.AddWithValue("$timestamp", timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$known", timestampKnown ? 1 : 0);
        command.Parameters.AddWithValue("$detected", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static void InsertIssue(SqliteConnection connection, SqliteTransaction transaction, string eventId,
        string generation, string stream, long rowId, string code, string? seriesId = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO ProcessingIssues(EventId,GenerationId,Stream,RowId,Code,SeriesId) VALUES($event,$generation,$stream,$row,$code,$series);";
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$stream", stream);
        command.Parameters.AddWithValue("$row", rowId);
        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$series", (object?)seriesId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static bool InsertObservation(SqliteConnection connection, SqliteTransaction transaction, string eventId,
        string? generation, string stream, long? rowId, DateTimeOffset timestamp, long experience,
        string seriesId, string source = "sqlite")
    {
        InsertEvent(connection, transaction, eventId, generation, stream, rowId, timestamp, "accepted", null);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO AdmiralObservations(EventId,TimestampUtc,Experience,SeriesId,Source) VALUES($event,$timestamp,$experience,$series,$source);";
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$timestamp", timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$experience", experience);
        command.Parameters.AddWithValue("$series", seriesId);
        command.Parameters.AddWithValue("$source", source);
        return command.ExecuteNonQuery() == 1;
    }

    private static bool InsertJsonShipObservation(SqliteConnection connection, SqliteTransaction transaction,
        string eventId, string seriesId, ExperienceSample sample, string source)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO JsonShipObservations(EventId,SeriesId,TimestampUtc,Experience,Source) VALUES($event,$series,$timestamp,$experience,$source);";
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$series", seriesId);
        command.Parameters.AddWithValue("$timestamp", sample.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$experience", sample.Experience);
        command.Parameters.AddWithValue("$source", source);
        return command.ExecuteNonQuery() == 1;
    }

    private static string? GetMeta(SqliteConnection connection, SqliteTransaction? transaction, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Value FROM Meta WHERE Key=$key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void SetMeta(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Meta(Key,Value) VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 5
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous=FULL;";
        command.ExecuteNonQuery();
        return connection;
    }

    public void Dispose() { }

    private sealed record CursorValues(long LastId, long? LastExperience, int SessionNumber);
}
