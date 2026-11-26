namespace SenkaSpeedMeasuringDevice.Core;

public static class SenkaCalculator
{
    public static ExperienceSample? FirstInHour(IEnumerable<ExperienceSample> records, DateTimeOffset hourStart)
    {
        var hourEnd = hourStart.AddHours(1);
        return records.Where(x => x.Timestamp >= hourStart && x.Timestamp < hourEnd)
            .OrderBy(x => x.Timestamp).FirstOrDefault();
    }

    public static double? Rate(ExperienceSample? start, ExperienceSample? end, bool senka)
    {
        if (start is null || end is null || end.Timestamp <= start.Timestamp ||
            end.Experience < start.Experience) return null;
        var seconds = (end.Timestamp - start.Timestamp).TotalSeconds;
        if (seconds <= 0) return null;
        var perHour = ((double)end.Experience - start.Experience) * 3600.0 / seconds;
        return senka ? perHour * 7.0 / 10000.0 : perHour;
    }

    public static string FormatThreeHours(IReadOnlyCollection<ExperienceSample> records,
        DateTimeOffset now, bool ship)
    {
        if (records.Count == 0) return "データなし";
        var current = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset);
        var previous = current.AddHours(-1);
        var twoBack = current.AddHours(-2);
        var ordered = records.OrderBy(x => x.Timestamp).ToArray();
        var r1 = FirstInHour(ordered, twoBack);
        var r2 = FirstInHour(ordered, previous);
        var r3 = FirstInHour(ordered, current);
        var latest = ordered[^1];
        string Value(double? value) => value is null ? "記録不足" :
            ship ? Math.Round(value.Value, MidpointRounding.AwayFromZero).ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-US"))
                 : value.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        string Line(DateTimeOffset from, DateTimeOffset to, ExperienceSample? a, ExperienceSample? b) =>
            $"・{from:HH\\:mm}~{to:HH\\:mm}{(ship ? " Exp時速 " : " 戦果時速 ")}{Value(Rate(a, b, !ship))}";
        return string.Join(Environment.NewLine,
            Line(twoBack, previous, r1, r2), Line(previous, current, r2, r3), Line(current, now, r3, latest));
    }
}

