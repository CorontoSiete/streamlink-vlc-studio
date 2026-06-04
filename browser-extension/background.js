const CAPTURE_ENDPOINT = "http://127.0.0.1:39179/capture";

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || message.type !== "capture-stream" || typeof message.url !== "string") {
    return false;
  }

  fetch(CAPTURE_ENDPOINT, {
    method: "POST",
    headers: {
      "Content-Type": "application/json"
    },
    body: JSON.stringify({ url: message.url })
  })
    .then((response) => sendResponse({
      ok: response.ok,
      status: response.status,
      statusText: response.statusText
    }))
    .catch((error) => sendResponse({ ok: false, error: String(error) }));

  return true;
});
