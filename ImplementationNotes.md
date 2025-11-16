# BlazorBffOnboarding Implementation Notes

Autor: Hugh Maaskant
<br />
Date: 2025-11-16
<br />
Status: Draft

This note contains background and details on some of the design / code decissions made for the `BlazorBffOnboarding`
demonstrator.
There is some overlap with the [README](README.md) file, and it is assumed that the reader has familiarity with its content.

## Changes made to the Duende `BlazorAutoRendering` sample

The `BlazorBffOnboarding` demonstrator is derived from Duende's `BlazorAutoRendering` sample found here:
https://github.com/DuendeSoftware/Samples/tree/main/BFF/v3/BlazorAutoRendering.

The main changes are:

### Generic

- Rename the directories, the solution and project files, and namespaces.
- Replace the solution and project GUIDs.
- Upgrade to the latest .NET 9 release packages (per 2025-11-10).

### `BlazorBffOnboarding` project

This project serves multiple roles: it (1) hosts the Blazor Client, (2) hosts the BFF, (3) provides Server rendered Blazor
pages, and (4) implements initialization and configuration for the solution.
This is also where most of the changes have occurred.

#### Existing Files

- `appsettings.js` and `appsettings.Development.js` - added sections for `Authentication`, `ConnectionStrings`, 
  and the `EnableAuthDiagnostics` feature flag.
- `Program.cs` added service registrations, updated the `builder.Services.AddAuthentication` options, added
  middleware and endpoints. See below for details.

#### New Files

- `OnboardingInputModel` - the model for the data collected during onboarding.
- `AppUser` folder - the C# model for the AppUser database entries and a service to access the database.
- `Components/Diagnostics` folder - Razor pages for the IDP and App diagnostics pages with information (Claims, Properties,
  and Tokens) from the created principal.
- `Components/FormValidation/SubmitOnlyDataAnnotationsValidator.razor` - a form validator that only validates upon a
  submit. This to avoid showing validations on every keystroke in the input field.
- `Components/Pages/OnboardingInteractive.razor` - Razor page with the onboarding form.
- `Components/Pages/UserAdmin` - Razor page to view and delete `AppUser` entries in the database.
- `Persistence/ApplicationDbContext.cs` - the EF Core `DbContext` definition for the `AppUser` database.
  __Note__: Because `sub` claims are only unique within a given IDP, there is a `unique` constraint on the
  (IdpName, IdpSubject) tuple.
- `wwwroot/js/onboarding.interop.js` - a JavaScript file for submitting the `OnboardingInputModel` to the 
  `/onboarding/finish` minimal API endpoint.

### `BlazorBffOnboarding.Api` project

Other than the name and GUID changes described above no changes were made.

### `BlazorBffOnboarding.Client` project

Only minimal changes were made to this project

- `Layout/NavMenu.razor` - Added menu entries for `User Admin` and (conditinally upon `EnableAuthDiagnostics` being `true`)
  `Diagnostics`.
- `Pages/Home`
  - Added logic for displaying an error message through query parameters. This could (but currently is not) be used by
    the login flow to show error messages when something goes wrong.
  - Changed the \<H1\> text to include "Onboarding".
  - Added displaying the value of the `display-name` Claim for authenticated users.

## Intercepting the Login flow

We need to intercept the login flow from the IDP in order to:

1. Replace the IDP's cookie with our own, including changing the `sub` Claim value to the App UserId.
2. Redirect to an Onboarding page to gather additional information about a user.

As described in the [README](README.md) file:

> The core of this solution is a two-stage sign-in process orchestrated by two different authentication cookies,
> a series of OIDC event handlers, and two dedicated onboarding endpoints.

For the global description see the [README](README.md) file.
Below, the details are described per major element of the server's `Program.cs` file.

### `builder.Services.AddAuthentication(options =>`

- Add a second `AddCookie` with "cookie-idp" as the scheme name and an `ExpireTimeSpan` of just 15 minutes,
  which should be sufficient to fill out the Onboarding form.

- Set the `options.SignInScheme = "cookie-idp";` and `options.SignOutScheme = "cookie";` 
  so the OIDC handler uses the temporary "cookie-idp" scheme for sign-in and the main "cookie" 
  scheme for sign-out.

- Add EventHandlers:

  - `options.Events.OnTokenValidated = HandleOnTokenValidatedEvent;`
  - `options.Events.OnTicketReceived = HandleOnTicketReceivedEvent;`
  - `options.Events.OnRedirectToIdentityProviderForSignOut = HandleOnRedirectToIdpForSignOut;`

  For a description of the various events that you can subscribe to in the OIDC handlers, see e.g.
  [OIDC Handler Events](https://docs.duendesoftware.com/identityserver/fundamentals/openid-connect-events/).
  
### `HandleOnTokenValidatedEvent`

Called by the OIDC signin handler after the ID token has been validated and an AuthenticationTicket has been created.

This event handler only checks for the existence of a `sid` (SessionId) Claim, and if not available generates one because
Duende's [Server-Side Sessions](https://docs.duendesoftware.com/bff/fundamentals/session/server-side-sessions/) 
subsystem needs a SessionId as key to the `IUserSessionStore`.

### `HandleOnTicketReceivedEvent`

Called by the OIDC signin handler after the OpenID Connect authentication flow is complete, 
including getting additional Claims from a 
[UserInfo Endpoint](https://docs.duendesoftware.com/identityserver/reference/endpoints/userinfo/) at the IDP if used,
but before the authentication ticket is returned.

In it we:

- First check for a `ClaimsPrincipal` on the `TicketReceivedContext`, if not available throw an `InvalidOperationException`.
- Check if the `ReturnUri` is a local Url to avoid open redirect attack, if not fall back to "/".
- Check for the existence of the `sub` claim, if not available throw an `InvalidOperationException`.
- Use the `IAppUserService` to look the `appUser` up in the AppUser database.
- If found, we have a returning user:

  - Get the `appUser.Id` and `appUser.DisplayName`
  - Call the `TransformPrincipal` helper method to transform the authentication information to a new 
    `(ClaimsPrincipal, AuthenticationProperties)` tuple.
  - Use these to sign the user in to the default "cookie" scheme and out of the "idp-cookie" scheme in the
    `SwitchSignInSchemeAsync` helper method.
  - Tell the middleware we have taken over the responsability for signing in the user with `context.HandleResponse();`
  - Redirect to the sanitized `returnUrl`.
  
  This all happens without any UI updates other than the visible redirect at the end.

- If the `appUser` is not found in the database, we have a first logon and need to start onboarding:

  - Preserve the value of the `returnUrl` in `context.Properties.Items["ultimateReturnUrl"]`.
  - Set the `context.ReturnUri` to either "/diag/idp" or "/onboarding/interactive" (based on the feature flag value).
  - Return to the OIDC sign-in handler that called us, it will trigger the redirect, ultimately ending up at the
    Onboarding Blazor page with the form to be filled out.

### `HandleOnboardingFinishPostRequest`

This is the POST handler for the "/onboarding/finish" minimal API endpoint that the Onboarding Blazor page redirects
to upon a valid submit of the form.
The form interaction is described in detail below in the section
[Interactive Onboarding](#interactive-onboarding).

In it we:

- Ensure we are authenticated with the IDP, as we are still under the temporary "cookie-idp" scheme.
- Validate the anti-forgery token.
- Validate the `ContentLength`, `ContentType`, and proper deserialization of the JSON encoded `OnboardingInputModel`.
- Re-validate the content of the `OnboardingInputModel`, possibly including additional checks such as
  duplicate DisplayName or email address detection.
- If any of the above checks fail, we return a JSON result with as data `{ success = false }` with eiter a "validation" or
  a "message" property and with `statusCode` value of `400` or `401` as appropriate.

  At this point the preconditions have been met, and we enter a try-catch block because any exception now is a
  Server Error with status code `500`.

- Get the `sub` value from the Claims, if not found throw `InvalidOperationException`.
- Create the new `AppUser` in the database, passing in, the `displayName` retrieved from the `OnboardingInputModel`.
- Like for the returning user, call the `TransformPrincipal` helper method.
- Retrieve and clean-up the "ultimateReturnUrl" value from the authentication `Properties`.
- Like for the returning user, call the `SwitchSignInSchemeAsyn` helper method to "switch cookies".
- Return a `Results.Json(new { success = true, redirectUrl = ultimateReturnUrl ?? "/" })`.
  This allows the Client to navigate to the `ultimateReturnUrl`, thereby breaking the circuit that is still under
  the old "cookie-idp" scheme  .

### `TransformPrincipal`

This is a helper method that is used both in the case of a returning user and in the case of a new user.

It:

- Copies the Claims from the `idpPrincipal` to the `appPrincipal`, but in doing so renames the `sub` claim to `sub-idp`.
- Adds the `sub` Claim with the app specific `appUserId` as well as the `display-name` Claim.
- Checks of there is a `name` Claim and if not adds it using the `displayName` as value.
- Reuses the existing `idpProperties` as `appProperties`. Note this is where the various tokens from the IDP were stored.
- Returns the `(appPrincipal, appProperties)` tuple to the caller.

__Note__: These are just examples of what could be done.

### `SwitchSignInSchemeAsync`

This is a helper method that is used both in the case of a returning user and in the case of a new user.

It signs the user in to the default "cookie" scheme with the (new) `appPrincipal` and `appProperties` and immediately 
signs the user out of the "cookie-idp" scheme.

## Interactive Onboarding

This project includes an interactive onboarding experience that keeps the onboarding page inside the Blazor layout.
It constitures a major part of the changes and arguably is the most complex part, with a mixture of OIDC flow, 
Blazor interactivity and JavaScript logic.
It is triggered by the redirect initiated in [HandleOnTicketReceivedEvent](#handleonticketreceivedevent) and finalizes by issuing
a POST request to "/onboarding/finalize", which is processed in
[HandleOnboardingFinishPostRequest](#handleonboardingfinishpostrequest).

### User visible behaviour

- A new App user signs in at the IDP.
- The App login flow routes them to `/onboarding/interactive`, which contains an Onboarding form.
- The user fills out the Onboarding form; in this demonstrator that is just a DisplayName field.
- Clicking Submit validates in Blazor land; if OK, a JSON POST is sent to the server "/onboarding/finish" minimal API
  endpoint (with antiforgery protection).
- If server (re-)validation fails, errors are shown inline and user corrects and resubmits (single click).
- On success, the server performs the cookie switch and returns a redirect URL; the JavaScript navigates there.

### The Challenge

For the Onboarding UI, I wanted to:

1. Have it fit in the App's Layout, hence a Blazor page.
2. Have it be interactive, so validation errors show up on submit, hence Interactive Server rendermode.

But the "cookie switch" needs to happen in an HTTP response, so that the server can set and remove the Auth cookies.
This cannot be reliably done from a Blazor server component because the SignalR circuit is not an HTTP 
response/response writer.
Therefore, we cannot trigger the cookie switch from the Blazor form's `OnValidSubmit`.
Still, it would be advantageous to leverage the form validation logic and visual feedback capabilities that
come with an `EditForm` control.
As a second challange, the standard `DataAnnotationsValidator` validates on every keystroke, turning the entry 
field for the Display Name field invalid (due to the minimum length constraint) upon the first character entered;
that is ugly and a bad user experience.

The solution to both issues is found by using a custom validator (`SubmitOnlyDataAnnotationsValidator.razor`), and having the
valid submit be handled in a small JavaScript module (`onboarding.interop.js`) that POSTs to a 
minimal API endpoint at the "/onboarding/finish" route, outside the Blazor router's domain.

The step by step details are outlined below on a component by component basis.

    Intermezzo: .NET 10 to the resque?

    In the not yet released (at the time of writing) .NET 10 Blazor Security documentation,
    they mention to use an IHttpContextAccessor to get the context established at the time
    the circuit is started. They also mention the option to:

> Use a `CircuitHandler` to capture a user from the `AuthenticationStateProvider` and set the user in a service.
> If you want to update the user, register a callback to `AuthenticationStateChanged` and enqueue a `Task` 
> to obtain the new user and update the service.

    That may be a solution to keep everything in Blazor code and handle the cookie-transfer
    in the Form's OnvalidSubmit without the fallback to JavaScript and a MinimalApi endpoint. 
    Still then we would need to break the circuit and force a full page reload.
    
    I have not (yet) tried this though.

### Blazor page: `Components/Pages/OnboardingInteractive.razor`

- Renders in Interactive Server mode.
- Imports the "/wwwroot/js/onboarding.interop.js" in `OnAfterRenderAsync` upon first render of the page.
- Uses an `EditForm` to obtain any additional onboarding information.
  In this demonstrator that is just a "DisplayName" field, but it could include approval of terms and conditions
  and many other things.
- Updates the bound model on every keystroke using an `@oninput` handler (accepts `ChangeEventArgs`)
  and calls `_editContext.NotifyFieldChanged(...)` so validation sees the latest value immediately
  and can act upon the input if so desired.

  __Note__: This is where you could e.g. check for duplicate `DisplayName` values in the database.
  However, remember we are on a circuit, so you need to inject a `DbContextFactory` and create/dispose
  the `DbContext` for each operation to avoid memory leaks.

- Uses an explicit `OnSubmit` lambda so validation and the client POST happen as a result of a single click.
- `HandleSubmit` runs `_editContext.Validate()` and only calls JavaScript when valid.
- When valid, the `HandleSubmit`'s `InvokeVoidAsync("submitJson", _dotNetRef, Model)` encodes the `Model` as
  JSON and transfers control to `onboarding.interop.js` for POSTing to the server.
- If the POST is successful, we do not return here, but on a client side Exception or a Server side validation
  error, we do and then display the error(s) in the Validation fields.
- Exposes [JSInvokable] methods such as `ApplyServerValidationErrors(Dictionary<string,string[]>)`
  and `OnServerError(string)` so `onboarding.interop.js` can report server-side errors or validation failures
  back to the page.
- Maintains a `ValidationMessageStore` so server-side errors and validation failures can be
  injected into the normal Blazor validation UI.

__Note__: Because users have no real way to deal with server errors on the Onboarding page, it may be better
to only return the validation failures and on the server  treat errors differently, e.g. by 
redirecting to the Home page (or a dedicated error page) with the error message in a query parameter.
The Home page implementation has this capability already built in, but as this is mostly a UI preference issue only,
I have not made the switch (yet).

### `wwwroot/js/onboarding.interop.js` JavaScript module

This provides the glue between the Onboarding Razor page and the "/onboarding/finish" POST endpoint in the server.

- Receives the Form's `OnboardingInputModel` as a JSON object in a parameter.
- Fetches an antiforgery request token from `GET /antiforgery/token` using `credentials: 'same-origin'` 
   so cookies are sent.
   This is needed because we are not using a form with the hidden antiforgery field in it, but send JSON instead.
- POST the JSON model to `POST /onboarding/finish` with header `RequestVerificationToken` and the token value.
- If the server returns validation errors, the JavaScript calls back into Blazor via a 
  `DotNetObjectReference` method `ApplyServerValidationErrors` with a dictionary of field -> `string[]`.
  This provides inline display of server-side validation results mapped into the Blazor `EditForm`
  via the `ValidationMessageStore`.
- On success, we are now authenticated with the App cookie and JavaScript redirects to the `redirectUrl` 
  returned in the POST result.
  This breaks the circuit (with the idp-cookie), and ensures the `OnboardingInteractive.razor` page will
  be properly disposed off.

### `GET /antiforgery/token`

Stores and returns the antiforgery request token, which requires an authenticated user with the "cookie-idp" cookie.

### `POST /onboarding/finish`

Finalizes the Onboarding by, a.o., storing the new appUser in the database and signing them in with the default
"cookie" scheme.
See [HandleOnboardingFinishPostRequest](#handleonboardingfinishpostrequest) above for the details.

## References

These are some of the resources I used to better understand OpenID Connect, the ASP.NET Core implementation,
and the Duende BFF Security Framework.

- Tore Nestenius (Datakonsult AB): [OpenID Connect for Developers](https://tn-data.se/openid-connect/).
- Nate Barbettini (on YouTube): 
  [OAuth 2.0 and OpenID Connect (in plain English)](https://www.youtube.com/watch?v=996OiexHze0).


- Dominick Baier's presentations on YouTube:
  - [Introduction to ASP.NET Core Authentication and Authorization](https://www.youtube.com/watch?v=02Yh3sxzAYI) (Roll your own login/logout).
  - [External Authentication Providers Part 1](https://www.youtube.com/watch?v=tjsfav3FEls) (Signing in with Google).
  - [External Authentication Providers Part 2](https://www.youtube.com/watch?v=daeVaU5CmPw) ("The Authentication Callback Pattern")
  - [Using OpenID Connect for Authentication](https://www.youtube.com/watch?v=sRb0UfLeOVw).
- Microsoft ASP.NET Core:
  [Cookie Authentication Documentation](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie).
- Microsoft ASP.NET Core: 
  [Mapping, customizing, and transforming claims](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/claims?view=aspnetcore-9.0)
  in ASP.NET Core.
- Microsoft Learn:
  [Configure OpenID Connect Web (UI) authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-oidc-web-authentication?view=aspnetcore-9.0) 
  in ASP.NET Core.
- Microsoft Azure: Web sign in with OpenID Connect in
  [Azure Active Directory B2C](https://docs.microsoft.com/en-us/azure/active-directory-b2c/openid-connect).


- Duende: [Backend For Frontend (BFF) Security Framework](https://docs.duendesoftware.com/bff/).
- Tore Nestenius: [Implementing BFF Pattern in ASP.NET Core for SPAs](https://nestenius.se/net/implementing-bff-pattern-in-asp-net-core-for-spas/).
