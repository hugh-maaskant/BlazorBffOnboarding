# Identity Replacement and New User Onboarding for Duende's BFF with Blazor Auto Rendering

This demonstration code implements two primary additions to Duende's BFF for Blazor Auto Rendering sample:

1. Replace the OpenID Connect Provider (IDP) `sub` claim value with the application's internal user identity upon sign-in.
2. Implement a custom new-user onboarding login flow, including persisting new user attributes to the application database.

The solution is built to be flexible, and is pre-configured and tested to work with two different OIDC providers: 
the Duende demonstration server and Azure AD B2C.

This implementation extends the 
[Blazor Auto Rendering sample](https://github.com/DuendeSoftware/Samples/tree/main/BFF/v3/BlazorAutoRendering)
from the DuendeSoftware's Sample repository.
The inspiration for the chosen approach is taken from the [External Authentication Providers Part 2](https://www.youtube.com/watch?v=daeVaU5CmPw) 
("The Authentication Callback Pattern") YouTube video from Dominick Baier.

### Note
The Duende [Backend For Frontend (BFF) Security Framework](https://docs.duendesoftware.com/bff/) is licensed software.
According to their website:

>Duende.BFF is free for development, testing and personal projects, but production use requires a license.

## Key New Features

- **Identity Replacement:** Replaces the IDP's `sub` claim value with a stable, application-specific, user ID,
  preserving the original IDP `sub` claim as `idp-sub` during the login flow.
- **New User Onboarding Flow:** Intercepts the standard ASP.NET Core c.q. the BFF sign-in process, directing
  new users to a Blazor Onboarding page with an `EditForm` that securely POSTs the collected data to the server.
- **Configuration-Driven Multi-Provider Support:** Easily switch between different OIDC providers via `appsettings.json`.
  Note: Duende Demo IdentityServer has been preconfigured, Azure AD B2C has been prepared with placeholder values, see below.
- **Provider-Agnostic `sid` Polyfill:** Automatically generate a `sid` (Session ID) claim if the external IDP does
  not provide one, ensuring compatibility with Duende BFF's server-side session management.
- **Clean, Standards-Compliant Logout:** Implements a two-step OIDC logout flow that handles the `state` parameter,
  ensuring it leaves a clean URL in the browser.
- **Persistent User Store:** Implements a simple SQLite database to persist application user information.
- **User Administration:** Includes an Admin page for viewing and deleting user records in the database.
- **Separated Diagnostic Endpoints (option):** Includes diagnostic endpoints (`/diag/app`, `/diag/idp`)
  to inspect claims and tokens without interfering with the primary user flow.

## Limitations

- Beyond the authentication of the login flows, only limited additional security measures are implemented (see below for details).
- All OIDC flow logic is deliberately inlined in the Host program file for ease of viewing.
  In a production setting this would likely be factored out to separate classes.
- The solution has only been tested with .NET 9.0 and version 3.0 of Duende's BFF and the two specified
  identity providers. Other versions and IPDs may require adjustments.
- To test with the Azure AD B2C provider, you will need to configure your own Client on Azure and fill in its
  properties in the `"B2C"` section of `appsettings.json`, e.g.

  ```json
    "B2C": {
      "Authority": "https://XXX.b2clogin.com/XXX.onmicrosoft.com/B2C_1_SignupAndSignin/v2.0",
      "ClientId": "Provided by Azure AD B2C",
      "ClientSecret": "Provided by Azure AD B2, Store in .NET User Secrets",
      "Scope": [ "openid", "profile", "offline_access", "The Client ID value" ]
    }
  ```

- The User database and the Onboarding forms are "bare-bones", just enough to show what is possible and how to interact
  with them from the login flow.

## Disclaimers

- Although I tried to learn the basics of OIDC and web security, I am not a security expert. 
  Review by an expert is recommended before using this in production.
- I am a "solo-developer" and used AI assistants (Gemini 2.5, GPT-5-mini) for technical support; the code has not been
  reviewed by a professional human developer.
- All in all this is just demo software, use appropriately.

## The Challenge: New User Onboarding with BFF

The standard Duende BFF flow is designed to be seamless: the user logs in at the IDP and is immediately signed
into the application.
However, real-world applications often need to intercept this process for new users to perform tasks such as:

- Creating a user record in the application database.
- Asking for additional profile information (e.g., a display name as used in this demonstrator).
- Presenting terms of service and requiring consent.

This requires a two-stage sign-in process, which this sample implements.

## The Solution: A Two-Stage Sign-In Flow

The core of this solution is a two-stage sign-in process orchestrated by two different authentication cookies,
a series of OIDC event handlers, and two dedicated onboarding endpoints.

1. **Initial Sign-In (`cookie-idp`):** The OIDC handler is configured with `SignInScheme = "cookie-idp"`. 
   After a user authenticates at the external IDP, they are signed into this temporary, short-lived cookie scheme.
   The `cookie-idp` cookie holds the original claims and tokens from the IDP.

2. **The `OnTicketReceived` Decision Point:**
   This event fires after the `cookie-idp` is created.
   Here, we perform a lookup in our application's user database.

   - **If the user exists (Returning User):**
     We immediately transform their claims and complete the sign-in to the main application cookie (`cookie`),
     clean up the temporary `cookie-idp`, and redirect to the original destination.

   - **If the user is new:** We store the original intended `ReturnUrl` in the authentication properties.
     Based on a feature flag (`EnableAuthDiagnostics`), we then either redirect the user to the `/diag/idp`
     page (for debugging) or the `/onboarding/interactive` page (for the real user flow).
  
3.  **The `/onboarding/interactive` Endpoint:**
    Renders a Blazor page (`OnboardingInteractive.razor`) containing an `<EditForm>` for the
    user to enter their information (with form validation). 
    This page is protected by the `cookie-idp` authorization policy.

4.  **The `/onboarding/finish` Endpoint:**
    The OnboardingInteractive.razor posts the `OnboardingInputModel` (using JSInterop and as JSON) to this endpoint.
    It authenticates the user against the `cookie-idp` scheme, creates a new user record in the database,
    transforms the claims, and completes the sign-in to the main `cookie`.
    Finally, it retrieves the original `ReturnUrl` and passes this as return value of the POST. The Client then
    redirects the user to their intended destination.

## Some of the Issues I Encountered

This project solves several issues that I encountered during the design and implementation of the demonstrator.

### 1. Configuration-Driven Provider Setup

- **Problem:** Hardcoding provider details (`Authority`, `ClientId`, `Scope`, etc.) in `Program.cs` makes the
  application rigid and difficult to test against different environments or providers.
 
- **Solution:** All provider-specific settings are defined in `appsettings.json` under named sub-sections
  (`Duende`, `B2C`) of the new `Authentication` section.
  The `ActiveAuthenticationScheme` key in that section determines which provider configuration is loaded at startup:
- 
  ```csharp
  var activeScheme = builder.Configuration["Authentication:ActiveScheme"];
  builder.Services.AddOptions<OpenIdConnectOptions>("oidc")
      .Bind(builder.Configuration.GetSection($"Authentication:{activeScheme}"));
  ```

## 2. The `sid` (Session ID) Claim Polyfill

- **Problem:** Duende BFF's server-side session management requires a `sid` claim.
  Some IDPs, like Azure AD B2C, do not issue one by default, causing logout to fail.
 
- **Solution:** We use the `OnTokenValidated` OIDC event, which fires very early in the pipeline.
  We check if a `sid` claim exists.
  If not, we generate our own `sid` value using `Guid.CreateVersion7()` and add it to the principal.
  This "polyfills" the missing claim, ensuring the BFF framework always has the data it needs.

### 3. Clean Post-Logout Redirects

- **Problem:** Some OIDC providers, like Azure AD B2C, echo back a `state` parameter on the post-logout redirect.
  This leaves a messy URL (`/?state=...`) in the address bar of the user's browser.

- **Solution:** We implement a two-step logout flow.
  In `OnRedirectToIdentityProviderForSignOut` event, we set the `PostLogoutRedirectUri` to the handler's own
  `SignedOutCallbackPath` and set `options.SignedOutRedirectUri = "/";`.
  This ensures the OIDC handler intercepts the callback, consumes the `state` parameter,
  and then performs a clean, final redirect to `"/"`.

### 4. Adding Form-Validation to the Onboarding Form

- **Problem:** To validate the Onboarding form, it must be interactive, preferably using `@rendermode InteractiveServer`.
  But to finalize the login-flow, we need access to the `HttpContext`, which is not available to a Blazor page in the
  `InteractiveServer` render mode.

- **Solution:** Do not submit the Form as usual, but use JavaScript Interop to send a POST request to the
  `/onboarding/finish` endpoint using the `fetch` API with the data of the form encoded as JSON payload.
  Implement that endpoint as a minimal API endpoint.

For detailed implementation notes, please see [ImplementationNotes](ImplementationNotes.md)

## Security Measures

The application implements several security measures, particularly around the authentication and onboarding process.
Some come by default in ASP.NET, some in the Duende BFF, and some are added in this solution.
Consider this as a starting point only.

### Cookie Security

- Authentication cookies use the `__Host-` prefix - guarantees that they are only sent to the host that set them.
- The main application cookie (`__Host-blazor-app`) and temporary IDP cookie (`__Host-blazor-idp`) are configured with:

  - `SameSite = Lax` - required for OIDC.
  - `SecurePolicy = Always` - only set on HTTPS scheme.
  - `HttpOnly = true` - cannot be read by JavaScript.
  - `IsEssential = true` (for GDPR compliance).

- The temporary IDP cookie has a short, 15-minute, expiration.

### Request Protection

- The Server header is removed from all responses 
- On the onboarding endpoints specifically:

  - Rate limiting(max 5 requests per 15 minutes per IP address).
  - Request size validation (1KB limit).
  - Content-type validation.
  - CSRF protection through antiforgery tokens.

### Authentication Flow Security

- Protection of diagnostic endpoints through authorization policies.
- Secure redirect validation: only local URLs allowed.
- Clean post-logout URLs: removal of state parameters.

### Input Validation

- Server-side validation of the onboarding form data.
- Display name requirements:

  - Minimum length: 5 characters.
  - Maximum length: 50 characters.
  - Required field validation.
  - Only alphanumeric and limited punctuation characters are allowed.

### Security Headers

- `X-Frame-Options: DENY` - do not allow the content to be embedded in another site.
- `X-Content-Type-Options: nosniff` - content type strictly determined by the header.
- `Referrer-Policy: strict-origin-when-cross-origin` - protect user data during cross-origin requests.

For production deployments, also consider:

- Implementing additional monitoring and logging.
- Adding Content Security Policy (CSP) and Cross-Origin Resource Sharing (CORS) headers.
- Implementing a more sophisticated rate-limiting strategy.
- Adding IP-based blocking for suspicious activity.
- Adding CAPTCHA or similar mechanisms if bot abuse becomes a concern.

## Possible Extensions

No software is ever complete, and this demonstrator is no exception.
A few possible extensions could be:

- Any of the above suggestions for use in production environments.
- Better error handling (also any errors returned from an IDP) in the login flow.
- Replace the in-memory Session Store with a database backed one.
- Upgrade to .NET 10 (just released at the time of writing) and Duende BFF 4.0 (still in preview at the time of writing).

## How to Run This Sample

### Prerequisites

- .NET 9.0 SDK or later.

### Configuration

1. **Choose a Provider:** Open `appsettings.json` and set the `ActiveAuthenticationScheme` to either `"Duende"` or `"B2C"`.

2. **Set Secrets (for B2C):** The Duende demo provider works out-of-the-box. For Azure AD B2C, you must provide the necessary parameters in appsetting.json:

  ```json
  "B2C": {
    "Authority": "https://XXX.b2clogin.com/XXX.onmicrosoft.com/B2C_1_SignupAndSignin/v2.0",
    "ClientId": "your client Id",
    "ClientSecret": null,
    "Scope": [ "openid", "profile", "offline_access", "your client Id" ]
  }
  ```

 Authority is typically something in the form of `"https://XXX.b2clogin.com/XXX.onmicrosoft.com/B2C_1_SignupAndSignin/v2.0"`, where XXX is your tenant name.
  
 Note the `ClientSecret` should be set using the .NET User Secrets manager:
 - From the `BlazorBffOnboarding` project directory, run the following command, replacing `Your_Secret_Value_Here` with your actual client secret from the Azure portal:

   ```sh
   > dotnet user-secrets init
   > dotnet user-secrets set "Authentication:B2C:ClientSecret" "Your_Secret_Value_Here"
   ```
 - The hierarchical key `Authentication:B2C:ClientSecret` is essential for the configuration binder to correctly associate the secret with the B2C provider settings.

3. **Configure the User Database:** This project uses a SQLite database to store user information. The connection string is located in `appsettings.json`:
   ```json
   "ConnectionStrings": {
     "UserStore": "Data Source=users.db"
   }
   ```
   The database is managed using Entity Framework Core migrations. To create and apply the initial migration, run the following commands from the `BlazorAutoRendering` project directory:
   ```sh
   > dotnet ef migrations add InitialCreate
   > dotnet ef database update
   ```

4. **Add your own Provider** (optional)**:** Add another section with a descriptive key and the required details for your Id Provider.

### Running from the Command Line

This solution contains two startup projects that must be running simultaneously: the Blazor BFF host (`BlazorAutoRendering`) and the backend API (`BlazorAutoRendering.Api`).

To run the solution from the command line, you will need to open **two separate terminal windows**.

**In Terminal 1 (Run the API):**

```sh
> # Navigate to the API project directory
> cd BlazorAutoRendering.Api

> # Run the API project
> dotnet run
```
You should see output indicating that the API is listening on `https://localhost:7001`.

**In Terminal 2 (Run the Blazor BFF Host):**

```sh
> # Navigate to the Blazor host project directory
> cd BlazorAutoRendering

> # Run the Blazor host project
> dotnet run
```

The application will launch.
Open a browser tab on `https://localhost:7035`. The Blazor application is now running and can make calls to the BFF (the /weather page) and the backend API (the /greeting page).

### Running from an IDE

Most modern IDEs (like Visual Studio, JetBrains Rider, or VS Code) can be configured to launch multiple startup projects. Please consult your IDE's documentation for instructions on how to set up a "Compound" or "Multiple Startup Projects" launch configuration that runs both the `BlazorAutoRendering` and `BlazorAutoRendering.Api` projects.

### Using the Diagnostic Endpoints

To aid in debugging and integration, this sample includes diagnostic pages. To enable them, add the following to your `appsettings.Development.json`:

```json
{
  "EnableAuthDiagnostics": true
}
```
-   **/diag/idp:** When the new user flow is triggered with diagnostics enabled, you will be sent to this page to see 
    the claims, properties, and tokens associated with the temporary `cookie-idp`.
    At the bottom of the page is a link that allows you to continue to the Onboarding form.

-   **/diag/app:** After logging in, navigate to this page to see all claims, properties, and tokens associated with
    your final application session (`cookie`).


## License and Attribution

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.

This project is an extension of a demo project from Duende Software, which is also licensed under the MIT License.
The original software can be found at: https://github.com/DuendeSoftware/Samples/tree/main/BFF/v3/BlazorAutoRendering.