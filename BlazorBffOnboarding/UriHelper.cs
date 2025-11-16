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
    
    
    /// <summary>
    /// Determines if a given Url is a local Url
    /// </summary>
    /// <remarks>
    /// This is inspired by .NET 10 where this is a standard function, see
    /// https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-10.0?view=aspnetcore-10.0#detect-if-url-is-local-using-redirecthttpresultislocalurl
    /// </remarks>
    public static bool IsLocalUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        switch (url[0])
        {
            // Allows "/" or "/foo" but not "//" or "/\".
            
            // url is exactly "/"
            case '/' when url.Length == 1:
                return true;
            
            // url doesn't start with "//" or "/\"
            case '/' when url[1] != '/' && url[1] != '\\':
                return !HasControlCharacter(url.AsSpan(1));
            
            // Allows "~/" or "~/foo" but not "~//" or "~/\".
           case '~' when url.Length > 1 && url[1] == '/':
            {
                // url is exactly "~/"
                if (url.Length == 2)
                {
                    return true;
                }
 
                // url doesn't start with "~//" or "~/\"
                if (url[2] != '/' && url[2] != '\\')
                    return !HasControlCharacter(url.AsSpan(2));
                break;
            }
        }

        // Any pattern not matches by the switch is not local
        return false;
 
        static bool HasControlCharacter(ReadOnlySpan<char> readOnlySpan)
        {
            // URLs may not contain ASCII control characters.
            for (var i = 0; i < readOnlySpan.Length; i++)
            {
                if (char.IsControl(readOnlySpan[i]))
                {
                    return true;
                }
            }
 
            return false;
        }
    }
}