// JS module for onboarding interactive page
export async function submitJson(dotNetRef, payload) {
  try {
    if (!payload || typeof payload !== "object") {
      await dotNetRef.invokeMethodAsync("OnServerError", "Invalid payload supplied to submitJson.");
      return;
    }

    // Obtain the antiforgery token as we POST the data as JSON, not as the Form data with the hiddenCSRF field value.
    const tokenResp = await fetch("/antiforgery/token", {
      method: "GET",
      credentials: "same-origin"
    });
    if (!tokenResp.ok) {
      await dotNetRef.invokeMethodAsync("OnServerError", "Unable to obtain antiforgery token.");
      return;
    }
    const { token } = await tokenResp.json();

    // Post JSON to /onboarding/finish
    const resp = await fetch("/onboarding/finish", {
      method: "POST",
      credentials: "same-origin",
      headers: {
        "Content-Type": "application/json",
        "RequestVerificationToken": token,      // this is the default header name expected by ASP.NET Core
        "Accept": "application/json",
        "X-Requested-With": "XMLHttpRequest"
      },
      body: JSON.stringify(payload)
    });

    const contentType = resp.headers.get("Content-Type") || "";
    let json = null;
    if (contentType.includes("application/json")) {
      json = await resp.json().catch(() => null);
    }

    if (!resp.ok) {
      // If validation payload returned, apply to form via Blazor callback
      if (json && json.validation) {
        await dotNetRef.invokeMethodAsync("ApplyServerValidationErrors", json.validation);
        return;
      }
      const msg = (json && json.message) ? json.message : "Onboarding failed with status ${resp.status}";
      await dotNetRef.invokeMethodAsync("OnServerError", msg);
      return;
    }

    // Success => redirectUrl
    const redirect = (json && json.redirectUrl) ? json.redirectUrl : "/";
    window.location.href = redirect;
  } catch (err) {
    console.error("onboarding.submitJson", err);
    try {
      await dotNetRef.invokeMethodAsync("OnServerError", err?.toString() ?? "Unknown error");
    } catch (cbErr) {
      console.error("onboarding.submitJson 'OnServerError' callback failed", cbErr);
    }
  }
}