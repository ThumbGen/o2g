using System.Net.Http;
using System.Diagnostics;

namespace Omron2Garmin.Web.Services;

/// <summary>
/// HTTP message handler that adds delays between requests to appear more human-like
/// and avoid triggering Garmin's fraud detection
/// </summary>
public class ThrottledHttpMessageHandler : DelegatingHandler
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private DateTime _lastRequestTime = DateTime.MinValue;
    private readonly TimeSpan _minimumDelay;
    private readonly TimeSpan _maximumDelay;
    private readonly Random _random = new();
    private int _requestCount = 0;

    public ThrottledHttpMessageHandler(HttpMessageHandler innerHandler, TimeSpan? minimumDelay = null, TimeSpan? maximumDelay = null) 
        : base(innerHandler)
    {
        _minimumDelay = minimumDelay ?? TimeSpan.FromSeconds(3);
        _maximumDelay = maximumDelay ?? TimeSpan.FromSeconds(5);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, 
        CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            _requestCount++;

            // Log the request for debugging
            Debug.WriteLine($"[Garmin Request #{_requestCount}] {request.Method} {request.RequestUri}");

            // Calculate time since last request
            var timeSinceLastRequest = DateTime.UtcNow - _lastRequestTime;

            // Add random jitter to appear more human-like (between min and max delay)
            var randomDelay = _minimumDelay + TimeSpan.FromMilliseconds(
                _random.Next(0, (int)(_maximumDelay - _minimumDelay).TotalMilliseconds));

            // If we're going too fast, add a delay
            if (timeSinceLastRequest < randomDelay && _lastRequestTime != DateTime.MinValue)
            {
                var delayNeeded = randomDelay - timeSinceLastRequest;
                Debug.WriteLine($"[Garmin Request #{_requestCount}] Throttling: waiting {delayNeeded.TotalSeconds:F1}s");
                await Task.Delay(delayNeeded, cancellationToken);
            }

            _lastRequestTime = DateTime.UtcNow;

            // Send the request
            var response = await base.SendAsync(request, cancellationToken);

            Debug.WriteLine($"[Garmin Request #{_requestCount}] Response: {(int)response.StatusCode} {response.StatusCode}");

            // If we get rate limited, log it prominently
            if ((int)response.StatusCode == 429 || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                Debug.WriteLine($"[Garmin Request #{_requestCount}] ⚠️ RATE LIMITED! Status: {response.StatusCode}");
            }

            return response;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _semaphore?.Dispose();
        }
        base.Dispose(disposing);
    }
}
