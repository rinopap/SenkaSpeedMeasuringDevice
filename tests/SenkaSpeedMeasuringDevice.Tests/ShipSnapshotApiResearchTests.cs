using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SenkaSpeedMeasuringDevice.Capture;

namespace SenkaSpeedMeasuringDevice.Tests;

/// <summary>
/// Optional, test-only inspection of two 74EN response shapes. Never included in the app,
/// and never prints ship identities or API values.
/// </summary>
public sealed class ShipSnapshotApiResearchTests
{
    private const int ResponseType = 1;
    private const int MaximumCompressedBytes = 2 * 1024 * 1024;
    private const int MaximumExpandedBytes = 8 * 1024 * 1024;
    private static readonly string[] Names = ["api_get_member/ship2", "api_get_member/ship_deck"];

    [Fact]
    public void ConfiguredDatabaseReportsBoundedShipSnapshotEndpointCoverage()
    {
        var path = Environment.GetEnvironmentVariable("SENKA_EO_SQLITE_TEST_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        Assert.True(File.Exists(path));
        var original = new FileInfo(path);
        var originalLength = original.Length;
        var originalWrite = original.LastWriteTimeUtc;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString());
        connection.Open();
        using (var readOnly = connection.CreateCommand())
        {
            readOnly.CommandText = "PRAGMA query_only=ON;";
            readOnly.ExecuteNonQuery();
        }

        long highWater;
        using (var high = connection.CreateCommand())
        {
            high.CommandText = "SELECT COALESCE(MAX(Id),0) FROM ApiFiles;";
            highWater = Convert.ToInt64(high.ExecuteScalar());
        }
        var startId = Math.Max(0, highWater - SqliteApiFileReader.MaximumInitialHistoryIdWindow);
        foreach (var name in Names)
        {
            long rows;
            string? firstTime;
            string? lastTime;
            using (var count = connection.CreateCommand())
            {
                count.CommandText = """
                    SELECT COUNT(*),MIN(TimeStamp),MAX(TimeStamp) FROM ApiFiles
                    WHERE Id>$start AND Id<=$end AND ApiFileType=$type AND Name=$name;
                    """;
                count.Parameters.AddWithValue("$start", startId);
                count.Parameters.AddWithValue("$end", highWater);
                count.Parameters.AddWithValue("$type", ResponseType);
                count.Parameters.AddWithValue("$name", name);
                using var reader = count.ExecuteReader();
                Assert.True(reader.Read());
                rows = reader.GetInt64(0);
                firstTime = reader.IsDBNull(1) ? null : reader.GetString(1);
                lastTime = reader.IsDBNull(2) ? null : reader.GetString(2);
            }

            var summary = "no-sample";
            using (var sample = connection.CreateCommand())
            {
                sample.CommandText = """
                    SELECT Content,length(Content),Version FROM ApiFiles
                    WHERE Id>$start AND Id<=$end AND ApiFileType=$type AND Name=$name
                    ORDER BY Id DESC LIMIT 1;
                    """;
                sample.Parameters.AddWithValue("$start", startId);
                sample.Parameters.AddWithValue("$end", highWater);
                sample.Parameters.AddWithValue("$type", ResponseType);
                sample.Parameters.AddWithValue("$name", name);
                using var reader = sample.ExecuteReader();
                if (reader.Read())
                {
                    var compressedLength = reader.GetInt32(1);
                    var version = reader.GetInt32(2);
                    if (reader.IsDBNull(0)) summary = $"content-null,version={version}";
                    else if (compressedLength > MaximumCompressedBytes) summary = $"compressed-limit,version={version}";
                    else
                    {
                        var compressed = (byte[])reader[0];
                        var json = DecompressBounded(compressed);
                        summary = InspectShape(name, json, version);
                    }
                }
            }
            // Timestamps, endpoint names, row counts, schema/version and shape counts only.
            // No record IDs, ship IDs, account fields, or experience values are written.
            Console.WriteLine($"snapshot-endpoint={name};response-rows={rows};time-range={firstTime ?? "none"}..{lastTime ?? "none"};sample={summary}");
        }

        var after = new FileInfo(path);
        Assert.Equal(originalLength, after.Length);
        Assert.Equal(originalWrite, after.LastWriteTimeUtc);
    }

    private static string InspectShape(string name, byte[] json, int version)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        if (!root.TryGetProperty("api_result", out var result) || result.ValueKind != JsonValueKind.Number || result.GetInt32() != 1)
            return $"api-result-invalid,version={version}";
        if (!root.TryGetProperty("api_data", out var data)) return $"api-data-missing,version={version}";
        JsonElement ships;
        if (name == "api_get_member/ship2")
        {
            ships = data;
        }
        else if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("api_ship_data", out ships))
            return $"ship-array-missing,version={version}";
        if (ships.ValueKind != JsonValueKind.Array) return $"ship-array-nonarray,version={version}";

        var validExp0 = 0;
        var invalidExp0 = 0;
        foreach (var ship in ships.EnumerateArray())
        {
            if (ship.ValueKind == JsonValueKind.Object && ship.TryGetProperty("api_exp", out var exp) &&
                exp.ValueKind == JsonValueKind.Array && exp.GetArrayLength() > 0 &&
                exp[0].ValueKind == JsonValueKind.Number && exp[0].TryGetInt32(out var total) && total >= 0)
                validExp0++;
            else invalidExp0++;
        }
        return $"api-exp0-present={validExp0};api-exp0-missing-or-invalid={invalidExp0};version={version}";
    }

    private static byte[] DecompressBounded(byte[] compressed)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = brotli.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length + read > MaximumExpandedBytes) throw new InvalidDataException("Expanded research sample exceeds limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
