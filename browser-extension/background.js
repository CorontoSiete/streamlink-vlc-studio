const CAPTURE_ENDPOINT = "http://127.0.0.1:39179/capture";
const CAPTURE_TIMEOUT_MS = 5000;

if (typeof importScripts === "function") {
  importScripts("platform-routes.generated.js", "content-core.js");
}

const channelFromUrl = globalThis.StreamlinkVlcStudioContentCore?.channelFromUrl;

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || message.type !== "capture-stream" || typeof message.url !== "string") {
    return false;
  }

  const canonicalUrl = typeof channelFromUrl === "function"
    ? channelFromUrl(message.url)
    : null;
  if (!canonicalUrl || message.url !== canonicalUrl) {
    sendResponse({
      ok: false,
      status: 400,
      statusText: "Bad Request",
      error: "Invalid or non-canonical live stream URL"
    });
    return true;
  }

  const controller = new AbortController();
  const timeoutId = setTimeout(() => controller.abort(), CAPTURE_TIMEOUT_MS);
  fetch(CAPTURE_ENDPOINT, {
    method: "POST",
    headers: {
      "Content-Type": "application/json"
    },
    body: JSON.stringify({ url: canonicalUrl }),
    signal: controller.signal
  })
    .then((response) => sendResponse({
      ok: response.ok,
      status: response.status,
      statusText: response.statusText
    }))
    .catch((error) => sendResponse({ ok: false, error: String(error) }))
    .finally(() => clearTimeout(timeoutId));

  return true;
});
