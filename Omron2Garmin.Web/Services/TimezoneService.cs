using Microsoft.JSInterop;

namespace Omron2Garmin.Web.Services;

/// <summary>
/// Service for detecting browser timezone using JavaScript Interop
/// </summary>
public class TimezoneService
{
    private readonly IJSRuntime _jsRuntime;
    private string? _cachedTimezone;

    public TimezoneService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    /// <summary>
    /// Get the browser's timezone IANA ID (e.g., "Europe/Berlin", "America/New_York")
    /// </summary>
    public async Task<string> GetBrowserTimezoneAsync()
    {
        // Return cached value if available
        if (!string.IsNullOrEmpty(_cachedTimezone))
            return _cachedTimezone;

        try
        {
            _cachedTimezone = await _jsRuntime.InvokeAsync<string>("getBrowserTimezone");
            return _cachedTimezone ?? "UTC";
        }
        catch
        {
            // Fallback to UTC if JS interop fails
            _cachedTimezone = "UTC";
            return _cachedTimezone;
        }
    }

    /// <summary>
    /// Get TimeZoneInfo from browser timezone
    /// </summary>
    public async Task<TimeZoneInfo> GetBrowserTimeZoneInfoAsync()
    {
        var ianaId = await GetBrowserTimezoneAsync();

        try
        {
            // Try to find by IANA ID (works on Linux/Mac)
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
        catch
        {
            // If IANA ID doesn't work, try to map to Windows timezone ID
            var windowsId = MapIanaToWindows(ianaId);
            if (!string.IsNullOrEmpty(windowsId))
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
                }
                catch
                {
                    // Fall through to UTC
                }
            }

            // Fallback to UTC
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// Map common IANA timezone IDs to Windows timezone IDs
    /// </summary>
    private static string? MapIanaToWindows(string ianaId)
    {
        // Common mappings - for a complete solution, use TimeZoneConverter NuGet package
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Europe
            { "Europe/London", "GMT Standard Time" },
            { "Europe/Paris", "Romance Standard Time" },
            { "Europe/Berlin", "W. Europe Standard Time" },
            { "Europe/Rome", "W. Europe Standard Time" },
            { "Europe/Madrid", "Romance Standard Time" },
            { "Europe/Amsterdam", "W. Europe Standard Time" },
            { "Europe/Brussels", "Romance Standard Time" },
            { "Europe/Vienna", "W. Europe Standard Time" },
            { "Europe/Zurich", "W. Europe Standard Time" },
            { "Europe/Stockholm", "W. Europe Standard Time" },
            { "Europe/Oslo", "W. Europe Standard Time" },
            { "Europe/Copenhagen", "Romance Standard Time" },
            { "Europe/Warsaw", "Central European Standard Time" },
            { "Europe/Prague", "Central European Standard Time" },
            { "Europe/Budapest", "Central Europe Standard Time" },
            { "Europe/Bucharest", "E. Europe Standard Time" },
            { "Europe/Athens", "GTB Standard Time" },
            { "Europe/Helsinki", "FLE Standard Time" },
            { "Europe/Moscow", "Russian Standard Time" },
            { "Europe/Istanbul", "Turkey Standard Time" },

            // Americas
            { "America/Los_Angeles", "Pacific Standard Time" },
            { "America/Denver", "Mountain Standard Time" },
            { "America/Chicago", "Central Standard Time" },
            { "America/New_York", "Eastern Standard Time" },
            { "America/Toronto", "Eastern Standard Time" },
            { "America/Mexico_City", "Central Standard Time (Mexico)" },
            { "America/Sao_Paulo", "E. South America Standard Time" },
            { "America/Buenos_Aires", "Argentina Standard Time" },

            // Asia/Pacific
            { "Asia/Dubai", "Arabian Standard Time" },
            { "Asia/Singapore", "Singapore Standard Time" },
            { "Asia/Shanghai", "China Standard Time" },
            { "Asia/Hong_Kong", "China Standard Time" },
            { "Asia/Tokyo", "Tokyo Standard Time" },
            { "Asia/Seoul", "Korea Standard Time" },
            { "Asia/Bangkok", "SE Asia Standard Time" },
            { "Asia/Kolkata", "India Standard Time" },
            { "Australia/Sydney", "AUS Eastern Standard Time" },
            { "Australia/Melbourne", "AUS Eastern Standard Time" },
            { "Pacific/Auckland", "New Zealand Standard Time" },

            // UTC
            { "UTC", "UTC" },
            { "Etc/UTC", "UTC" }
        };

        return mappings.TryGetValue(ianaId, out var windowsId) ? windowsId : null;
    }
}
