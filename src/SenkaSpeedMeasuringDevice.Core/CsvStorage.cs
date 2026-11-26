using System.Globalization;
using System.Text;

namespace SenkaSpeedMeasuringDevice.Core;

public static class CsvStorage
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss";

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
}

