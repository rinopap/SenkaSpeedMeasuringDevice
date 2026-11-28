using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Capture;

/// <summary>Reads a bounded set of allowlisted ElectronicObserver response rows.</summary>
public sealed class SqliteApiFileReader
{
    public const int SupportedApiFileVersion = 1;
    public const int DefaultMaximumCompressedBytes = 2 * 1024 * 1024;
    public const int DefaultMaximumExpandedBytes = 2 * 1024 * 1024;
    public const int DefaultMaximumBatchBytes = 8 * 1024 * 1024;
    public const int DefaultMaximumRows = 4;
    public const long MaximumInitialHistoryIdWindow = 20_000;
    private const int MaximumJsonDepth = 32;
    private const int ResponseType = 1;

    private static readonly string[] AllowedApiNames =
    [
        "api_port/port",
        "api_req_sortie/battleresult",
        "api_req_practice/battle_result",
        "api_req_mission/result"
    ];
    private const string DeferredCombinedBattleApi = "api_req_combined_battle/battleresult";

    private readonly string _databasePath;
    private readonly int _defaultTimeoutSeconds;
    private readonly int _maximumCompressedBytes;
    private readonly int _maximumExpandedBytes;
    private readonly int _maximumBatchBytes;

    public SqliteApiFileReader(
        string databasePath,
        int defaultTimeoutSeconds = 1,
        int maximumCompressedBytes = DefaultMaximumCompressedBytes,
        int maximumExpandedBytes = DefaultMaximumExpandedBytes,
        int maximumBatchBytes = DefaultMaximumBatchBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (defaultTimeoutSeconds is < 0 or > 30) throw new ArgumentOutOfRangeException(nameof(defaultTimeoutSeconds));
        if (maximumCompressedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCompressedBytes));
        if (maximumExpandedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumExpandedBytes));
        if (maximumBatchBytes < maximumCompressedBytes) throw new ArgumentOutOfRangeException(nameof(maximumBatchBytes));

        _databasePath = Path.GetFullPath(databasePath);
        _defaultTimeoutSeconds = defaultTimeoutSeconds;
        _maximumCompressedBytes = maximumCompressedBytes;
        _maximumExpandedBytes = maximumExpandedBytes;
        _maximumBatchBytes = maximumBatchBytes;
    }

    public Task<long> ReadHighWaterIdAsync(CancellationToken cancellationToken = default) =>
        Task.Run(ReadHighWaterId, cancellationToken);

    /// <summary>Checks only the expected schema and a bounded primary-key aggregate using a read-only connection.</summary>
    public void ValidateDatabase() => _ = ReadHighWaterId();

    /// <summary>Reads a bounded first/last-row fingerprint through primary-key lookups.</summary>
    public Task<string> ReadIdentityFingerprintAsync(CancellationToken cancellationToken = default) =>
        Task.Run(ReadIdentityFingerprint, cancellationToken);

    public Task<SqliteApiFileBatch> ReadBatchAsync(
        long afterId,
        long throughId,
        int maximumRows = DefaultMaximumRows,
        CancellationToken cancellationToken = default,
        string? apiName = null,
        DateTimeOffset? minimumTimestampUtc = null) =>
        Task.Run(() => ReadBatch(afterId, throughId, maximumRows, cancellationToken, apiName, minimumTimestampUtc), cancellationToken);

    /// <summary>Reads only identity metadata for a deliberately unsupported response stream; Content is never selected.</summary>
    public Task<SqliteApiMetadataBatch> ReadMetadataBatchAsync(long afterId, long throughId,
        int maximumRows = DefaultMaximumRows, CancellationToken cancellationToken = default,
        string apiName = "api_req_combined_battle/battleresult") =>
        Task.Run(() => ReadMetadataBatch(afterId, throughId, maximumRows, cancellationToken, apiName), cancellationToken);

    public static long GetInitialHistoryStartId(long highWaterId) =>
        Math.Max(0, highWaterId - MaximumInitialHistoryIdWindow);

    private long ReadHighWaterId()
    {
        using var connection = OpenReadOnly();
        ValidateSchema(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Id), 0) FROM ApiFiles;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private string ReadIdentityFingerprint()
    {
        using var connection = OpenReadOnly();
        ValidateSchema(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, TimeStamp, Version,
                   hex(substr(Content,1,32)), hex(substr(Content,-32))
            FROM ApiFiles
            WHERE Id=(SELECT MIN(Id) FROM ApiFiles);
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<string>(2);
        while (reader.Read())
            rows.Add(string.Join("|", reader.GetInt64(0).ToString(CultureInfo.InvariantCulture),
                reader.GetString(1), reader.GetString(2), reader.GetInt32(3).ToString(CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? "" : reader.GetString(4), reader.IsDBNull(5) ? "" : reader.GetString(5)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows))));
    }

    private SqliteApiFileBatch ReadBatch(long afterId, long throughId, int maximumRows,
        CancellationToken cancellationToken, string? apiName, DateTimeOffset? minimumTimestampUtc)
    {
        if (afterId < 0) throw new ArgumentOutOfRangeException(nameof(afterId));
        if (throughId < 0) throw new ArgumentOutOfRangeException(nameof(throughId));
        if (maximumRows is < 1 or > DefaultMaximumRows) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        cancellationToken.ThrowIfCancellationRequested();
        if (throughId <= afterId) return new SqliteApiFileBatch([], [], throughId, 0, 0);

        using var connection = OpenReadOnly();
        ValidateSchema(connection);
        using var command = connection.CreateCommand();
        if (apiName is not null && !AllowedApiNames.Contains(apiName, StringComparer.Ordinal))
            throw new ArgumentException("API name is not allowlisted.", nameof(apiName));
        if (apiName == DeferredCombinedBattleApi)
            throw new ArgumentException("連合艦隊結果はメタデータ検出だけに限定しています。", nameof(apiName));
        command.CommandText = apiName is null ? """
            SELECT Id, Name,
                   CASE WHEN length(Content) <= $maximumCompressedBytes THEN Content ELSE NULL END,
                   length(Content), TimeStamp, Version
            FROM ApiFiles
            WHERE Id > $afterId AND Id <= $throughId
              AND ApiFileType = $responseType
              AND Name IN ($port, $sortie, $practice, $mission)
            ORDER BY Id
            LIMIT $limit;
            """ : """
            SELECT Id, Name,
                   CASE WHEN length(Content) <= $maximumCompressedBytes THEN Content ELSE NULL END,
                   length(Content), TimeStamp, Version
            FROM ApiFiles
            WHERE Id > $afterId AND Id <= $throughId
              AND ApiFileType = $responseType AND Name = $apiName
              AND ($minimumTimestamp IS NULL OR TimeStamp >= $minimumTimestamp)
            ORDER BY Id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$afterId", afterId);
        command.Parameters.AddWithValue("$throughId", throughId);
        command.Parameters.AddWithValue("$responseType", ResponseType);
        command.Parameters.AddWithValue("$port", AllowedApiNames[0]);
        command.Parameters.AddWithValue("$sortie", AllowedApiNames[1]);
        command.Parameters.AddWithValue("$practice", AllowedApiNames[2]);
        command.Parameters.AddWithValue("$mission", AllowedApiNames[3]);
        if (apiName is not null) command.Parameters.AddWithValue("$apiName", apiName);
        command.Parameters.AddWithValue("$limit", maximumRows);
        command.Parameters.AddWithValue("$maximumCompressedBytes", _maximumCompressedBytes);
        if (apiName is not null)
            command.Parameters.AddWithValue("$minimumTimestamp", minimumTimestampUtc is null
                ? DBNull.Value
                : minimumTimestampUtc.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

        var responses = new List<ApiResponse>(maximumRows);
        var issues = new List<SqliteApiFileIssue>();
        var totalCompressedBytes = 0;
        var totalExpandedBytes = 0;
        long lastReturnedId = afterId;
        var rowsRead = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowsRead++;
            var id = reader.GetInt64(0);
            var name = reader.GetString(1);
            var sourceTimestamp = TryParseTimestamp(reader.IsDBNull(4) ? null : reader.GetString(4));
            if (reader.IsDBNull(3))
            {
                issues.Add(new SqliteApiFileIssue(id, "missing-content", sourceTimestamp));
                lastReturnedId = id;
                continue;
            }
            var compressedLength = reader.GetInt32(3);
            if (compressedLength > _maximumCompressedBytes)
            {
                issues.Add(new SqliteApiFileIssue(id, "compressed-size-limit", sourceTimestamp));
                lastReturnedId = id;
                continue;
            }
            totalCompressedBytes = checked(totalCompressedBytes + compressedLength);
            if (totalCompressedBytes > _maximumBatchBytes)
            {
                issues.Add(new SqliteApiFileIssue(id, "compressed-batch-limit", sourceTimestamp));
                lastReturnedId = id;
                continue;
            }
            if (reader.IsDBNull(2))
            {
                issues.Add(new SqliteApiFileIssue(id, "missing-content", sourceTimestamp));
                lastReturnedId = id;
                continue;
            }
            var compressed = (byte[])reader[2];

            try
            {
                var version = reader.GetInt32(5);
                if (version != SupportedApiFileVersion) throw new UnsupportedApiFileVersionException(version);
                var body = DecompressAndValidate(compressed);
                totalExpandedBytes = checked(totalExpandedBytes + body.Length);
                if (totalExpandedBytes > DefaultMaximumBatchBytes)
                {
                    issues.Add(new SqliteApiFileIssue(id, "expanded-batch-limit", sourceTimestamp));
                    lastReturnedId = id;
                    continue;
                }

                if (sourceTimestamp is not { } timestamp)
                    throw new InvalidDataException("API記録の日時形式を読み取れません。");

                var eventKey = Encoding.UTF8.GetBytes($"{_databasePath}\n{id.ToString(CultureInfo.InvariantCulture)}");
                var eventId = Convert.ToHexString(SHA256.HashData(eventKey));
                responses.Add(new ApiResponse("/kcsapi/" + name, timestamp, body, eventId, id));
            }
            catch (InvalidDataException)
            {
                issues.Add(new SqliteApiFileIssue(id, "invalid-payload", sourceTimestamp));
            }
            catch (UnsupportedApiFileVersionException)
            {
                issues.Add(new SqliteApiFileIssue(id, "unsupported-version", sourceTimestamp));
            }

            lastReturnedId = id;
        }

        // A short page proves there are no more allowlisted rows in this fixed window.
        var scannedThroughId = rowsRead < maximumRows ? throughId : lastReturnedId;
        return new SqliteApiFileBatch(responses, issues, scannedThroughId, totalCompressedBytes, totalExpandedBytes);
    }

    private SqliteApiMetadataBatch ReadMetadataBatch(long afterId, long throughId, int maximumRows,
        CancellationToken cancellationToken, string apiName)
    {
        if (afterId < 0) throw new ArgumentOutOfRangeException(nameof(afterId));
        if (throughId < 0) throw new ArgumentOutOfRangeException(nameof(throughId));
        if (maximumRows is < 1 or > DefaultMaximumRows) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        if (apiName != DeferredCombinedBattleApi)
            throw new ArgumentException("Metadata-only inspection is restricted to the deferred combined battle API.", nameof(apiName));
        cancellationToken.ThrowIfCancellationRequested();
        if (throughId <= afterId) return new SqliteApiMetadataBatch([], throughId);

        using var connection = OpenReadOnly();
        ValidateSchema(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id,Name,TimeStamp,Version
            FROM ApiFiles
            WHERE Id>$after AND Id<=$through AND ApiFileType=$responseType AND Name=$name
            ORDER BY Id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$after", afterId);
        command.Parameters.AddWithValue("$through", throughId);
        command.Parameters.AddWithValue("$responseType", ResponseType);
        command.Parameters.AddWithValue("$name", apiName);
        command.Parameters.AddWithValue("$limit", maximumRows);
        var rows = new List<SqliteApiRecordMetadata>(maximumRows);
        long lastId = afterId;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastId = reader.GetInt64(0);
            rows.Add(new SqliteApiRecordMetadata(lastId, reader.GetString(1),
                TryParseTimestamp(reader.IsDBNull(2) ? null : reader.GetString(2)), reader.GetInt32(3)));
        }
        var scannedThroughId = rows.Count < maximumRows ? throughId : lastId;
        return new SqliteApiMetadataBatch(rows, scannedThroughId);
    }

    private static DateTimeOffset? TryParseTimestamp(string? timestampText) =>
        DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)
            ? timestamp : null;

    private byte[] DecompressAndValidate(byte[] compressed)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(Math.Min(_maximumExpandedBytes, 16 * 1024));
        var buffer = new byte[8192];
        while (true)
        {
            var read = brotli.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length + read > _maximumExpandedBytes)
                throw new InvalidDataException("展開後のAPIデータが上限を超えました。");
            output.Write(buffer, 0, read);
        }

        var bytes = output.ToArray();
        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("APIデータが有効なUTF-8ではありません。", ex);
        }

        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaximumJsonDepth });
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("APIデータのJSONルートがオブジェクトではありません。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("APIデータのJSONを解析できません。", ex);
        }

        return bytes;
    }

    private SqliteConnection OpenReadOnly()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = _defaultTimeoutSeconds
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only = ON;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void ValidateSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM pragma_table_info('ApiFiles')
            WHERE (name = 'Id' AND upper(type) = 'INTEGER')
               OR (name = 'ApiFileType' AND upper(type) = 'INTEGER')
               OR (name = 'Name' AND upper(type) = 'TEXT')
               OR (name = 'Content' AND upper(type) = 'BLOB')
               OR (name = 'TimeStamp' AND upper(type) = 'TEXT')
               OR (name = 'Version' AND upper(type) = 'INTEGER');
            """;
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 6)
            throw new InvalidDataException("ApiFilesテーブルのスキーマが未対応です。");
    }
}

public sealed record SqliteApiFileBatch(
    IReadOnlyList<ApiResponse> Responses,
    IReadOnlyList<SqliteApiFileIssue> Issues,
    long ScannedThroughId,
    int CompressedBytes,
    int ExpandedBytes);

public sealed record SqliteApiRecordMetadata(long RowId, string Name, DateTimeOffset? TimestampUtc, int Version);

public sealed record SqliteApiMetadataBatch(IReadOnlyList<SqliteApiRecordMetadata> Records, long ScannedThroughId);

public sealed record SqliteApiFileIssue(long RowId, string Code, DateTimeOffset? TimestampUtc = null,
    string? MeasurementSeriesId = null);

public sealed record MeasurementSessionState(string SessionId, string GenerationId,
    DateTimeOffset StartedAtUtc, long StartAfterId);

public sealed class UnsupportedApiFileVersionException(int version)
    : InvalidOperationException($"未対応のApiFiles Versionです: {version}")
{
    public int Version { get; } = version;
}
