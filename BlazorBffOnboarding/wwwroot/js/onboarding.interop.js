// JS module for onboarding interactive page (JS isolation friendly)
// export submitJson(dotNetRef, formId)
export async function submitJson(dotNetRef, formId) {
  try {
    const form = document.getElementById(formId);
    if (!form) {
      await dotNetRef.invokeMethodAsync("OnServerError", "Form element not found.");
      return;
    }

    // Serialize form to simple object
    const fd = new FormData(form);
    const payload = {};
    for (const [k, v] of fd.entries()) {
      payload[k] = v;
    }

    // Obtain antiforgery token (cookie is stored and request token returned)
    const tokenResp = await fetch("/antiforgery/token", {
      method: "GET",
      credentials: "same-origin"
    });
    if (!tokenResp.ok) {
      await dotNetRef.invokeMethodAsync("OnServerError", "Unable to obtain antiforgery token.");
      return;
    }
    const tokenJson = await tokenResp.json();
    const token = tokenJson?.token;

    // Post JSON to /onboarding/finish
    const resp = await fetch("/onboarding/finish", {
      method: "POST",
      credentials: "same-origin",
      headers: {
        'Content-Type': "application/json",
        'RequestVerificationToken': token,
        'Accept': "application/json",
        'X-Requested-With': "XMLHttpRequest"
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
      const msg = (json && json.message) ? json.message : ("Onboarding failed with status " + resp.status);
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
    } catch { }
  }
}