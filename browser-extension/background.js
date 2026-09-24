const CAPTURE_ENDPOINT = "http://127.0.0.1:39179/capture";
const CAPTURE_TIMEOUT_MS = 5000;

if (typeof importScripts === "function") {
  importScripts("platform-routes.generated.js", "content-core.js");
}

const channelFromUrl = globalThis.StreamlinkVlcStudioContentCore?.channelFromUrl;

function isTrustedSender(sender) {
  // Only this extension's own content script, running in a tab on a supported platform host,
  // may ask the desktop app to open a stream.
  if (!sender || sender.id !== chrome.runtime.id || !sender.tab) {
    return false;
  }

  const isTwitchHost = globalThis.StreamlinkVlcStudioContentCore?.isTwitchHost;
  const platformNameFromUrl = globalThis.StreamlinkVlcStudioContentCore?.platformNameFromUrl;
  if (typeof isTwitchHost !== "function" || typeof platformNameFromUrl !== "function") {
    return false;
  }

  const origin = typeof sender.origin === "string" && sender.origin
    ? sender.origin
    : sender.url;
  const platform = platformNameFromUrl(origin);
  return platform === "Twitch" || platform === "Kick";
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || message.type !== "capture-stream" || typeof message.url !== "string") {
    return false;
  }

  if (!isTrustedSender(sender)) {
    sendResponse({
      ok: false,
      status: 403,
      statusText: "Forbidden",
      error: "Capture requests must come from a supported platform tab"
    });
    return true;
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

  let responded = false;
  const respondOnce = (payload) => {
    if (responded) {
      return;
    }

    responded = true;
    sendResponse(payload);
  };

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
    .then(
      (response) => respondOnce({
        ok: response.ok,
        status: response.status,
        statusText: response.statusText
      }),
      (error) => respondOnce({ ok: false, error: String(error) }))
    .finally(() => clearTimeout(timeoutId));

  return true;
});
