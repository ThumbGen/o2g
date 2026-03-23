using Garmin.Connect;
using Garmin.Connect.Auth;
using Garmin.Connect.Models;
using Omron2Garmin.Web.Models;
using System.Globalization;

namespace Omron2Garmin.Web.Services;

/// <summary>
/// Service for interacting with Garmin Connect with rate-limit protection.
/// 
/// IMPORTANT: Garmin Connect has aggressive rate limiting on login attempts.
/// The OAuth flow makes multiple requests, so even a single login attempt
/// can trigger rate limiting if done too frequently. This service implements:
/// - Session reuse to avoid re-authenticating with same credentials
/// - Minimum intervals between login attempts
/// - Exponential backoff when rate limited
/// </summary>
public class GarminService : IDisposable
{
    private GarminConnectContext? _context;
    private GarminConnectClient? _client;
    private HttpClient? _httpClient;
    private string? _displayName;
    private string? _cachedEmail;
    private string? _cachedPasswordHash;
    private DateTime _lastSuccessfulLogin = DateTime.MinValue;
    private DateTime _lastLoginAttempt = DateTime.MinValue;
    private DateTime _rateLimitedUntil = DateTime.MinValue;
    private int _consecutiveFailures = 0;

    // Minimum time between login attempts to avoid rate limiting
    private static readonly TimeSpan MinLoginInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SessionValidityDuration = TimeSpan.FromHours(1);
    private const int MaxConsecutiveFailures = 3;

    public bool IsAuthenticated => _client != null && !string.IsNullOrEmpty(_displayName);
    public string? DisplayName => _displayName;
    public bool IsRateLimited => DateTime.UtcNow < _rateLimitedUntil;
    public TimeSpan? RateLimitRemainingTime => IsRateLimited ? _rateLimitedUntil - DateTime.UtcNow : null;

    // Event for MFA code request
    public event Func<Task<string?>>? OnMfaCodeRequired;

    /// <summary>
    /// Login to Garmin Connect with MFA support and rate-limit protection
    /// </summary>
    public async Task<(bool Success, string? Error)> LoginAsync(string email, string password)
    {
        // Check if we're currently rate-limited
        if (IsRateLimited)
        {
            var remaining = RateLimitRemainingTime!.Value;
            return (false, $"Rate-limited by Garmin Connect. Please wait {remaining.Minutes} minutes and {remaining.Seconds} seconds before trying again.");
        }

        // Check if we can reuse existing session (same credentials, session still valid)
        var passwordHash = ComputeSimpleHash(password);
        if (_client != null && _cachedEmail == email && _cachedPasswordHash == passwordHash)
        {
            // Check if session is likely still valid (within validity window)
            var sessionAge = DateTime.UtcNow - _lastSuccessfulLogin;
            if (sessionAge < SessionValidityDuration)
            {
                try
                {
                    // Verify existing session is still valid
                    var profile = await _client.GetSocialProfile();
                    if (profile != null)
                    {
                        _displayName = profile.DisplayName;
                        return (true, null);
                    }
                }
                catch
                {
                    // Session expired, need to re-authenticate
                    CleanupSession();
                }
            }
            else
            {
                // Session too old, clean up
                CleanupSession();
            }
        }

        // Enforce minimum interval between NEW login attempts (not session reuse)
        var timeSinceLastAttempt = DateTime.UtcNow - _lastLoginAttempt;
        if (timeSinceLastAttempt < MinLoginInterval)
        {
            var waitTime = MinLoginInterval - timeSinceLastAttempt;
            return (false, $"Please wait {waitTime.Seconds} seconds before attempting to login again. Garmin rate-limits frequent login attempts.");
        }

        _lastLoginAttempt = DateTime.UtcNow;

        try
        {
            var authParameters = new BasicAuthParameters(email, password);
            var mfaCodeProvider = new InteractiveMfaCodeProvider(this);

            // Reuse HttpClient to maintain connection pooling
            _httpClient ??= new HttpClient();

            _context = new GarminConnectContext(_httpClient, authParameters, mfaCodeProvider);
            _client = new GarminConnectClient(_context);

            // Verify authentication by getting user profile
            var profile = await _client.GetSocialProfile();
            _displayName = profile.DisplayName;

            // Cache credentials for session reuse
            _cachedEmail = email;
            _cachedPasswordHash = passwordHash;
            _consecutiveFailures = 0;
            _lastSuccessfulLogin = DateTime.UtcNow;

            return (true, null);
        }
        catch (Exception ex)
        {
            CleanupSession();
            _consecutiveFailures++;

            bool isRateLimited = ex.Message.Contains("Rate limited") ||
                                 ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                                 ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
                                 ex.Message.Contains("too many", StringComparison.OrdinalIgnoreCase) ||
                                 ex.Message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase);

            if (isRateLimited || _consecutiveFailures >= MaxConsecutiveFailures)
            {
                // Apply exponential backoff for rate limiting, starting from RateLimitCooldown
                var multiplier = Math.Pow(2, Math.Min(_consecutiveFailures - 1, 3));
                var cooldown = TimeSpan.FromTicks((long)(RateLimitCooldown.Ticks * multiplier));
                _rateLimitedUntil = DateTime.UtcNow + cooldown;
                return (false, $"Garmin Connect is rate-limiting login attempts. Please wait {cooldown.TotalMinutes:F0} minutes before trying again.");
            }

            if (ex.Message.Contains("401") || ex.Message.Contains("Unauthorized"))
            {
                return (false, "Invalid email or password.");
            }

            return (false, ex.Message);
        }
    }

    private void CleanupSession()
    {
        _context = null;
        _client = null;
        _displayName = null;
        // Don't dispose HttpClient - it can be reused
    }

    private static string ComputeSimpleHash(string input)
    {
        // Simple hash for credential comparison (not for security)
        var hash = 0;
        foreach (var c in input)
        {
            hash = ((hash << 5) - hash) + c;
        }
        return hash.ToString("X8");
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
        CleanupSession();
        _cachedEmail = null;
        _cachedPasswordHash = null;
        _consecutiveFailures = 0;
    }

    /// <summary>
    /// Reset rate limit state (use with caution, only after waiting the required time)
    /// </summary>
    public void ResetRateLimitState()
    {
        _rateLimitedUntil = DateTime.MinValue;
        _consecutiveFailures = 0;
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

        if (_client == null || _context == null)
        {
            result.Errors.Add("Not authenticated with Garmin Connect");
            return result;
        }

        // Step 1: Get unique dates from readings to upload
        var uniqueDates = readings
            .Select(r => DateOnly.FromDateTime(r.Timestamp))
            .Distinct()
            .ToList();

        // Step 2: Fetch existing readings from Garmin for each unique date
        var existingByDate = new Dictionary<DateOnly, List<(int Systolic, int Diastolic, int Pulse)>>();
        foreach (var date in uniqueDates)
        {
            try
            {
                var dailyReading = await _client.GetBloodPressureDaily(date.ToDateTime(TimeOnly.MinValue));
                var dailyValues = dailyReading?.BloodPressureMeasurements?.Select(r => (
                    Systolic: (int)r.Systolic,
                    Diastolic: (int)r.Diastolic,
                    Pulse: (int)r.Pulse
                )).ToList() ?? [];

                existingByDate[date] = dailyValues;
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

            var readingDate = DateOnly.FromDateTime(reading.Timestamp);

            // Get existing readings for this date
            var existingReadings = existingByDate.GetValueOrDefault(readingDate, []);

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

                // Use the Garmin.Connect library's AddBloodPressure method
                // The library will handle timestamp conversion internally
                var garminReading = new GarminBloodPressure
                {
                    Systolic = systolic,
                    Diastolic = diastolic,
                    Pulse = pulse,
                    MeasurementDateTime = reading.Timestamp, // Send timestamp as-is from CSV
                    Notes = reading.IrregularHeartbeat ? "Irregular heartbeat detected" : reading.Notes
                };

                var success = await ExecuteWithRetryAsync(
                    async () => await _client.AddBloodPressure(garminReading),
                    maxRetries: 3);

                if (success)
                {
                    result.Uploaded++;
                    result.UploadedReadings.Add(reading);

                    // Add to existing readings to prevent duplicates within the same batch
                    existingByDate[readingDate].Add((systolic, diastolic, pulse));
                }
                else
                {
                    result.Failed++;
                    result.Errors.Add($"Failed to upload reading at {reading.Timestamp:g}: Upload returned false");
                }

                // Adaptive delay to be respectful to the API
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                result.Failed++;

                if (IsRateLimitException(ex))
                {
                    result.Errors.Add($"Rate limited during upload. Stopping to prevent further issues.");
                    break; // Stop processing to avoid more rate limiting
                }

                result.Errors.Add($"Failed to upload reading at {reading.Timestamp:g}: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                {
                    result.Errors.Add($"  Inner: {ex.InnerException.Message}");
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Execute an operation with exponential backoff retry
    /// </summary>
    private static async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, int maxRetries = 3)
    {
        var delay = TimeSpan.FromSeconds(1);

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex) when (attempt < maxRetries && IsTransientException(ex))
            {
                await Task.Delay(delay);
                delay *= 2; // Exponential backoff
            }
        }

        return await operation(); // Final attempt, let exception propagate
    }

    private static bool IsTransientException(Exception ex)
    {
        return ex.Message.Contains("503") ||
               ex.Message.Contains("502") ||
               ex.Message.Contains("504") ||
               ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRateLimitException(Exception ex)
    {
        return ex.Message.Contains("429") ||
               ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("too many", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase);
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

        // Get existing weights to avoid duplicates
        var startDate = readings.Min(r => r.Timestamp).Date;
        var endDate = readings.Max(r => r.Timestamp).Date;

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

            var readingDate = DateOnly.FromDateTime(reading.Timestamp);

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
                // Convert timestamp to UTC properly using the source timezone
                var offset = sourceTimeZone.GetUtcOffset(reading.Timestamp);
                var dateTimeOffset = new DateTimeOffset(reading.Timestamp, offset);
                var timestampUtc = dateTimeOffset.UtcDateTime;

                // Format timestamps for Garmin API
                var timestampLocalStr = reading.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);
                var timestampUtcStr = timestampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

                // Garmin weight API expects kg as double (not grams!)
                var weightKg = reading.WeightKg;

                // Create the weight data payload matching Garmin's expected format
                var weightData = new
                {
                    dateTimestamp = timestampLocalStr,
                    gmtTimestamp = timestampUtcStr,
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
                else if ((int)response.StatusCode == 429 || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    result.Failed++;
                    result.Errors.Add($"Rate limited during weight upload. Stopping to prevent further issues.");
                    break;
                }
                else
                {
                    result.Failed++;
                    var responseBody = await response.Content.ReadAsStringAsync();
                    result.Errors.Add($"Failed to upload weight at {reading.Timestamp:g}: {response.StatusCode} - {responseBody}");
                }

                // Adaptive delay to be respectful to the API
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                result.Failed++;

                if (IsRateLimitException(ex))
                {
                    result.Errors.Add($"Rate limited during weight upload. Stopping to prevent further issues.");
                    break;
                }

                result.Errors.Add($"Failed to upload weight at {reading.Timestamp:g}: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                {
                    result.Errors.Add($"  Inner: {ex.InnerException.Message}");
                }
            }
        }

        return result;
    }

    public void Dispose()
    {
        CleanupSession();
        _httpClient?.Dispose();
        _httpClient = null;
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
