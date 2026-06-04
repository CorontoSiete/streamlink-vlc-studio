const {
  channelFromUrl,
  captureStatusFromResponse,
  findChannelPointClaimElements,
  isTwitchHost
} = globalThis.StreamlinkVlcStudioContentCore;

const CHANNEL_POINT_SCAN_INTERVAL_MS = 15000;
const CHANNEL_POINT_MUTATION_DELAY_MS = 250;
const CHANNEL_POINT_CLICK_COOLDOWN_MS = 1500;
const CAPTURE_STATUS_ELEMENT_ID = "streamlink-vlc-studio-capture-status";
const CAPTURE_STATUS_SUCCESS_TIMEOUT_MS = 1800;
const CAPTURE_STATUS_ERROR_TIMEOUT_MS = 7000;

let captureStatusHideTimer = 0;

function closestAnchor(event) {
  for (const target of event.composedPath()) {
    if (target instanceof HTMLAnchorElement && target.href) {
      return target;
    }
  }

  return null;
}

function ensureCaptureStatusRoot() {
  if (!document.documentElement) {
    return null;
  }

  let host = document.getElementById(CAPTURE_STATUS_ELEMENT_ID);
  if (!host) {
    host = document.createElement("div");
    host.id = CAPTURE_STATUS_ELEMENT_ID;
    document.documentElement.append(host);

    const root = typeof host.attachShadow === "function"
      ? host.attachShadow({ mode: "open" })
      : host;
    root.innerHTML = `
      <style>
        :host {
          all: initial;
          position: fixed;
          top: 16px;
          right: 16px;
          z-index: 2147483647;
          pointer-events: none;
        }
        .toast {
          box-sizing: border-box;
          min-width: 280px;
          max-width: min(380px, calc(100vw - 32px));
          padding: 12px 14px;
          border: 1px solid #2a3646;
          border-left-width: 4px;
          border-radius: 8px;
          background: rgba(10, 14, 20, 0.96);
          color: #eef3f8;
          font-family: "Segoe UI", system-ui, sans-serif;
          font-size: 13px;
          line-height: 1.35;
          box-shadow: 0 12px 30px rgba(0, 0, 0, 0.35);
          opacity: 0;
          transform: translateY(-8px);
          transition: opacity 140ms ease, transform 140ms ease;
        }
        .toast.visible {
          opacity: 1;
          transform: translateY(0);
        }
        .toast.success {
          border-left-color: #43e486;
        }
        .toast.error {
          border-left-color: #f45b69;
        }
        .title {
          margin: 0 0 3px;
          font-size: 12px;
          font-weight: 700;
          color: #dce6ef;
        }
        .message {
          margin: 0;
          color: #eef3f8;
        }
      </style>
      <div class="toast" role="status" aria-live="polite">
        <div class="title">Twitch & Kick player</div>
        <div class="message"></div>
      </div>`;
  }

  return host.shadowRoot || host;
}

function showCaptureStatus(status) {
  const root = ensureCaptureStatusRoot();
  if (!root) {
    window.setTimeout(() => showCaptureStatus(status), 0);
    return;
  }

  const toast = root.querySelector(".toast");
  const message = root.querySelector(".message");
  if (!toast || !message) {
    return;
  }

  message.textContent = status.message;
  toast.classList.remove("success", "error", "visible");
  toast.classList.add(status.kind === "success" ? "success" : "error");
  window.requestAnimationFrame(() => toast.classList.add("visible"));

  if (captureStatusHideTimer) {
    window.clearTimeout(captureStatusHideTimer);
  }

  const timeoutMs = status.kind === "success"
    ? CAPTURE_STATUS_SUCCESS_TIMEOUT_MS
    : CAPTURE_STATUS_ERROR_TIMEOUT_MS;
  captureStatusHideTimer = window.setTimeout(() => {
    toast.classList.remove("visible");
    captureStatusHideTimer = 0;
  }, timeoutMs);
}

function sendStreamCapture(streamUrl) {
  try {
    chrome.runtime.sendMessage({ type: "capture-stream", url: streamUrl }, (response) => {
      const runtimeError = chrome.runtime.lastError;
      const status = captureStatusFromResponse(
        runtimeError ? { ok: false, error: runtimeError.message } : response,
        streamUrl);
      showCaptureStatus(status);
    });
  } catch (error) {
    showCaptureStatus(captureStatusFromResponse({ ok: false, error: String(error) }, streamUrl));
  }
}

document.addEventListener(
  "click",
  (event) => {
    if (event.defaultPrevented || event.button !== 0) {
      return;
    }

    const anchor = closestAnchor(event);
    if (!anchor) {
      return;
    }

    const streamUrl = channelFromUrl(anchor.href, window.location.href);
    if (!streamUrl) {
      return;
    }

    event.preventDefault();
    event.stopImmediatePropagation();
    sendStreamCapture(streamUrl);
  },
  true
);

const twitchChannelPointAutoClaim = (() => {
  let observer = null;
  let intervalId = 0;
  let scheduledScanId = 0;
  let lastClickAt = 0;

  function canRunOnCurrentPage() {
    return isTwitchHost(window.location.hostname);
  }

  function claimVisibleBonus() {
    if (!canRunOnCurrentPage()) {
      return false;
    }

    const now = Date.now();
    if (now - lastClickAt < CHANNEL_POINT_CLICK_COOLDOWN_MS) {
      return false;
    }

    const claimButtons = findChannelPointClaimElements(document);
    if (claimButtons.length === 0) {
      return false;
    }

    lastClickAt = now;
    claimButtons[0].click();
    return true;
  }

  function scheduleScan(delayMs = CHANNEL_POINT_MUTATION_DELAY_MS) {
    if (scheduledScanId !== 0) {
      return;
    }

    scheduledScanId = window.setTimeout(() => {
      scheduledScanId = 0;
      claimVisibleBonus();
    }, delayMs);
  }

  function startObserver() {
    if (observer || !document.documentElement || typeof MutationObserver !== "function") {
      return Boolean(observer);
    }

    observer = new MutationObserver(() => scheduleScan());
    observer.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ["aria-label", "aria-disabled", "class", "disabled", "role", "style", "title"],
      childList: true,
      subtree: true
    });

    return true;
  }

  function start() {
    if (!canRunOnCurrentPage()) {
      return;
    }

    if (!startObserver()) {
      document.addEventListener("DOMContentLoaded", start, { once: true });
    }

    scheduleScan(0);

    if (intervalId === 0) {
      intervalId = window.setInterval(() => scheduleScan(0), CHANNEL_POINT_SCAN_INTERVAL_MS);
    }
  }

  return {
    claimVisibleBonus,
    start
  };
})();

twitchChannelPointAutoClaim.start();
