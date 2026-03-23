using System.Net;

namespace Omron2Garmin.Web.Services;

/// <summary>
/// Manages persistent sessions with Garmin Connect to avoid repeated logins
/// </summary>
public class GarminSessionManager
{
    private CookieContainer? _cookieContainer;
    private DateTime _sessionCreated = DateTime.MinValue;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    public bool HasValidSession => _cookieContainer != null && 
                                    DateTime.UtcNow - _sessionCreated < SessionLifetime;

    public CookieContainer? GetCookieContainer()
    {
        if (HasValidSession)
        {
            return _cookieContainer;
        }
        return null;
    }

    public void SaveSession(CookieContainer cookies)
    {
        _cookieContainer = cookies;
        _sessionCreated = DateTime.UtcNow;
    }

    public void ClearSession()
    {
        _cookieContainer = null;
        _sessionCreated = DateTime.MinValue;
    }

    public TimeSpan GetSessionAge()
    {
        if (_cookieContainer == null)
        {
            return TimeSpan.Zero;
        }
        return DateTime.UtcNow - _sessionCreated;
    }
}
