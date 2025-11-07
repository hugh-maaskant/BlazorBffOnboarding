# Identity Replacement and New User Onboarding for Duende's BFF with Blazor Auto Rendering

This code base demonstrates two primary additions to the Duende's BFF for Blazor Auto Rendering sample:

1. Replacing the OpenID Connect Provider (IDP) `sub` claim value with an application's internal user identity upon sign-in.
2. Implementing a custom new-user onboarding login flow, including persisting new user attributes to an application database.

The solution is built to be flexible, and is pre-configured and tested to work with two different OIDC providers: 
the Duende demonstration server and Azure AD B2C.

This implementation is built on, and extends the [Blazor Auto Rendering sample](https://github.com/DuendeSoftware/Samples/tree/main/BFF/v3/BlazorAutoRendering) from the DuendeSoftware's 
Sample repository.
The inspiration for the chosen approach is taken from the [External Authentication Providers Part 2](https://www.youtube.com/watch?v=daeVaU5CmPw) 
("The Authentication Callback Pattern") YouTube video from Dominick Baier.

### Note
The Duende [Backend For Frontend (BFF) Security Framework](https://docs.duendesoftware.com/bff/) is licensed software.
According to their website:

>Duende.BFF is free for development, testing and personal projects, but production use requires a license.

## Key New Features

- **Identity Replacement:** Replaces the IDP's `sub` claim with a stable, internal application-specific user ID,
  while preserving the original IDP `sub` claim as `idp-sub`.
- **New User Onboarding Flow:** Interrupts the standard BFF sign-in process to direct new users to a Blazor page
  with an `EditForm` that securely POSTs data to the server.
- **Persistent User Store:** Implements a SQLite database to persist application user information.
- **User Administration:** Includes an Admin page for viewing and deleting user records in the database.
- **Configuration-Driven Multi-Provider Support:** Easily switch between different OIDC providers via `appsettings.json`.
  Note: Duende Demo IdentityServer has been preconfigured, Azure AD B2C has been prepared with placeholder values, see below.
- **Provider-Agnostic `sid` Polyfill:** Automatically generates a `sid` (Session ID) claim if the external IDP does
  not provide one, ensuring compatibility with Duende BFF's server-side session management.
- **Clean, Standards-Compliant Logout:** Implements a two-step OIDC logout flow that handles the `state` parameter,
  ensuring it leaves a clean URL in the browser.
- **Separated Diagnostic Endpoints (option):** Includes diagnostic endpoints (`/diag/app`, `/diag/idp`)
  to inspect claims and tokens without interfering with the primary user flow.

## Limitations

- Like the original Duende sample, no additional additional security checks like Content Security
  Policies (CSPs) or rate-limiting requests are implemented beyond the authentication of the login flows.
- All OIDC flow logic is deliberately inlined in the Host program file for ease of viewing.
  In a production setting this would likely be factored out to separate classes.
- Server-Side Validation Only on Onboarding Form. The `OnboardingForm.razor` page uses a standard HTML form POST.
  While this is simple and robust for the authentication flow, it does not perform client-side validation with retry logic.
  The `OnboardingInputModel` has validation attributes (`[Required]`, `[MinLength]`, etc.),
  which are enforced on the server.
  If validation fails, the user is logged out and redirected to the homepage with an error message.
- This has only been tested with .NET 9.0 and version 3.0 of Duende's BFF.
- To test with the Azure AD B2C provider, you will need to configure your own Client on Azure and fill in its
  properties in the `"B2C"` section of `appsettings.json`, e.g.
  ```json
    "B2C": {
      "Authority": "https://XXX.b2clogin.com/XXX.onmicrosoft.com/B2C_1_SignupAndSignin/v2.0",
      "ClientId": "Provided by Azure AD B2C",
      "ClientSecret": "Provided by Azure AD B2, Store in .NET User Secrets",
      "Scope": [ "openid", "profile", "offline_access", "The Client ID" ]
    }
  ```

## Disclaimers

- Although I tried to learn the basics of OIDC and web security, I am not a security expert. 
  Review by an expert is recommended before using this in production.
- I am a "solo-developer" and used an AI assistant (Gemini 2.5) for technical support; the code has not been reviewed by
  a human professional developer.
- All in all this is just demo software, use appropriately.

## The Challenge: New User Onboarding with BFF

The standard Duende BFF flow is designed to be seamless: the user logs in at the IDP and is immediately signed into the application.
However, real-world applications often need to intercept this process for new users to perform tasks such as:

- Creating a user record in the application database.
- Asking for additional profile information (e.g., a display name).
- Presenting terms of service and requiring consent.

This requires a two-stage sign-in process, which this sample implements.

## The Solution: A Two-Stage Sign-In Flow

The core of this solution is a two-stage sign-in process orchestrated by two different authentication cookies,
a series of OIDC event handlers, and a dedicated "/onboarding" endpoint with a Blazor "Onboarding" page and form .

1. **Initial Sign-In (`cookie-idp`):** The OIDC handler is configured with `SignInScheme = "cookie-idp"`. 
   After a user authenticates at the external IDP, they are signed into a temporary, short-lived cookie scheme.
   This cookie holds the original claims and tokens from the IDP.

2. **The `OnTicketReceived` Decision Point:** This event fires after the `cookie-idp` is created.
   Here, we perform a lookup in our application's user database.

    - **If the user exists (Returning User):** We immediately transform their claims and complete the sign-in to the
      main application cookie (`cookie`), cleaning up the temporary `cookie-idp` and redirecting them to their
      original destination.

   - **If the user is new:** We store the original intended `ReturnUrl` in the authentication properties.
     Based on a feature flag (`EnableAuthDiagnostics`), we then redirect the user to either the `/diag/idp` page
     (for debugging) or the `/onboarding` page (for the real user flow).
    
3.  **The `/onboarding` Endpoint:**
    - `GET /onboarding`: Renders a Blazor page (`OnboardingForm.razor`) containing an `<EditForm>` for the
      user to enter their information.
      This page is protected by the `cookie-idp` authorization policy.
    - `POST /onboarding`: The form posts to this endpoint.
      It authenticates the user against the `cookie-idp` scheme, creates a new user record in the database,
      transforms the claims, and completes the sign-in to the main `cookie`.
      Finally, it retrieves the original `ReturnUrl` and redirects the user to their intended destination.

## Critical Implementation Details & "Gotchas"

This project solves several issues that can arise when integrating OIDC providers with the Duende BFF.

### 1. Configuration-Driven Provider Setup

- **Problem:** Hardcoding provider details (`Authority`, `ClientId`, `Scope`, etc.) in `Program.cs` makes the
  application rigid and difficult to test against different environments or providers.
- **Solution:** All provider-specific settings are defined in `appsettings.json` under named sections (`Duende`, `B2C`).
  A top-level key, `ActiveAuthenticationScheme`, determines which provider configuration is loaded at startup:
  ```csharp
  builder.Services.AddOptions<OpenIdConnectOptions>("oidc")
      .Bind(builder.Configuration.GetSection($"Authentication:{activeScheme}"));
  ```
### 2. The `sid` (Session ID) Claim Polyfill

- **Problem:** Duende BFF's server-side session management requires a `sid` claim. Some IDPs, like Azure AD B2C, do not issue one by default, causing logout to fail.
- **Solution:** We use the `OnTokenValidated` OIDC event, which fires very early in the pipeline.
  We check if a `sid` claim exists.
  If not, we generate our own using `Guid.CreateVersion7()` and add it to the principal.
  This "polyfills" the missing claim, ensuring the BFF framework always has the data it needs.

### 3. Clean Post-Logout Redirects

- **Problem:** Some OIDC providers, like Azure AD B2C, echo back a `state` parameter on the post-logout redirect.
  This leaves a messy URL (`/?state=...`) in the address bar of the user's browser.
- **Solution:** We implement a two-step logout flow.
  In `OnRedirectToIdentityProviderForSignOut` event, we set the `PostLogoutRedirectUri` to the handler's own
  `SignedOutCallbackPath`.
  We also set `options.SignedOutRedirectUri = "/";`.
  This ensures the OIDC handler intercepts the callback, consumes the `state` parameter,
  and then performs a clean, final redirect to `"/"`.

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
   - From the `BlazorAutoRendering` project directory, run the following command, replacing `Your_Secret_Value_Here` with your actual client secret from the Azure portal:
     ```sh
     dotnet user-secrets set "Authentication:B2C:ClientSecret" "Your_Secret_Value_Here"
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
   dotnet ef migrations add InitialCreate
   dotnet ef database update
   ```

4. **Add your own Provider** (optional)**:** Add another section with a descriptive key and the required details for your Id Provider.

### Running from the Command Line

This solution contains two startup projects that must be running simultaneously: the Blazor BFF host (`BlazorAutoRendering`) and the backend API (`BlazorAutoRendering.Api`).

To run the solution from the command line, you will need to open **two separate terminal windows**.

**In Terminal 1 (Run the API):**

```sh
# Navigate to the API project directory
cd BlazorAutoRendering.Api

# Run the API project
dotnet run
```
You should see output indicating that the API is listening on `https://localhost:7001`.

**In Terminal 2 (Run the Blazor BFF Host):**

```sh
# Navigate to the Blazor host project directory
cd BlazorAutoRendering

# Run the Blazor host project
dotnet run
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
    At the bottom of the page is a URL that allows you to continue to the  onboarding form.
-   **/diag/app:** After logging in, navigate to this page to see all claims, properties, and tokens associated with
    your final application session (`cookie`).