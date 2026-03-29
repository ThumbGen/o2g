using Garmin.Connect.Auth;
using Microsoft.Extensions.Caching.Memory;

namespace Omron2Garmin.Web.Services;

/// <summary>
/// Stores Garmin sessions per user in memory cache to survive Blazor circuit reconnections.
/// Now includes OAuth2 token storage for the library's ITokenCache integration.
/// </summary>
public class GarminSessionStore
{
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan SessionExpiration = TimeSpan.FromHours(8);

    public GarminSessionStore(IMemoryCache cache)
    {
        _cache = cache;
    }

    public GarminSessionData? GetSession(string userKey)
    {
        return _cache.TryGetValue(GetCacheKey(userKey), out GarminSessionData? session) 
            ? session 
            : null;
    }

    public void SaveSession(string userKey, GarminSessionData session)
    {
        var cacheKey = GetCacheKey(userKey);
        _cache.Set(cacheKey, session, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = SessionExpiration,
            SlidingExpiration = TimeSpan.FromHours(1)
        });
    }

    public void ClearSession(string userKey)
    {
        _cache.Remove(GetCacheKey(userKey));
    }

    private static string GetCacheKey(string userKey) => $"garmin_session_{userKey}";
}

/// <summary>
/// Session data for a Garmin user including OAuth2 token for the library's ITokenCache.
/// </summary>
public class GarminSessionData
{
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Cached OAuth2 token from the Garmin.Connect library.
    /// The library uses this to avoid re-authenticating on each startup.
    /// </summary>
    public OAuth2Token? Token { get; set; }

    /// <summary>
    /// When the OAuth2 token expires.
    /// </summary>
    public DateTimeOffset TokenExpiresAt { get; set; }
}
