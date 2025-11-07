namespace BlazorBffOnboarding;

public static class UriHelper
{
    /// <summary>
    /// Get the URI for the <paramref name="request"/>'s base address.
    /// </summary>
    public static Uri GetBaseUri(this HttpRequest request)
    {
        var builder = new UriBuilder
        {
            Scheme = request.Scheme,
            Host = request.Host.Host
        };
        if (request.Host.Port.HasValue)
        {
            builder.Port = request.Host.Port.Value;
        }
        return builder.Uri;
    }
}