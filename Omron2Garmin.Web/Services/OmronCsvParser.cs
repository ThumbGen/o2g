using Omron2Garmin.Web.Models;
using System.Globalization;

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
        using var reader = new StreamReader(csvStream);
        var content = reader.ReadToEnd();

        // Reset stream position for potential reuse
        if (csvStream.CanSeek)
        {
            csvStream.Position = 0;
        }

        // Try different parsing strategies based on CSV format
        var readings = TryParseOmronFormat(content);

        return readings.OrderBy(r => r.Timestamp).ToList();
    }

    /// <summary>
    /// Parse all data types from OMRON Connect CSV export (Blood Pressure, Weight, etc.)
    /// </summary>
    public OmronParseResult ParseAllData(Stream csvStream)
    {
        using var reader = new StreamReader(csvStream);
        var content = reader.ReadToEnd();

        // Reset stream position for potential reuse
        if (csvStream.CanSeek)
        {
            csvStream.Position = 0;
        }

        var result = new OmronParseResult();

        // Extract Blood Pressure section
        var bpSection = ExtractBloodPressureSection(content);
        if (!string.IsNullOrEmpty(bpSection))
        {
            result.BloodPressureReadings = TryParseSingleSectionFormat(bpSection)
                .OrderBy(r => r.Timestamp).ToList();
        }

        // Extract Weight section
        var weightSection = ExtractWeightSection(content);
        if (!string.IsNullOrEmpty(weightSection))
        {
            result.WeightReadings = ParseWeightSection(weightSection)
                .OrderBy(r => r.Timestamp).ToList();
        }

        return result;
    }

    /// <summary>
    /// Try to parse OMRON Connect export format
    /// OMRON exports can have different formats depending on region/app version
    /// OMRON CSV files contain multiple vertical sections (BP, Steps, Weight, Oxygen) separated by new headers
    /// </summary>
    private List<BloodPressureReading> TryParseOmronFormat(string content)
    {
        // OMRON CSV has multiple vertical sections - we need to extract only the Blood Pressure section
        var bloodPressureSection = ExtractBloodPressureSection(content);

        if (string.IsNullOrEmpty(bloodPressureSection))
        {
            // Fallback to original behavior if no sections detected
            return TryParseSingleSectionFormat(content);
        }

        return TryParseSingleSectionFormat(bloodPressureSection);
    }

    /// <summary>
    /// Extract only the Blood Pressure section from OMRON multi-section CSV
    /// Sections are separated by empty lines and each starts with a header row
    /// Known sections: Blood Pressure, Steps (Total steps), Weight, Oxygen Level
    /// </summary>
    private static string ExtractBloodPressureSection(string content)
    {
        var lines = content.Split('\n');
        var sectionLines = new List<string>();
        var inBloodPressureSection = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Check if this is a header line (contains column names)
            if (IsHeaderLine(trimmed))
            {
                // Check if this is the blood pressure section header
                if (IsBloodPressureHeader(trimmed))
                {
                    inBloodPressureSection = true;
                    sectionLines.Clear(); // Reset in case we had false positives
                    sectionLines.Add(line);
                }
                else
                {
                    // We hit a different section's header, stop collecting
                    if (inBloodPressureSection && sectionLines.Count > 1)
                    {
                        break;
                    }
                    inBloodPressureSection = false;
                }
                continue;
            }

            // If we're in the BP section, collect data lines
            if (inBloodPressureSection)
            {
                // Empty line might indicate section end
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    // If we already have data, empty line means section end
                    if (sectionLines.Count > 1)
                    {
                        break;
                    }
                    continue;
                }

                sectionLines.Add(line);
            }
        }

        return sectionLines.Count > 1 ? string.Join('\n', sectionLines) : string.Empty;
    }

    /// <summary>
    /// Check if a line appears to be a header row
    /// </summary>
    private static bool IsHeaderLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var lower = line.ToLowerInvariant();

        // Header lines typically contain column name keywords
        var headerKeywords = new[] { "date", "time", "systolic", "diastolic", "pulse", "weight", "steps",
                                      "oxygen", "bmi", "distance", "calories", "mmhg", "bpm", "kg", "%" };

        var keywordCount = headerKeywords.Count(k => lower.Contains(k));

        // If it contains multiple header-like keywords, it's likely a header
        return keywordCount >= 2;
    }

    /// <summary>
    /// Check if this header is for the Blood Pressure section
    /// </summary>
    private static bool IsBloodPressureHeader(string header)
    {
        var lower = header.ToLowerInvariant();

        // Blood pressure section must have systolic/diastolic columns
        return (lower.Contains("systolic") || lower.Contains("sys")) &&
               (lower.Contains("diastolic") || lower.Contains("dia"));
    }

    /// <summary>
    /// Check if this header is for the Weight section
    /// </summary>
    private static bool IsWeightHeader(string header)
    {
        var lower = header.ToLowerInvariant();

        // Weight section has "Weight (kg)" and "BMI" columns
        return lower.Contains("weight") && lower.Contains("bmi");
    }

    /// <summary>
    /// Extract Weight section from OMRON multi-section CSV
    /// </summary>
    private static string ExtractWeightSection(string content)
    {
        var lines = content.Split('\n');
        var sectionLines = new List<string>();
        var inWeightSection = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Check if this is a header line
            if (IsHeaderLine(trimmed))
            {
                if (IsWeightHeader(trimmed))
                {
                    inWeightSection = true;
                    sectionLines.Clear();
                    sectionLines.Add(line);
                }
                else
                {
                    // We hit a different section's header
                    if (inWeightSection && sectionLines.Count > 1)
                    {
                        break;
                    }
                    inWeightSection = false;
                }
                continue;
            }

            if (inWeightSection)
            {
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    if (sectionLines.Count > 1)
                    {
                        break;
                    }
                    continue;
                }

                sectionLines.Add(line);
            }
        }

        return sectionLines.Count > 1 ? string.Join('\n', sectionLines) : string.Empty;
    }

    /// <summary>
    /// Parse weight readings from extracted weight section
    /// </summary>
    private List<WeightReading> ParseWeightSection(string content)
    {
        var readings = new List<WeightReading>();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length < 2)
            return readings;

        var delimiter = DetectDelimiter(lines[0]);
        var header = lines[0].Split(delimiter);
        var columnMap = MapWeightColumns(header);

        if (!columnMap.ContainsKey("weight"))
            return readings;

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            try
            {
                var values = ParseCsvLine(line, delimiter);
                var reading = ParseWeightReading(values, columnMap);
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
    /// Map weight section column names to indices
    /// </summary>
    private static Dictionary<string, int> MapWeightColumns(string[] header)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < header.Length; i++)
        {
            var col = header[i].Trim().Trim('"').ToLowerInvariant();

            if (col.Contains("weight") && col.Contains("kg"))
                map["weight"] = i;
            else if (col.Contains("bmi") && !col.Contains("category"))
                map["bmi"] = i;
            else if (col.Contains("category"))
                map["category"] = i;
            else if (col.Contains("skeletal") || col.Contains("muscle"))
                map["muscle"] = i;
            else if (col.Contains("body fat") || col.Contains("fat %"))
                map["bodyfat"] = i;
            else if (col.Contains("visceral"))
                map["visceral"] = i;
            else if ((col.Contains("date") && !col.Contains("time")) || col == "data")
                map["date"] = i;
            else if ((col.Contains("time") && !col.Contains("date")) || col == "ora")
                map["time"] = i;
        }

        return map;
    }

    /// <summary>
    /// Parse a single weight reading from CSV values
    /// </summary>
    private WeightReading? ParseWeightReading(string[] values, Dictionary<string, int> columnMap)
    {
        try
        {
            // Parse weight (required) - must be positive and within reasonable range (30-500 kg)
            if (!TryGetDouble(values, columnMap, "weight", out var weight) || 
                weight <= 0 || 
                weight < 30 || 
                weight > 500)
            {
                return null;
            }

            // Parse timestamp
            DateTime timestamp = DateTime.Now;
            if (columnMap.ContainsKey("date"))
            {
                var dateStr = GetValue(values, columnMap["date"]);
                var timeStr = columnMap.TryGetValue("time", out int value1) ? GetValue(values, value1) : "00:00";
                TryParseDateTime($"{dateStr} {timeStr}", out timestamp);
            }

            // Parse optional fields
            TryGetDouble(values, columnMap, "bmi", out var bmi);
            TryGetDouble(values, columnMap, "muscle", out var muscle);
            TryGetDouble(values, columnMap, "bodyfat", out var bodyFat);
            TryGetInt(values, columnMap, "visceral", out var visceral);

            var category = columnMap.TryGetValue("category", out int value) ? GetValue(values, value) : null;
            if (category == "-") category = null;

            return new WeightReading
            {
                Timestamp = timestamp,
                WeightKg = weight,
                Bmi = bmi > 0 ? bmi : null,
                Category = category,
                SkeletalMusclePercent = muscle > 0 ? muscle : null,
                BodyFatPercent = bodyFat > 0 ? bodyFat : null,
                VisceralFatLevel = visceral > 0 ? visceral : null,
                DeviceModel = "OMRON (CSV Import)"
            };
        }
        catch
        {
            return null;
        }
    }

    private bool TryGetDouble(string[] values, Dictionary<string, int> map, string key, out double result)
    {
        result = 0;
        if (!map.ContainsKey(key))
            return false;

        var value = GetValue(values, map[key]);
        if (string.IsNullOrWhiteSpace(value) || value == "-")
            return false;

        // Try parsing with invariant culture (accepts dot as decimal separator)
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out result))
            return true;

        // Try parsing with current culture (may accept comma as decimal separator)
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out result))
            return true;

        // Try replacing comma with dot and parse again
        var normalizedValue = value.Replace(',', '.');
        return double.TryParse(normalizedValue, NumberStyles.Any, CultureInfo.InvariantCulture, out result);
    }

    /// <summary>
    /// Parse a single section (original logic, extracted for reuse)
    /// </summary>
    private List<BloodPressureReading> TryParseSingleSectionFormat(string content)
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

    private static char DetectDelimiter(string headerLine)
    {
        // Check for common delimiters
        if (headerLine.Contains(';'))
            return ';';
        if (headerLine.Contains('\t'))
            return '\t';
        return ',';
    }

    private static Dictionary<string, int> MapColumns(string[] header)
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

    private static BloodPressureReading? ParseReading(string[] values, Dictionary<string, int> columnMap)
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

    private static string[] ParseCsvLine(string line, char delimiter)
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

    private static string GetValue(string[] values, int index)
    {
        if (index < 0 || index >= values.Length)
            return "";
        return values[index].Trim().Trim('"');
    }

    private static bool TryGetInt(string[] values, Dictionary<string, int> map, string key, out int result)
    {
        result = 0;
        if (!map.TryGetValue(key, out int value1))
            return false;

        var value = GetValue(values, value1);

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

    private static bool TryParseDateTime(string value, out DateTime result)
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
