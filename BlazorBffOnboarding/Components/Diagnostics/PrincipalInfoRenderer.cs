using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;

namespace BlazorBffOnboarding.Components.Diagnostics;

public static class PrincipalInfoRenderer
{

    public static async Task<RenderFragment> GetAuthenticationInfoAsRenderFragmentAsync(HttpContext context, string scheme)
    {
        var htmlContent = await GetAuthenticationInfoAsHtmlAsync(context, scheme);

        return builder =>
        {
            builder.AddMarkupContent(0, htmlContent);
        };
    }

    public static async Task<string> GetAuthenticationInfoAsHtmlAsync(HttpContext context, string scheme)
    {
        var sb = new StringBuilder();

        var authResult = await context.AuthenticateAsync(scheme);

        if (!authResult.Succeeded)
        {
            sb.Append($"<h1>Not authenticated via scheme '{scheme}'</h1>");
            sb.Append("/<p>The authentication cookie was not found or was invalid.</p>");

            return sb.ToString();
        }

        // Authentication successful, build the HTML content
        sb.Append($"<h1>Authenticated via scheme {scheme}</h1>");

        sb.Append("<h2>Claims</h2>");
        if (authResult.Principal.Claims.Any())
        {
            sb.Append("<ul>");
            foreach (var claim in authResult.Principal.Claims)
            {
                sb.Append($"<li><b>{claim.Type}</b>: {claim.Value}</li>");
            }
            sb.Append("</ul>");
        }
        else
        {
            sb.Append("<p>No Claims found...</p>");
        }

        sb.Append("<h2>Properties</h2>");
        if (authResult.Properties.Items.Any())
        {
            sb.Append("<ul>");
            foreach (var prop in authResult.Properties.Items)
            {
                sb.Append($"<li><b>{prop.Key}</b>: {prop.Value}</li>");
            }
            sb.Append("</ul>");
        }
        else
        {
            sb.Append("<p>No Properties found...</p>");
        }

        sb.Append("<h2>Tokens</h2>");
        var tokens = authResult.Properties.GetTokens().ToList();
        if (tokens.Count != 0)
        {
            sb.Append("<ul>");
            foreach (var token in tokens)
            {
                var valueToShow = token.Value.Length > 80 ? token.Value.Substring(0, 80) + "..." : token.Value;
                sb.Append($"<li><b>{token.Name}</b>: {valueToShow}</li>");
            }
            sb.Append("</ul>");
        }
        else
        {
            sb.Append("<p>No Tokens found...</p>");
        }

        return sb.ToString();
    }
}