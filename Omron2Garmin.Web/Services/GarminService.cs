using Garmin.Connect;
using Garmin.Connect.Auth;
using Garmin.Connect.Models;
using Omron2Garmin.Web.Models;
using System.Globalization;

namespace Omron2Garmin.Web.Services;

/// <summary>
/// Service for interacting with Garmin Connect using the Unofficial.Garmin.Connect library.
/// 
/// Authentication is based on the library's recommended approach:
/// - BasicAuthParameters for credentials
/// - IMfaCodeProvider for MFA support
/// - ITokenCache for OAuth2 token persistence across restarts
/// 
/// The library handles OAuth2 token refresh automatically.
/// </summary>
public class GarminService : IDisposable
{
    private readonly GarminSessionStore _sessionStore;
    private GarminConnectContext? _context;
    private GarminConnectClient? _client;
    private string? _displayName;
    private string? _currentUserKey;
    private DateTime _lastLoginAttempt = DateTime.MinValue;
    private DateTime _rateLimitedUntil = DateTime.MinValue;
    private int _consecutiveFailures = 0;

    // Minimum time between login attempts to avoid rate limiting
    private static readonly TimeSpan MinLoginInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(15);
    private const int MaxConsecutiveFailures = 3;

    public bool IsAuthenticated => _client != null && !string.IsNullOrEmpty(_displayName);
    public string? DisplayName => _displayName;
    public bool IsRateLimited => DateTime.UtcNow < _rateLimitedUntil;
    public TimeSpan? RateLimitRemainingTime => IsRateLimited ? _rateLimitedUntil - DateTime.UtcNow : null;

    // Event for MFA code request
    public event Func<Task<string?>>? OnMfaCodeRequired;

    public GarminService(GarminSessionStore sessionStore)
    {
        _sessionStore = sessionStore;
    }

    /// <summary>
    /// Login to Garmin Connect with MFA support and token caching.
    /// Uses the library's ITokenCache for OAuth2 token persistence.
    /// </summary>
    public async Task<(bool Success, string? Error)> LoginAsync(string email, string password)
    {
        // Check if we're currently rate-limited
        if (IsRateLimited)
        {
            var remaining = RateLimitRemainingTime!.Value;
            return (false, $"Rate-limited by Garmin Connect. Please wait {remaining.Minutes} minutes and {remaining.Seconds} seconds before trying again.");
        }

        // Use email as user key for session/token storage
        var userKey = ComputeUserKey(email);
        _currentUserKey = userKey;

        // Create token cache for this user - the library will reuse tokens automatically
        var tokenCache = new GarminTokenCache(_sessionStore, userKey);

        // Check if we have a valid cached session with matching credentials
        var existingSession = _sessionStore.GetSession(userKey);
        var passwordHash = ComputeSimpleHash(password);

        if (existingSession != null && existingSession.PasswordHash == passwordHash && existingSession.Token != null)
        {
            // Session exists with cached token - try to reuse it
            try
            {
                System.Diagnostics.Debug.WriteLine($"[GarminService] Attempting to restore session for {email} using cached OAuth2 token");

                var authParameters = new BasicAuthParameters(email, password);
                var mfaCodeProvider = new InteractiveMfaCodeProvider(this);

                // Create context with token cache - library handles token refresh
                _context = new GarminConnectContext(new HttpClient(), authParameters, mfaCodeProvider, tokenCache);
                _client = new GarminConnectClient(_context);

                // Verify session is still valid by fetching profile
                var profile = await _client.GetSocialProfile();
                if (profile != null)
                {
                    _displayName = profile.DisplayName;
                    System.Diagnostics.Debug.WriteLine($"[GarminService] Session restored successfully for {_displayName}");
                    return (true, null);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GarminService] Session restoration failed: {ex.Message}");
                // Token expired or invalid, need fresh login
                CleanupSession();
                _sessionStore.ClearSession(userKey);
            }
        }

        // Enforce minimum interval between NEW login attempts
        var timeSinceLastAttempt = DateTime.UtcNow - _lastLoginAttempt;
        if (timeSinceLastAttempt < MinLoginInterval)
        {
            var waitTime = MinLoginInterval - timeSinceLastAttempt;
            return (false, $"Please wait {waitTime.Seconds} seconds before attempting to login again. Garmin rate-limits frequent login attempts.");
        }

        _lastLoginAttempt = DateTime.UtcNow;

        try
        {
            System.Diagnostics.Debug.WriteLine($"[GarminService] Starting fresh login for {email} at {DateTime.UtcNow:HH:mm:ss.fff}");

            var authParameters = new BasicAuthParameters(email, password);
            var mfaCodeProvider = new InteractiveMfaCodeProvider(this);

            System.Diagnostics.Debug.WriteLine("[GarminService] Creating Garmin Connect context with token cache...");

            // Create context with token cache - the library will:
            // 1. Check cache for valid token
            // 2. Authenticate if no token cached
            // 3. Store new token in cache
            // 4. Auto-refresh token when needed
            _context = new GarminConnectContext(new HttpClient(), authParameters, mfaCodeProvider, tokenCache);
            _client = new GarminConnectClient(_context);

            System.Diagnostics.Debug.WriteLine("[GarminService] Fetching user profile to verify authentication...");
            var profile = await _client.GetSocialProfile();
            _displayName = profile.DisplayName;

            System.Diagnostics.Debug.WriteLine($"[GarminService] Login successful! User: {_displayName}");

            // Update session metadata (token is already cached by GarminTokenCache)
            var sessionData = _sessionStore.GetSession(userKey) ?? new GarminSessionData { Email = email };
            sessionData.PasswordHash = passwordHash;
            sessionData.DisplayName = _displayName;
            sessionData.CreatedAt = DateTime.UtcNow;
            _sessionStore.SaveSession(userKey, sessionData);

            _consecutiveFailures = 0;

            return (true, null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GarminService] ❌ Login failed with exception: {ex.GetType().Name}");
            System.Diagnostics.Debug.WriteLine($"[GarminService] Message: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[GarminService] StackTrace: {ex.StackTrace}");

            if (ex.InnerException != null)
            {
                System.Diagnostics.Debug.WriteLine($"[GarminService] Inner Exception: {ex.InnerException.GetType().Name}");
                System.Diagnostics.Debug.WriteLine($"[GarminService] Inner Message: {ex.InnerException.Message}");
                System.Diagnostics.Debug.WriteLine($"[GarminService] Inner StackTrace: {ex.InnerException.StackTrace}");
            }

            CleanupSession();
            _consecutiveFailures++;

            bool isRateLimited = ex.Message.Contains("Rate limited") ||
                                 ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                                 ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
                                 ex.Message.Contains("too many", StringComparison.OrdinalIgnoreCase) ||
                                 ex.Message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase) ||
                                 ex is Garmin.Connect.Exceptions.GarminConnectTooManyRequestsException;

            if (isRateLimited || _consecutiveFailures >= MaxConsecutiveFailures)
            {
                // Apply exponential backoff for rate limiting, starting from RateLimitCooldown
                var multiplier = Math.Pow(2, Math.Min(_consecutiveFailures - 1, 3));
                var cooldown = TimeSpan.FromTicks((long)(RateLimitCooldown.Ticks * multiplier));
                _rateLimitedUntil = DateTime.UtcNow + cooldown;
                return (false, $"Garmin Connect is rate-limiting login attempts. Please wait {cooldown.TotalMinutes:F0} minutes before trying again.");
            }

            if (ex.Message.Contains("401") || ex.Message.Contains("Unauthorized") ||
                ex is Garmin.Connect.Auth.External.GarminConnectAuthenticationException)
            {
                return (false, "Invalid email or password.");
            }

            // Return detailed error for debugging
            var errorDetails = ex.InnerException != null 
                ? $"{ex.Message} (Inner: {ex.InnerException.Message})" 
                : ex.Message;

            return (false, $"Login failed: {errorDetails}");
        }
    }

    private void CleanupSession()
    {
        _context = null;
        _client = null;
        _displayName = null;
    }

    private static string ComputeUserKey(string email)
    {
        // Use email hash as cache key
        return $"user_{ComputeSimpleHash(email)}";
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
        if (_currentUserKey != null)
        {
            _sessionStore.ClearSession(_currentUserKey);
        }
        CleanupSession();
        _currentUserKey = null;
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
    }
}

/// <summary>
/// Token cache implementation that integrates with GarminSessionStore.
/// This allows OAuth2 tokens to persist across Blazor circuit reconnections.
/// </summary>
internal class GarminTokenCache : ITokenCache
{
    private readonly GarminSessionStore _sessionStore;
    private readonly string _userKey;

    public GarminTokenCache(GarminSessionStore sessionStore, string userKey)
    {
        _sessionStore = sessionStore;
        _userKey = userKey;
    }

    public Task<OAuth2Token?> GetOAuth2Token(CancellationToken cancellationToken)
    {
        var session = _sessionStore.GetSession(_userKey);
        if (session?.Token != null && session.TokenExpiresAt > DateTimeOffset.UtcNow)
        {
            System.Diagnostics.Debug.WriteLine($"[GarminTokenCache] Retrieved valid cached token, expires at {session.TokenExpiresAt}");
            return Task.FromResult<OAuth2Token?>(session.Token);
        }

        System.Diagnostics.Debug.WriteLine("[GarminTokenCache] No valid cached token found");
        return Task.FromResult<OAuth2Token?>(null);
    }

    public Task SetOAuth2Token(OAuth2Token token, CancellationToken cancellationToken)
    {
        var session = _sessionStore.GetSession(_userKey) ?? new GarminSessionData();
        session.Token = token;
        session.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);
        _sessionStore.SaveSession(_userKey, session);

        System.Diagnostics.Debug.WriteLine($"[GarminTokenCache] Cached new token, expires at {session.TokenExpiresAt}");
        return Task.CompletedTask;
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
        // Add a small delay to make the flow more human-like
        await Task.Delay(500);

        var code = await _service.RequestMfaCodeAsync();
        return code ?? throw new InvalidOperationException("MFA code was not provided");
    }
}
