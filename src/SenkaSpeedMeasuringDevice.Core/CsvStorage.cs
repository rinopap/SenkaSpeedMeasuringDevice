using System.Globalization;
using System.Text;

namespace SenkaSpeedMeasuringDevice.Core;

public static class CsvStorage
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss";

    /// <summary>Reads the original CSV body verbatim after validating its header.</summary>
    public static IReadOnlyList<string>? ReadRawRows(string path, string valueColumn)
    {
        if (!File.Exists(path)) return [];
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        if (!string.Equals(reader.ReadLine(), $"timestamp,{valueColumn}", StringComparison.Ordinal)) return null;
        var rows = new List<string>();
        while (reader.ReadLine() is { } row) rows.Add(row);
        return rows;
    }

    public static IReadOnlyList<ExperienceSample> Load(string path, string valueColumn)
    {
        var result = new List<ExperienceSample>();
        if (!File.Exists(path)) return result;
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        if (!string.Equals(reader.ReadLine(), $"timestamp,{valueColumn}", StringComparison.Ordinal)) return result;
        while (reader.ReadLine() is { } line)
        {
            var comma = line.IndexOf(',');
            if (comma <= 0) continue;
            if (!DateTimeOffset.TryParseExact(line[..comma], TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var timestamp) ||
                !long.TryParse(line[(comma + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var exp))
                continue;
            result.Add(new ExperienceSample(timestamp, exp));
        }
        result.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return result;
    }

    public static void Append(string path, string valueColumn, ExperienceSample sample)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null) Directory.CreateDirectory(directory);
        var writeHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (writeHeader) writer.WriteLine($"timestamp,{valueColumn}");
        writer.Write(sample.Timestamp.LocalDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.WriteLine(sample.Experience.ToString(CultureInfo.InvariantCulture));
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Atomically replaces a CSV projection while preserving its legacy schema.</summary>
    public static void WriteAtomic(string path, string valueColumn, IEnumerable<ExperienceSample> samples)
        => WriteAtomicPreservingRows(path, valueColumn, [], samples);

    /// <summary>Rewrites a compatible CSV while retaining its pre-existing data rows verbatim.</summary>
    public static void WriteAtomicPreservingRows(string path, string valueColumn,
        IReadOnlyList<string> preservedRows, IEnumerable<ExperienceSample> appendedSamples)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.WriteLine($"timestamp,{valueColumn}");
                foreach (var line in preservedRows) writer.WriteLine(line);
                foreach (var sample in appendedSamples.OrderBy(x => x.Timestamp))
                {
                    writer.Write(sample.Timestamp.LocalDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));
                    writer.Write(',');
                    writer.WriteLine(sample.Experience.ToString(CultureInfo.InvariantCulture));
                }
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch (IOException) { }
        }
    }

    public static void WriteAtomicShipGain(string path, IEnumerable<ShipExpGainEvent> events)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.WriteLine("timestamp,source_id,event_id,kind,gained_exp,meaning");
                foreach (var item in events.OrderBy(x => x.ObservedAt).ThenBy(x => x.SourceRecordId))
                {
                    writer.Write(EscapeCsv(item.ObservedAt.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture)));
                    writer.Write(',');
                    writer.Write(EscapeCsv(item.SourceGenerationId));
                    writer.Write(',');
                    writer.Write(EscapeCsv(item.EventId));
                    writer.Write(',');
                    writer.Write(item.Kind.ToString());
                    writer.Write(',');
                    writer.Write(item.GainedExp.ToString(CultureInfo.InvariantCulture));
                    writer.Write(',');
                    writer.WriteLine(EscapeCsv(item.Meaning));
                }
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch (IOException) { }
        }
    }

    private static string EscapeCsv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : '"' + value.Replace("\"", "\"\"") + '"';
}
