using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using Omron2Garmin.Web.Models;

namespace Omron2Garmin.Web.Services;

/// <summary>
/// Service for parsing OMRON Connect CSV exports
/// </summary>
public class OmronCsvParser
{
    /// <summary>
    /// Parse blood pressure readings from OMRON Connect CSV export
    /// </summary>
    public List<BloodPressureReading> ParseCsv(Stream csvStream)
    {
        var readings = new List<BloodPressureReading>();

        using var reader = new StreamReader(csvStream);
        var content = reader.ReadToEnd();

        // Reset stream position for potential reuse
        if (csvStream.CanSeek)
        {
            csvStream.Position = 0;
        }

        // Try different parsing strategies based on CSV format
        readings = TryParseOmronFormat(content);

        return readings.OrderBy(r => r.Timestamp).ToList();
    }

    /// <summary>
    /// Try to parse OMRON Connect export format
    /// OMRON exports can have different formats depending on region/app version
    /// </summary>
    private List<BloodPressureReading> TryParseOmronFormat(string content)
    {
        var readings = new List<BloodPressureReading>();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length < 2)
            return readings;

        // Detect delimiter (could be comma, semicolon, or tab)
        var delimiter = DetectDelimiter(lines[0]);

        // Parse header to find column indices
        var header = lines[0].Split(delimiter);
        var columnMap = MapColumns(header);

        if (!columnMap.ContainsKey("systolic") || !columnMap.ContainsKey("diastolic"))
        {
            // Try alternative parsing for different OMRON formats
            return TryParseAlternativeFormat(content, delimiter);
        }

        // Parse data rows
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            try
            {
                var values = ParseCsvLine(line, delimiter);
                var reading = ParseReading(values, columnMap);
                if (reading != null)
                {
                    readings.Add(reading);
                }
            }
            catch
            {
                // Skip malformed lines
            }
        }

        return readings;
    }

    /// <summary>
    /// Alternative parsing for different OMRON export formats
    /// </summary>
    private List<BloodPressureReading> TryParseAlternativeFormat(string content, char delimiter)
    {
        var readings = new List<BloodPressureReading>();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Some OMRON exports have format: Date,Time,SYS,DIA,Pulse,...
        // or: Measurement Date,Measurement Time,SYS(mmHg),DIA(mmHg),Pulse(bpm),...
        foreach (var line in lines.Skip(1))
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrEmpty(trimmedLine))
                continue;

            var values = ParseCsvLine(trimmedLine, delimiter);

            // Try to find numeric values that look like blood pressure
            var reading = TryExtractReadingFromValues(values);
            if (reading != null)
            {
                readings.Add(reading);
            }
        }

        return readings;
    }

    /// <summary>
    /// Try to extract a blood pressure reading from CSV values
    /// </summary>
    private BloodPressureReading? TryExtractReadingFromValues(string[] values)
    {
        // Look for patterns: date/time followed by 3 numbers (sys, dia, pulse)
        DateTime? timestamp = null;
        int? systolic = null;
        int? diastolic = null;
        int? pulse = null;

        foreach (var value in values)
        {
            var trimmed = value.Trim().Trim('"');

            // Try to parse as date/time
            if (timestamp == null && TryParseDateTime(trimmed, out var dt))
            {
                timestamp = dt;
                continue;
            }

            // Try to parse as number
            if (int.TryParse(trimmed, out var num))
            {
                // Blood pressure values are typically in specific ranges
                if (systolic == null && num >= 60 && num <= 250)
                {
                    systolic = num;
                }
                else if (systolic != null && diastolic == null && num >= 30 && num <= 150)
                {
                    diastolic = num;
                }
                else if (systolic != null && diastolic != null && pulse == null && num >= 30 && num <= 200)
                {
                    pulse = num;
                }
            }
        }

        if (systolic.HasValue && diastolic.HasValue)
        {
            return new BloodPressureReading
            {
                Timestamp = timestamp ?? DateTime.Now,
                Systolic = systolic.Value,
                Diastolic = diastolic.Value,
                Pulse = pulse ?? 0,
                DeviceModel = "OMRON (CSV Import)"
            };
        }

        return null;
    }

    private char DetectDelimiter(string headerLine)
    {
        // Check for common delimiters
        if (headerLine.Contains(';'))
            return ';';
        if (headerLine.Contains('\t'))
            return '\t';
        return ',';
    }

    private Dictionary<string, int> MapColumns(string[] header)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < header.Length; i++)
        {
            var col = header[i].Trim().Trim('"').ToLowerInvariant();

            // Map common column names - check pulse FIRST since it may contain "bpm"
            // and we want to catch "Pulse (bpm)" before other checks
            if (col.Contains("pulse") || col.Contains("puls") || (col.Contains("bpm") && !col.Contains("dia") && !col.Contains("sys")))
            {
                if (!map.ContainsKey("pulse")) // Only set if not already mapped
                    map["pulse"] = i;
            }
            else if (col.Contains("sys") || col.Contains("systolic"))
                map["systolic"] = i;
            else if (col.Contains("dia") || col.Contains("diastolic"))
                map["diastolic"] = i;
            else if (col.Contains("heart") && !map.ContainsKey("pulse"))
                map["pulse"] = i;
            else if ((col.Contains("date") && !col.Contains("time")) || col == "data")
                map["date"] = i;
            else if ((col.Contains("time") && !col.Contains("date")) || col == "ora")
                map["time"] = i;
            else if (col.Contains("datetime") || col.Contains("timestamp") || col.Contains("measurement date"))
                map["datetime"] = i;
            else if (col.Contains("irregular") || col.Contains("arrhythmia") || col.Contains("neregulat"))
                map["irregular"] = i;
            else if (col.Contains("device") || col.Contains("model") || col.Contains("dispozitiv"))
                map["device"] = i;
            else if (col.Contains("note") || col.Contains("comment") || col.Contains("notă"))
                map["notes"] = i;
        }

        return map;
    }

    private BloodPressureReading? ParseReading(string[] values, Dictionary<string, int> columnMap)
    {
        try
        {
            // Parse systolic and diastolic (required)
            if (!TryGetInt(values, columnMap, "systolic", out var systolic) ||
                !TryGetInt(values, columnMap, "diastolic", out var diastolic))
            {
                return null;
            }

            // Parse timestamp
            DateTime timestamp = DateTime.Now;
            if (columnMap.ContainsKey("datetime"))
            {
                TryParseDateTime(GetValue(values, columnMap["datetime"]), out timestamp);
            }
            else if (columnMap.ContainsKey("date"))
            {
                var dateStr = GetValue(values, columnMap["date"]);
                var timeStr = columnMap.ContainsKey("time") ? GetValue(values, columnMap["time"]) : "00:00";
                TryParseDateTime($"{dateStr} {timeStr}", out timestamp);
            }

            // Parse optional fields
            TryGetInt(values, columnMap, "pulse", out var pulse);

            var irregular = false;
            if (columnMap.ContainsKey("irregular"))
            {
                var irregularValue = GetValue(values, columnMap["irregular"]).ToLowerInvariant();
                irregular = irregularValue == "yes" || irregularValue == "true" || irregularValue == "1" ||
                            irregularValue == "detected" || irregularValue.Contains("detect");
            }

            var device = columnMap.ContainsKey("device") ? GetValue(values, columnMap["device"]) : "OMRON (CSV Import)";
            var notes = columnMap.ContainsKey("notes") ? GetValue(values, columnMap["notes"]) : null;

            return new BloodPressureReading
            {
                Timestamp = timestamp,
                Systolic = systolic,
                Diastolic = diastolic,
                Pulse = pulse,
                IrregularHeartbeat = irregular,
                DeviceModel = device,
                Notes = notes
            };
        }
        catch
        {
            return null;
        }
    }

    private string[] ParseCsvLine(string line, char delimiter)
    {
        var values = new List<string>();
        var inQuotes = false;
        var current = "";

        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == delimiter && !inQuotes)
            {
                values.Add(current);
                current = "";
            }
            else
            {
                current += c;
            }
        }
        values.Add(current);

        return values.ToArray();
    }

    private string GetValue(string[] values, int index)
    {
        if (index < 0 || index >= values.Length)
            return "";
        return values[index].Trim().Trim('"');
    }

    private bool TryGetInt(string[] values, Dictionary<string, int> map, string key, out int result)
    {
        result = 0;
        if (!map.ContainsKey(key))
            return false;

        var value = GetValue(values, map[key]);

        // Handle empty or whitespace values
        if (string.IsNullOrWhiteSpace(value))
            return false;

        // Try parsing, also handle potential decimal values (e.g., "72.0")
        if (int.TryParse(value, out result))
            return true;

        // Try parsing as decimal and convert to int
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var doubleResult))
        {
            result = (int)Math.Round(doubleResult);
            return true;
        }

        return false;
    }

    private bool TryParseDateTime(string value, out DateTime result)
    {
        result = DateTime.Now;

        // Try various date/time formats
        var formats = new[]
        {
            // OMRON Connect format (e.g., "22 Dec 2025 23:21")
            "d MMM yyyy H:mm",
            "d MMM yyyy HH:mm",
            "dd MMM yyyy H:mm",
            "dd MMM yyyy HH:mm",
            "d MMM yyyy",
            "dd MMM yyyy",
            // Standard formats
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "yyyy/MM/dd HH:mm:ss",
            "yyyy/MM/dd HH:mm",
            "dd-MM-yyyy HH:mm:ss",
            "dd-MM-yyyy HH:mm",
            "dd/MM/yyyy HH:mm:ss",
            "dd/MM/yyyy HH:mm",
            "MM/dd/yyyy HH:mm:ss",
            "MM/dd/yyyy HH:mm",
            "dd.MM.yyyy HH:mm:ss",
            "dd.MM.yyyy HH:mm",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm:ssZ",
            "yyyy-MM-dd",
            "dd-MM-yyyy",
            "dd/MM/yyyy",
            "MM/dd/yyyy"
        };

        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
            {
                return true;
            }
        }

        // Try with current culture (may help with localized month names)
        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(value, format, CultureInfo.CurrentCulture, DateTimeStyles.None, out result))
            {
                return true;
            }
        }

        // Try general parse as fallback
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out result) ||
               DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out result);
    }
}
