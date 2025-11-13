// @ts-nocheck
// JS module for onboarding interactive page (JS isolation friendly)

/**
 * Submit form as JSON to /onboarding/finish and map server validation back into Blazor.
 * @param {any} dotNetRef DotNetObjectReference from Blazor
 * @param {string} formId id of the <form> element
 */
export async function submitJson(dotNetRef, formId) {
  try {
    const form = document.getElementById(formId);
    /** @type {Record<string,string>} */
    const payload = {};

    if (!form || !(form instanceof HTMLFormElement)) {
      if (dotNetRef && dotNetRef.invokeMethodAsync) {
        await dotNetRef.invokeMethodAsync('OnServerError', 'Form element not found.');
      }
      return;
    }

    // Serialize form fields to a plain object
    const fd = new FormData(form);
    for (const [k, v] of fd.entries()) {
      payload[k] = (typeof v === 'string') ? v : String(v);
    }

    // 1) Obtain antiforgery token
    const tokenResp = await fetch('/antiforgery/token', {
      method: 'GET',
      credentials: 'same-origin'
    });
    if (!tokenResp.ok) {
      const msg = 'Unable to obtain antiforgery token.';
      if (dotNetRef && dotNetRef.invokeMethodAsync) await dotNetRef.invokeMethodAsync('OnServerError', msg);
      return;
    }
    const tokenJson = await tokenResp.json().catch(() => null);
    const token = tokenJson?.token;
    if (!token) {
      const msg = 'Antiforgery token not found.';
      if (dotNetRef && dotNetRef.invokeMethodAsync) await dotNetRef.invokeMethodAsync('OnServerError', msg);
      return;
    }

    console.debug("Onboarding submit payload:", payload);
    console.debug("RequestVerificationToken:", token);

    // 2) POST JSON to /onboarding/finish
    const resp = await fetch('/onboarding/finish', {
      method: 'POST',
      credentials: 'same-origin',
      headers: {
        'Content-Type': 'application/json',
        'RequestVerificationToken': token,
        'Accept': 'application/json',
        'X-Requested-With': 'XMLHttpRequest'
      },
      body: JSON.stringify(payload)
    });

    const contentType = resp.headers.get('Content-Type') || '';
    const json = contentType.includes('application/json') ? await resp.json().catch(() => null) : null;

    if (!resp.ok) {
      if (json && typeof json === 'object' && json.validation) {
        if (dotNetRef && dotNetRef.invokeMethodAsync) {
          await dotNetRef.invokeMethodAsync('ApplyServerValidationErrors', json.validation);
        }
        return;
      }

      const msg = (json && json.message) ? json.message : `Onboarding failed with status ${resp.status}`;
      if (dotNetRef && dotNetRef.invokeMethodAsync) await dotNetRef.invokeMethodAsync('OnServerError', msg);
      return;
    }

    // Success -> redirect
    const redirect = (json && json.redirectUrl) ? json.redirectUrl : '/';
    window.location.href = redirect;
  } catch (err) {
    console.error('onboarding.submitJson', err);
    try {
      if (dotNetRef && dotNetRef.invokeMethodAsync) await dotNetRef.invokeMethodAsync('OnServerError', String(err));
    } catch { /* swallow */ }
  }
}