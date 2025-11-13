// Copyright (c) 2025 Hugh Maaskant. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using BlazorBffOnboarding;
using BlazorBffOnboarding.AppUser;
using BlazorBffOnboarding.Client;
using BlazorBffOnboarding.Components;
using BlazorBffOnboarding.Persistence;
using Duende.Bff;
using Duende.Bff.Blazor;
using Duende.Bff.Yarp;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Primitives;

var builder = WebApplication.CreateBuilder(args);

// Remove the Server header from all responses
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.AddServerHeader = false;
});

// Add services to the container.
builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// BFF setup for Blazor
builder.Services
    .AddBff()
    .AddServerSideSessions() // Add in-memory implementation of server side sessions
    .AddBlazorServer()
    .AddRemoteApis();

// Register an HTTP client for the external greeting API that uses the user's access token
builder.Services.AddUserAccessTokenHttpClient("greet",
    configureClient: client => client.BaseAddress = new Uri("https://localhost:7001/"));

// Register an abstraction for retrieving weather forecasts that can run on the server
// On the WASM client this will be retrieved via an HTTP call to the server
builder.Services.AddSingleton<IWeatherClient, ServerWeatherClient>();

// Register the AppUserService
builder.Services.AddScoped<IAppUserService, AppUserService>();

// Register ApplicationDbContext with SQLite
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// Get the active authentication scheme from configuration
var activeScheme = builder.Configuration["ActiveAuthenticationScheme"] ??
                   throw new InvalidOperationException("ActiveAuthenticationScheme not configured");

// Configure OIDC Provider specific authentication options from the active scheme's configuration section
builder.Services.AddOptions<OpenIdConnectOptions>("oidc")
    .Bind(builder.Configuration.GetSection($"Authentication:{activeScheme}"));

// Configure the authentication
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "cookie";
        options.DefaultChallengeScheme = "oidc";
        options.DefaultSignOutScheme = "oidc";
    })
    .AddCookie("cookie", options =>
    {
        // The cookie with the app's authentication properties, it is also the default authentication scheme
        options.Cookie.Name = "__Host-blazor-app";
        options.Cookie.SameSite = SameSiteMode.Lax;
    })
    .AddCookie("cookie-idp", options =>
    {
        // the temporary cookie for the IDP to sign in the user with the IDP's identity and properties
        options.Cookie.Name = "__Host-blazor-idp";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddOpenIdConnect("oidc", options =>
    {
        // These are default values, but we set them for clarity
        options.CallbackPath = new PathString("/signin-oidc");
        options.SignedOutCallbackPath = new PathString("/signout-callback-oidc");
        options.RemoteSignOutPath = new PathString("/signout-oidc");

        // Protocol options
        options.ResponseType = "code";
        options.ResponseMode = "query";

        // Processing options
        options.SaveTokens = true;
        options.MapInboundClaims = false;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = "role";
        options.SignInScheme = "cookie-idp";
        options.SignOutScheme = "cookie";
        options.SignedOutRedirectUri = "/";

        options.Events.OnTokenValidated = context =>
        {
            if (context.Principal?.Identity is not ClaimsIdentity claimsIdentity)
            {
                throw new InvalidOperationException("Cannot retrieve ClaimsIdentity from Principal");
            }

            // Duende BFF needs a Session ID (sid) claim, if the IDP doesn't have one, polyfill our own
            if (claimsIdentity.FindFirst("sid") == null)
            {
                claimsIdentity.AddClaim(new Claim("sid", Guid.CreateVersion7().ToString()));
            }

            return Task.CompletedTask;
        };

        options.Events.OnTicketReceived = async context =>
        {
            var idpPrincipal = context.Principal ??
                throw new InvalidOperationException("IDP Principal cannot be Null");

            // Determine the return URL but only allow local URLs, fall back to "/" if not local.
            var returnUrl = context.ReturnUri;
            if (string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith('/'))
            {
                returnUrl = "/";
            }

            var idpSubject = idpPrincipal.FindFirstValue("sub") ??
                             throw new InvalidOperationException("'sub' claim not found");

            var appUserService = context.HttpContext.RequestServices.GetRequiredService<IAppUserService>();
            var appUser = await appUserService.FindUserByExternalIdAsync(activeScheme, idpSubject);

            if (appUser != null)
            {
                // User already exists
                var appUserId = appUser.Id.ToString();
                var (appPrincipal, appProperties) = TransformPrincipal(idpPrincipal, context.Properties, appUserId);

                // Complete the sign-in process by switching from the idp cookie to the app cookie
                await PerformSignInAsync(context.HttpContext, appPrincipal, appProperties);

                // Tell the ASP.NET Core middleware we have taken over the Authentication flow
                context.HandleResponse();

                context.Response.Redirect(returnUrl);
            }
            else
            {
                // User is new
                if (context.Properties is not null)
                {
                    // preserve the user's sanitized returnUrl to use after onboarding
                    context.Properties.Items["ultimateReturnUrl"] = returnUrl;
                }

                // Continue the sign-in process (directly or indirectly via "/diag/idp") with the onboarding UI
                context.ReturnUri = builder.Configuration.GetValue<bool>("EnableAuthDiagnostics")
                    ? "/diag/idp"                 // Shows Auth info and contains a link to "/onboarding" to proceed
                    : "/onboarding/interactive";  // The onboarding Blazor page with a sample form
            }
        };

        options.Events.OnRedirectToIdentityProviderForSignOut = context =>
        {
            // Ensure we leave the user with a clean URL in the browser, i.e., without the state parameter
            context.ProtocolMessage.PostLogoutRedirectUri =
                new Uri(context.Request.GetBaseUri(), context.Options.SignedOutCallbackPath)
                .ToString();
            return Task.CompletedTask;
        };
    });

// Make sure the authentication state is available to all components
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddAuthorization(options =>
{
    // To protect the /diag/idp endpoint
    options.AddPolicy("cookie-idp", policy =>
    {
        policy.AddAuthenticationSchemes("cookie-idp");
        policy.RequireAuthenticatedUser();
    });

    // To protect the /admin/users endpoint
    options.AddPolicy("Admin", policy =>
    {
        policy.RequireAuthenticatedUser();
        // In a real app, you would also require a specific role claim:
        // policy.RequireRole("admin");
    });
});

// Configure Rate Limiting for the onboarding endpoint and the antiforgery token endpoint
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("onboarding", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(15)
            }));

    // Lightweight rate-limit for antiforgery token requests.
    // Only the authenticated temporary IDP session should call this, but add rate limiting as defense-in-depth.
    options.AddPolicy("antiforgery", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,               // e.g. 10 requests
                Window = TimeSpan.FromMinutes(5)
            }));
});

builder.Services.Configure<CircuitOptions>(options =>
{
    // Development-time only: show detailed exceptions on circuits
    options.DetailedErrors = true;
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();
app.UseAuthentication();

app.UseBff();
app.UseAuthorization();
app.UseAntiforgery();

app.MapBffManagementEndpoints();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(BlazorBffOnboarding.Client._Imports).Assembly);

app.MapRemoteBffApiEndpoint("/remote-apis/greetings", "https://localhost:7001")
    .RequireAccessToken(TokenType.User);

app.MapWeatherEndpoints();

// This is where the onboarding form's POST callback is handled
app.MapPost("/onboarding", async (HttpContext context, [FromForm] OnboardingInputModel model) =>
{
    // Ensure we are authenticated with the IDP
    var result = await context.AuthenticateAsync("cookie-idp");
    if (!result.Succeeded || result.Principal is null || result.Properties is null)
        return Results.Challenge();
    
    // Request validation
    if (!context.Request.HasFormContentType)
        return Results.BadRequest("Invalid content type");
    
    var contentLength = context.Request.ContentLength ?? 0;
    if (contentLength > 1024) // 1KB limit
        return Results.BadRequest("Request too large");
    
    // Server-side validation of the incoming model
    var validationResults = new List<ValidationResult>();
    var validationContext = new ValidationContext(model);

    if (!Validator.TryValidateObject(model, validationContext, validationResults, true))
    {
        // The user provided invalid data. Initiate a full OIDC sign-out and redirect to the homepage with an error message
        // Extract and combine the validation error messages
        var errorMessages = validationResults
            .Select(r => r.ErrorMessage)
            .Where(m => m is not null)
            .ToList();
        var combinedErrorMessage = string.Join("; ", errorMessages);

        // URL-encode the message
        var encodedMessage = System.Web.HttpUtility.UrlEncode(combinedErrorMessage);

        // Initiate a full OIDC sign-out and redirect to the home page with errormessage
        var authProps = new AuthenticationProperties
        {
            RedirectUri = $"/?onboardingError=true&message={encodedMessage}"
        };
        return Results.SignOut(authProps, authenticationSchemes: ["cookie", "oidc"]);
    }

    // Obtain the AppUserService
    var appUserService = context.RequestServices.GetRequiredService<IAppUserService>();
    var idpSubject = result.Principal.FindFirstValue("sub") ?? 
                     throw new InvalidOperationException("sub claim not found");

    var newAppUser = await appUserService.CreateAppUserAsync(activeScheme, idpSubject, model.DisplayName!);
    var newUserId = newAppUser.Id.ToString();

    var (appPrincipal, appProperties) = TransformPrincipal(result.Principal, result.Properties, newUserId);

    // Examples of Claim transformations
    var identity = (ClaimsIdentity)appPrincipal.Identity!;
    // Ensure there is a "name" claim, use DisplayName as fallback value if there was none
    if (!identity.HasClaim(claim => claim.Type.Equals("name", StringComparison.InvariantCultureIgnoreCase)))
    {
        identity.AddClaim(new Claim("name", model.DisplayName!));
    }
    // Add the new display name as a claim to the appPrincipal
    identity.AddClaim(new Claim("display-name", model.DisplayName!));
        
    appProperties.Items.TryGetValue("ultimateReturnUrl", out var finalReturnUrl);
    appProperties.Items.Remove("ultimateReturnUrl");

    // Complete the sign-in process by switching from the idp cookie to the app cookie
    await PerformSignInAsync(context, appPrincipal, appProperties);
        
    return Results.LocalRedirect(finalReturnUrl ?? "/");
}).RequireAuthorization("cookie-idp")
  .RequireRateLimiting("onboarding");

// Async antiforgery token endpoint (require cookie-idp and rate limiting)
app.MapGet("/antiforgery/token", async (HttpContext ctx, IAntiforgery antiforgery) =>
{
    // Require the temporary IDP cookie session
    var authResult = await ctx.AuthenticateAsync("cookie-idp");
    if (!authResult.Succeeded) return Results.Unauthorized();

    var tokens = antiforgery.GetAndStoreTokens(ctx);
    return Results.Json(new { token = tokens.RequestToken });
})
.RequireAuthorization("cookie-idp")
.RequireRateLimiting("antiforgery");

// Development-only diagnostic /onboarding/finish handler
app.MapPost("/onboarding/finish", async (HttpContext context, IAntiforgery antiforgery, ILogger<Program> logger) =>
{
    logger.LogInformation("Onboarding/finish START - RemoteIp={RemoteIp}", context.Connection.RemoteIpAddress);

    // Ensure cookie-idp auth (we need auth properties)
    var auth = await context.AuthenticateAsync("cookie-idp");
    if (!auth.Succeeded || auth.Principal is null || auth.Properties is null)
    {
        logger.LogWarning("Onboarding/finish: not authenticated (cookie-idp)");
        return Results.Json(new { success = false, message = "Not authenticated" }, statusCode: 401);
    }

    // Validate antiforgery token
    try
    {
        await antiforgery.ValidateRequestAsync(context);
    }
    catch (AntiforgeryValidationException ex)
    {
        logger.LogWarning(ex, "Onboarding/finish: antiforgery validation failed");
        return Results.Json(new { success = false, message = "Invalid antiforgery token" }, statusCode: 400);
    }

    // Ensure JSON content
    var contentType = context.Request.ContentType ?? "";
    if (!contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
    {
        logger.LogWarning("Onboarding/finish: unexpected Content-Type: {ContentType}", contentType);
        return Results.Json(new { success = false, message = "Expected application/json" }, statusCode: 400);
    }

    // Read and validate model
    OnboardingInputModel? model;
    try
    {
        model = await context.Request.ReadFromJsonAsync<OnboardingInputModel>();
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Onboarding/finish: malformed JSON");
        return Results.Json(new { success = false, message = "Malformed JSON" }, statusCode: 400);
    }

    if (model is null)
        return Results.Json(new { success = false, message = "Missing body" }, statusCode: 400);

    var validationResults = new List<ValidationResult>();
    var validationContext = new ValidationContext(model);
    if (!Validator.TryValidateObject(model, validationContext, validationResults, true))
    {
        var dict = validationResults
            .SelectMany(r => (r.MemberNames.Any() ? r.MemberNames : new[] { string.Empty })
                              .Select(m => new { Field = m, Error = r.ErrorMessage ?? string.Empty }))
            .GroupBy(x => x.Field)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Error).ToArray());

        logger.LogInformation("Onboarding/finish: validation failed {@Validation}", dict);
        return Results.Json(new { success = false, validation = dict }, statusCode: 400);
    }

    // Create user and perform sign-in
    try
    {
        var appUserService = context.RequestServices.GetRequiredService<IAppUserService>();
        var idpSubject = auth.Principal.FindFirstValue("sub") ?? throw new InvalidOperationException("sub claim not found");

        var newAppUser = await appUserService.CreateAppUserAsync(activeScheme, idpSubject, model.DisplayName!);
        var newUserId = newAppUser.Id.ToString();

        var (appPrincipal, appProperties) = TransformPrincipal(auth.Principal, auth.Properties, newUserId);

        var identity = (ClaimsIdentity)appPrincipal.Identity!;
        if (!identity.HasClaim(c => c.Type.Equals("name", StringComparison.InvariantCultureIgnoreCase)))
        {
            identity.AddClaim(new Claim("name", model.DisplayName!));
        }
        identity.AddClaim(new Claim("display-name", model.DisplayName!));

        appProperties.Items.TryGetValue("ultimateReturnUrl", out var finalReturnUrl);
        appProperties.Items.Remove("ultimateReturnUrl");

        logger.LogInformation("Onboarding/finish: performing SignIn/SignOut for new user {UserId}", newUserId);
        await PerformSignInAsync(context, appPrincipal, appProperties);
        logger.LogInformation("Onboarding/finish: SignIn complete");

        return Results.Json(new { success = true, redirectUrl = finalReturnUrl ?? "/" });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Onboarding/finish: error creating user or signing in");
        return Results.Json(new { success = false, message = "Server error" }, statusCode: 500);
    }
})
.RequireAuthorization("cookie-idp")
.RequireRateLimiting("onboarding");

    // Additional Security Headers  
app.Use(async (context, next) =>
{
    context.Response.Headers.TryAdd("X-Frame-Options", "DENY");
    context.Response.Headers.TryAdd("X-Content-Type-Options", "nosniff");
    context.Response.Headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
    await next();
});

app.Run();

return;

static (ClaimsPrincipal, AuthenticationProperties) TransformPrincipal(
    ClaimsPrincipal idpPrincipal, AuthenticationProperties? idpProperties, string newUserId)
{
    var appClaims = new List<Claim>();

    // Copy (or rename) any claims that need to be preserved from the IDP principal
    foreach (var claim in idpPrincipal.Claims)
    {
        appClaims.Add(claim.Type == "sub" ? new Claim("idp-sub", claim.Value) : claim);
    }
    // Add the Application User ID as the "sub" claim
    appClaims.Add(new Claim("sub", newUserId));

    // Create the new appIdentity and appPrincipal using the transformed claims
    var appIdentity = new ClaimsIdentity(appClaims, "cookie", nameType: "name", roleType: "role");
    var appPrincipal = new ClaimsPrincipal(appIdentity);

    // If the original properties are null, create a new empty set. Otherwise, use the original properties to preserve all data.
    // Here too you could filter and/or add Properties at will, be sure to preserve the tokens though.
    var appProperties = idpProperties ?? new AuthenticationProperties();

    return (appPrincipal, appProperties);
}

// Sign-in to the default (App) cookie ("cookie" scheme) and also
// sign out of the IDP cookie ("cookie-idp" scheme).
static async Task PerformSignInAsync(HttpContext context, ClaimsPrincipal appPrincipal, AuthenticationProperties props)
{
    await context.SignInAsync("cookie", appPrincipal, props);
    await context.SignOutAsync("cookie-idp");
}