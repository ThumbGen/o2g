using Garmin.Connect;
using Garmin.Connect.Auth;
using Garmin.Connect.Models;
using Omron2Garmin.Web.Models;
using System.Globalization;

namespace Omron2Garmin.Web.Services;

/// <summary>
/// Service for interacting with Garmin Connect
/// </summary>
public class GarminService : IDisposable
{
    private GarminConnectContext? _context;
    private GarminConnectClient? _client;
    private string? _displayName;

    public bool IsAuthenticated => _client != null && !string.IsNullOrEmpty(_displayName);
    public string? DisplayName => _displayName;

    // Event for MFA code request
    public event Func<Task<string?>>? OnMfaCodeRequired;

    /// <summary>
    /// Login to Garmin Connect with MFA support
    /// </summary>
    public async Task<(bool Success, string? Error)> LoginAsync(string email, string password)
    {
        try
        {
            var authParameters = new BasicAuthParameters(email, password);
            var mfaCodeProvider = new InteractiveMfaCodeProvider(this);

            _context = new GarminConnectContext(new HttpClient(), authParameters, mfaCodeProvider);
            _client = new GarminConnectClient(_context);

            // Verify authentication by getting user profile
            var profile = await _client.GetSocialProfile();
            _displayName = profile.DisplayName;

            return (true, null);
        }
        catch (Exception ex)
        {
            _context = null;
            _client = null;
            _displayName = null;

            var errorMessage = ex.Message;
            if (ex.Message.Contains("401") || ex.Message.Contains("Unauthorized"))
            {
                errorMessage = "Invalid email or password";
            }

            return (false, errorMessage);
        }
    }

    /// <summary>
    /// Internal method to request MFA code from UI
    /// </summary>
    internal async Task<string?> RequestMfaCodeAsync()
    {
        if (OnMfaCodeRequired != null)
        {
            return await OnMfaCodeRequired.Invoke();
        }
        return null;
    }

    /// <summary>
    /// Logout from Garmin Connect
    /// </summary>
    public void Logout()
    {
        _context = null;
        _client = null;
        _displayName = null;
    }

    /// <summary>
    /// Upload blood pressure readings to Garmin Connect
    /// </summary>
    public async Task<SyncResult> UploadReadingsAsync(
        List<BloodPressureReading> readings,
        TimeZoneInfo sourceTimeZone,
        IProgress<(int Current, int Total, string Message)>? progress = null)
    {
        var result = new SyncResult
        {
            TotalReadings = readings.Count
        };

        if (_client == null)
        {
            result.Errors.Add("Not authenticated with Garmin Connect");
            return result;
        }

        // Step 1: Get unique dates from readings to upload (convert to UTC first)
        var uniqueDates = readings
            .Select(r => ConvertToUtc(r.Timestamp, sourceTimeZone).Date)
            .Distinct()
            .ToList();

        // Step 2: Fetch existing readings from Garmin for each unique date
        var existingByDate = new Dictionary<DateTime, List<BloodPressureReading>>();
        foreach (var date in uniqueDates)
        {
            try
            {
                var dailyReading = await _client.GetBloodPressureDaily(date);
                var dailyReadings = dailyReading?.BloodPressureMeasurements?.Select(r => new BloodPressureReading
                {
                    Timestamp = r.MeasurementTimestampGmt,
                    Systolic = (int)r.Systolic,
                    Diastolic = (int)r.Diastolic,
                    Pulse = (int)r.Pulse
                }).ToList() ?? [];

                existingByDate[date] = dailyReadings;
            }
            catch (Exception)
            {
                existingByDate[date] = [];
            }
        }

        // Step 3: Iterate readings and check for duplicates
        var current = 0;
        foreach (var reading in readings)
        {
            current++;
            progress?.Report((current, readings.Count, $"Processing {reading.Timestamp:g}..."));

            // Convert CSV reading timestamp to UTC using provided timezone
            var readingUtc = ConvertToUtc(reading.Timestamp, sourceTimeZone);
            var targetDate = readingUtc.Date;

            // Get existing readings for this date
            var existingReadings = existingByDate.GetValueOrDefault(targetDate, []);

            // Check for duplicates - same VALUES (systolic, diastolic, pulse)
            var isDuplicate = existingReadings.Any(e =>
                e.Systolic == reading.Systolic &&
                e.Diastolic == reading.Diastolic &&
                e.Pulse == reading.Pulse);

            if (isDuplicate)
            {
                result.Skipped++;
                continue;
            }

            try
            {
                // Garmin validation requires:
                // - Systolic: 40-300
                // - Diastolic: 30-200
                // - Pulse: 1-300 (cannot be 0)
                var pulse = reading.Pulse;
                if (pulse < 1) pulse = 60; // Default to 60 if missing/invalid
                if (pulse > 300) pulse = 300;

                var systolic = Math.Clamp(reading.Systolic, 40, 300);
                var diastolic = Math.Clamp(reading.Diastolic, 30, 200);

                // Garmin timezone handling:
                // The library calls ToUniversalTime() which subtracts the server's timezone offset.
                // To compensate, we add the source timezone offset to the original CSV time.
                // Example: CSV=9:34 in GMT+1, we add +1h → send 10:34 Unspecified
                //          Library does: 10:34 - 1h (server GMT+1) = 9:34 UTC
                //          Garmin stores 9:34 UTC and displays it correctly
                var offset = sourceTimeZone.GetUtcOffset(reading.Timestamp);
                var measurementDateTime = reading.Timestamp.Add(offset);
                measurementDateTime = DateTime.SpecifyKind(measurementDateTime, DateTimeKind.Unspecified);

                var bloodPressure = new GarminBloodPressure
                {
                    Systolic = systolic,
                    Diastolic = diastolic,
                    Pulse = pulse,
                    MeasurementDateTime = measurementDateTime,
                    Notes = reading.IrregularHeartbeat ? "Irregular heartbeat detected" : reading.Notes
                };

                var success = await _client.AddBloodPressure(bloodPressure);

                if (success)
                {
                    result.Uploaded++;
                    result.UploadedReadings.Add(reading);

                    // Add to existing readings to prevent duplicates within the same batch
                    existingByDate[targetDate].Add(reading);
                }
                else
                {
                    result.Failed++;
                    result.Errors.Add($"Failed to upload reading at {reading.Timestamp:g}: Validation failed");
                }

                // Small delay to be respectful to the API
                await Task.Delay(300);
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"Failed to upload reading at {reading.Timestamp:g}: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Upload weight readings to Garmin Connect
    /// </summary>
    public async Task<WeightSyncResult> UploadWeightReadingsAsync(
        List<WeightReading> readings,
        TimeZoneInfo sourceTimeZone,
        IProgress<(int Current, int Total, string Message)>? progress = null)
    {
        var result = new WeightSyncResult
        {
            TotalReadings = readings.Count
        };

        if (_client == null || _context == null)
        {
            result.Errors.Add("Not authenticated with Garmin Connect");
            return result;
        }

        // Get existing weights to avoid duplicates (convert to UTC first)
        var startDate = readings.Min(r => ConvertToUtc(r.Timestamp, sourceTimeZone)).Date;
        var endDate = readings.Max(r => ConvertToUtc(r.Timestamp, sourceTimeZone)).Date;

        List<(DateOnly Date, double Weight)> existingWeights = [];
        try
        {
            var existingRange = await _client.GetWeightRange(startDate, endDate);
            if (existingRange?.DailyWeightSummaries != null)
            {
                existingWeights = existingRange.DailyWeightSummaries
                    .Where(w => w.LatestWeight?.Weight != null)
                    .Select(w => (w.SummaryDate, (double)w.LatestWeight!.Weight / 1000.0)) // Convert grams to kg
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Warning: Could not fetch existing weights: {ex.Message}");
        }

        var current = 0;
        foreach (var reading in readings)
        {
            current++;
            progress?.Report((current, readings.Count, $"Processing weight {reading.WeightKg:F1} kg from {reading.Timestamp:g}..."));

            // Skip invalid weight values
            if (reading.WeightKg <= 0 || reading.WeightKg < 30 || reading.WeightKg > 500)
            {
                result.Skipped++;
                result.Errors.Add($"Skipped invalid weight: {reading.WeightKg:F1} kg at {reading.Timestamp:g}");
                continue;
            }

            // Convert to UTC using provided timezone
            var readingUtc = ConvertToUtc(reading.Timestamp, sourceTimeZone);
            var readingDate = DateOnly.FromDateTime(readingUtc);

            // Check for duplicate (same date and very similar weight)
            var isDuplicate = existingWeights.Any(e =>
                e.Date == readingDate &&
                Math.Abs(e.Weight - reading.WeightKg) < 0.05);

            if (isDuplicate)
            {
                result.Skipped++;
                continue;
            }

            try
            {
                // For weight API, we send the timestamp directly as string
                // The API expects GMT timestamp, so send the UTC time directly
                var timestampStr = readingUtc.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

                // Garmin weight API expects kg as double (not grams!)
                var weightKg = reading.WeightKg;

                // Create the weight data payload matching Garmin's expected format
                var weightData = new
                {
                    dateTimestamp = timestampStr,
                    gmtTimestamp = timestampStr,
                    unitKey = "kg",
                    value = weightKg
                };

                // Use the internal context to make the POST request
                var response = await _context.MakeHttpPost(
                    "/weight-service/user-weight",
                    weightData);

                if (response.IsSuccessStatusCode)
                {
                    result.Uploaded++;
                    result.UploadedReadings.Add(reading);
                    existingWeights.Add((readingDate, reading.WeightKg));
                }
                else
                {
                    result.Failed++;
                    var responseBody = await response.Content.ReadAsStringAsync();
                    result.Errors.Add($"Failed to upload weight at {reading.Timestamp:g}: {response.StatusCode} - {responseBody}");
                }

                await Task.Delay(300);
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"Failed to upload weight at {reading.Timestamp:g}: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                {
                    result.Errors.Add($"  Inner: {ex.InnerException.Message}");
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Convert a DateTime from the source timezone to UTC
    /// </summary>
    private static DateTime ConvertToUtc(DateTime dateTime, TimeZoneInfo sourceTimeZone)
    {
        // If already UTC, return as is
        if (dateTime.Kind == DateTimeKind.Utc)
            return dateTime;

        // If Local, convert using system timezone (though we should use source timezone)
        if (dateTime.Kind == DateTimeKind.Local)
        {
            // Treat as if it's in the source timezone
            dateTime = DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified);
        }

        // Convert from source timezone to UTC
        return TimeZoneInfo.ConvertTimeToUtc(dateTime, sourceTimeZone);
    }

    public void Dispose()
    {
        _context = null;
        _client = null;
    }
}

/// <summary>
/// MFA code provider that requests code from UI
/// </summary>
internal class InteractiveMfaCodeProvider : IMfaCodeProvider
{
    private readonly GarminService _service;

    public InteractiveMfaCodeProvider(GarminService service)
    {
        _service = service;
    }

    public async Task<string> GetMfaCodeAsync()
    {
        var code = await _service.RequestMfaCodeAsync();
        return code ?? throw new InvalidOperationException("MFA code was not provided");
    }
}
