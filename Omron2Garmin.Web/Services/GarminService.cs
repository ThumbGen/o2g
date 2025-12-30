using Garmin.Connect;
using Garmin.Connect.Auth;
using Garmin.Connect.Models;
using Omron2Garmin.Web.Models;

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
        IProgress<(int Current, int Total, string Message)>? progress = null)
    {
        // TEMP: Limit to 2 readings for testing
        //readings = readings.Take(2).ToList();

        var result = new SyncResult
        {
            TotalReadings = readings.Count
        };

        if (_client == null)
        {
            result.Errors.Add("Not authenticated with Garmin Connect");
            return result;
        }

        // Step 1: Get unique dates from readings to upload
        var uniqueDates = readings
            .Select(r => r.Timestamp.Kind == DateTimeKind.Utc ? r.Timestamp.Date : r.Timestamp.ToUniversalTime().Date)
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

                // Log for debugging
                // result.Errors.Add($"DEBUG: Date {date:yyyy-MM-dd} has {dailyReadings.Count} readings in Garmin");
                // foreach (var e in dailyReadings)
                // {
                //     result.Errors.Add($"  Garmin: {e.Systolic}/{e.Diastolic} | P:{e.Pulse}");
                // }
            }
            catch (Exception ex)
            {
                existingByDate[date] = [];
                result.Errors.Add($"DEBUG: Error getting readings for {date:d}: {ex.Message}");
            }
        }

        // Step 3: Iterate readings and check for duplicates
        var current = 0;
        foreach (var reading in readings)
        {
            current++;
            progress?.Report((current, readings.Count, $"Processing {reading.Timestamp:g}..."));

            // Convert CSV reading timestamp to UTC
            var readingUtc = reading.Timestamp.Kind == DateTimeKind.Utc
                ? reading.Timestamp
                : reading.Timestamp.ToUniversalTime();
            var targetDate = readingUtc.Date;

            // Log what we're trying to upload
            result.Errors.Add($"  CSV ({targetDate:yyyy-MM-dd}): {reading.Systolic}/{reading.Diastolic} | P:{reading.Pulse}");

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
                result.Errors.Add($"    -> SKIPPED (found reading with same values)");
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

                var bloodPressure = new GarminBloodPressure
                {
                    Systolic = systolic,
                    Diastolic = diastolic,
                    Pulse = pulse,
                    MeasurementDateTime = reading.Timestamp,
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
