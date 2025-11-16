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
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Remove the Kestrel Server header from all responses
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.AddServerHeader = false;
});

// Add Blazor services to the container.
builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// BFF setup for Blazor
builder.Services
    .AddBff()
    .AddServerSideSessions() // Add in-memory implementation of server side sessions
    .AddBlazorServer()
    .AddRemoteApis() ;

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
var activeScheme = builder.Configuration["Authentication:ActiveScheme"] ??
                   throw new InvalidOperationException("Active Authentication Scheme not configured");

// Configure OIDC Provider specific authentication options from the active scheme's configuration section
builder.Services.AddOptions<OpenIdConnectOptions>("oidc")
    .Bind(builder.Configuration.GetRequiredSection($"Authentication:{activeScheme}"));

// Configure Authentication
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

        // Configure the Handlers to be called by the OpenIdConnectHandler during the flows
        options.Events.OnTokenValidated = HandleOnTokenValidatedEvent;
        options.Events.OnTicketReceived = HandleOnTicketReceivedEvent;
        options.Events.OnRedirectToIdentityProviderForSignOut = HandleOnRedirectToIdpForSignOut;
    });

// Make sure the authentication state is available to all Blazor components
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("cookie-idp", policy =>
    {
        policy.AddAuthenticationSchemes("cookie-idp");
        policy.RequireAuthenticatedUser();
    })
    .AddPolicy("Admin", policy =>
    {
        policy.RequireAuthenticatedUser();
        // In a real app, you would also require a specific role claim:
        // policy.RequireRole("admin");
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

// Async antiforgery token endpoint for submitting the OnboardingInputModel from JavaScript.
// Require cookie-idp and rate limiting
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

// This is where the onboarding form's POST (from JavaScript) callback is handled
app.MapPost("/onboarding/finish", HandleOnboardingFinishPostRequest)
.RequireAuthorization("cookie-idp")
.RequireRateLimiting("onboarding");

// Add additional Security Headers to every response
app.Use(async (context, next) =>
{
    context.Response.Headers.TryAdd("X-Frame-Options", "DENY");
    context.Response.Headers.TryAdd("X-Content-Type-Options", "nosniff");
    context.Response.Headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
    await next();
});

app.Run();

return;

//
// --- Handlers called during the login and logout flows ---
//

// Called by the OIDC sign-in handler
Task HandleOnTokenValidatedEvent(TokenValidatedContext context)
{
    if (context.Principal?.Identity is not ClaimsIdentity claimsIdentity)
    {
        throw new InvalidOperationException("Cannot retrieve ClaimsIdentity from context");
    }

    // Duende BFF needs a Session ID (sid) claim, if the IDP doesn't have one, polyfill our own
    if (claimsIdentity.FindFirst("sid") == null)
    {
        claimsIdentity.AddClaim(new Claim("sid", Guid.CreateVersion7().ToString()));
    }

    return Task.CompletedTask;
}

// Called by the OIDC sign-in handler
async Task HandleOnTicketReceivedEvent(TicketReceivedContext context)
{
    var idpPrincipal = context.Principal ?? throw new InvalidOperationException("IDP Principal cannot be Null");

    // Only allow local URLs, fall back to "/" if not local
    var returnUrl = context.ReturnUri ?? string.Empty;
    if (!UriHelper.IsLocalUrl(returnUrl))
    {
        returnUrl = "/";
    }

    var idpSubject = idpPrincipal.FindFirstValue("sub") ?? throw new InvalidOperationException("'sub' claim not found");

    var appUserService = context.HttpContext.RequestServices.GetRequiredService<IAppUserService>();
    var appUser = await appUserService.FindUserByExternalIdAsync(activeScheme, idpSubject);

    if (appUser is not null)
    {
        // User already exists
        var appUserId = appUser.Id.ToString();
        var displayName = appUser.DisplayName;
        var (appPrincipal, appProperties) = TransformPrincipal(idpPrincipal, context.Properties, appUserId, displayName);

        // Complete the sign-in process by switching from the idp cookie to the app cookie
        await SwitchSignInSchemeAsync(context.HttpContext, appPrincipal, appProperties);

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
            ? "/diag/idp" // Shows Auth info and contains a link to "/onboarding" to proceed
            : "/onboarding/interactive"; // The onboarding Blazor page with a sample form
    }
}

// Called by the endpoint for the JavaScript POST back with the data from the Onboarding form ---
async Task<IResult> HandleOnboardingFinishPostRequest(HttpContext context, IAntiforgery antiforgery)
{
    // Ensure we are authenticated with the IDP
    var auth = await context.AuthenticateAsync("cookie-idp");
    if (!auth.Succeeded || auth.Principal is null || auth.Properties is null)
        return Results.Json(new { success = false, message = "Not authenticated" }, statusCode: 401);
    
    // Validate the antiforgery token
    try
    {
        await antiforgery.ValidateRequestAsync(context);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.Json(new { success = false, message = "Invalid antiforgery token" }, statusCode: 400);
    }

    // Ensure we do not have any overposting or too much data
    var contentLength = context.Request.ContentLength ?? 0;
    if (contentLength > 1024) // 1KB limit, adapt to the Onboarding Input Model max size
        return Results.BadRequest("Request too large");

    // Ensure we have JSON content
    var contentType = context.Request.ContentType ?? "";
    if (!contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        return Results.Json(new { success = false, message = "Expected application/json" }, statusCode: 400);

    // Read and validate the Onboarding Input Model from the POST data
    OnboardingInputModel? model;
    try
    {
        model = await context.Request.ReadFromJsonAsync<OnboardingInputModel>();
    }
    catch (Exception)
    {
        return Results.Json(new { success = false, message = "Malformed JSON" }, statusCode: 400);
    }
    if (model is null) 
        return Results.Json(new { success = false, message = "Missing body" }, statusCode: 400);

    // Always (re-)validate user input on the Server side
    // Note: this is different from the form validation, we got this via a JavaScript initiated POST request
    var validationResults = new List<ValidationResult>();
    var validationContext = new ValidationContext(model);
    if (!Validator.TryValidateObject(model, validationContext, validationResults, true))
    {
        var dict = validationResults
            .SelectMany(r => (r.MemberNames.Any() ? r.MemberNames : [string.Empty])
                .Select(m => new { Field = m, Error = r.ErrorMessage ?? string.Empty }))
            .GroupBy(x => x.Field)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Error).ToArray());

        return Results.Json(new { success = false, validation = dict }, statusCode: 400);
    }

    // All is well, add the new user to the database and perform sign-in with the app cookie
    try
    {
        var idpSubject = auth.Principal.FindFirstValue("sub") ?? 
                         throw new InvalidOperationException("sub claim not found");
        var displayName = model.DisplayName!; // has been validated :-)
        var appUserService = context.RequestServices.GetRequiredService<IAppUserService>();
        
        // As a quick solution, use the activeScheme to identify the IDP, in real life consider a better approach
        var newAppUser = await appUserService.CreateAppUserAsync(activeScheme, idpSubject, displayName);
        var newUserId = newAppUser.Id.ToString();

        var (appPrincipal, appProperties) = TransformPrincipal(auth.Principal, auth.Properties, newUserId, displayName);
        
        // Get and cleanup the ultimateReturnUrl
        appProperties.Items.TryGetValue("ultimateReturnUrl", out var ultimateReturnUrl);
        appProperties.Items.Remove("ultimateReturnUrl");
    
        await SwitchSignInSchemeAsync(context, appPrincipal, appProperties);

        return Results.Json(new { success = true, redirectUrl = ultimateReturnUrl ?? "/" });
    }
    catch (Exception)
    {
        // Add logging here ...
        return Results.Json(new { success = false, message = "Server error" }, statusCode: 500);
    }
}

//
// --- Helper methods ---
//

// 
static (ClaimsPrincipal appPrincipal, AuthenticationProperties appProperties) TransformPrincipal(
    ClaimsPrincipal idpPrincipal, AuthenticationProperties? idpProperties, string newUserId, string displayName)
{
    var appClaims = new List<Claim>();

    // Copy (or rename) any claims that need to be preserved from the IDP principal
    foreach (var claim in idpPrincipal.Claims)
    {
        appClaims.Add(claim.Type == "sub" ? new Claim("idp-sub", claim.Value) : claim);
    }
    // Add the Application User ID as the "sub" claim
    appClaims.Add(new Claim("sub", newUserId));
    appClaims.Add(new Claim("display-name", displayName));
    // If there was no name Claim, use the displayName as value
    if (appClaims.FirstOrDefault(c => c.Type == "name") is null)
    {
        appClaims.Add(new Claim("name", displayName));
    }

    // Create the new appIdentity and appPrincipal using the transformed claims
    var appIdentity = new ClaimsIdentity(appClaims, "cookie", nameType: "name", roleType: "role");
    var appPrincipal = new ClaimsPrincipal(appIdentity);

    // If the original properties are null, create a new empty set. Otherwise, use the original properties to preserve all data.
    // Here you could filter and/or add Properties at will, be sure to preserve the tokens though.
    var appProperties = idpProperties ?? new AuthenticationProperties();

    return (appPrincipal, appProperties);
}

// Sign-in to the default (App) cookie ("cookie" scheme) and also sign-out of the IDP cookie ("cookie-idp" scheme).
static async Task SwitchSignInSchemeAsync(
    HttpContext context, ClaimsPrincipal appPrincipal, AuthenticationProperties appProperties)
{
    await context.SignInAsync("cookie", appPrincipal, appProperties);
    await context.SignOutAsync("cookie-idp");
}

// Called by the OIDC sign-out handler
Task HandleOnRedirectToIdpForSignOut(RedirectContext context)
{
    // Ensure we leave the user with a clean URL in the browser, i.e., without the state parameter
    context.ProtocolMessage.PostLogoutRedirectUri = 
        new Uri(context.Request.GetBaseUri(), context.Options.SignedOutCallbackPath).ToString();
    
    return Task.CompletedTask;
}
