const {
  channelFromUrl,
  captureStatusFromResponse,
  findChannelPointClaimElements,
  isTwitchChannelRoute,
  isTwitchHost
} = globalThis.StreamlinkVlcStudioContentCore;

const CHANNEL_POINT_SCAN_INTERVAL_MS = 15000;
const CHANNEL_POINT_ROUTE_CHECK_INTERVAL_MS = 1000;
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
    if (!event.isTrusted ||
        event.defaultPrevented ||
        event.button !== 0 ||
        event.ctrlKey ||
        event.metaKey ||
        event.shiftKey ||
        event.altKey) {
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
  let fallbackScanIntervalId = 0;
  let routeCheckIntervalId = 0;
  let scheduledScanId = 0;
  let lastClickAt = 0;
  let started = false;
  let active = false;
  let awaitingDocumentElement = false;
  const pendingScanRoots = new Set();

  function canRunOnCurrentPage() {
    return isTwitchHost(window.location.hostname)
      && isTwitchChannelRoute(window.location.href);
  }

  function claimVisibleBonus(root = document) {
    if (!canRunOnCurrentPage()) {
      return false;
    }

    const now = Date.now();
    if (now - lastClickAt < CHANNEL_POINT_CLICK_COOLDOWN_MS) {
      return false;
    }

    const claimButtons = findChannelPointClaimElements(root);
    if (claimButtons.length === 0) {
      return false;
    }

    lastClickAt = now;
    claimButtons[0].click();
    return true;
  }

  function isScannableRoot(root) {
    return root === document
      || (root && typeof root.querySelectorAll === "function");
  }

  function enqueueScanRoot(root) {
    if (isScannableRoot(root)) {
      pendingScanRoots.add(root);
    } else if (isScannableRoot(root?.parentElement)) {
      pendingScanRoots.add(root.parentElement);
    }
  }

  function runScheduledScan() {
    scheduledScanId = 0;
    if (!active || !canRunOnCurrentPage()) {
      refreshRoute();
      return;
    }

    const roots = [...pendingScanRoots];
    pendingScanRoots.clear();
    for (const root of roots) {
      if (claimVisibleBonus(root)) {
        break;
      }
    }
  }

  function scheduleScan(root, delayMs = CHANNEL_POINT_MUTATION_DELAY_MS) {
    if (root !== undefined) {
      enqueueScanRoot(root);
    }

    if (pendingScanRoots.size === 0) {
      return;
    }

    if (scheduledScanId !== 0) {
      return;
    }

    scheduledScanId = window.setTimeout(runScheduledScan, delayMs);
  }

  function handleMutations(mutations) {
    refreshRoute();
    if (!active) {
      return;
    }

    for (const mutation of mutations) {
      if (mutation.type === "childList") {
        for (const node of mutation.addedNodes || []) {
          enqueueScanRoot(node);
        }
      } else if (mutation.type === "attributes") {
        enqueueScanRoot(mutation.target);
      }
    }

    if (pendingScanRoots.size > 0) {
      scheduleScan(undefined, CHANNEL_POINT_MUTATION_DELAY_MS);
    }
  }

  function startObserver() {
    if (observer || !document.documentElement || typeof MutationObserver !== "function") {
      return Boolean(observer);
    }

    observer = new MutationObserver(handleMutations);
    observer.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ["aria-label", "aria-disabled", "class", "disabled", "role", "style", "title"],
      childList: true,
      subtree: true
    });

    return true;
  }

  function startFallbackScan() {
    if (fallbackScanIntervalId === 0) {
      fallbackScanIntervalId = window.setInterval(
        () => scheduleScan(document, 0),
        CHANNEL_POINT_SCAN_INTERVAL_MS);
    }
  }

  function onDocumentReady() {
    awaitingDocumentElement = false;
    if (!started || !canRunOnCurrentPage()) {
      return;
    }

    activate();
  }

  function activate() {
    if (active && observer) {
      return;
    }

    active = true;
    if (!startObserver()) {
      if (!awaitingDocumentElement) {
        awaitingDocumentElement = true;
        document.addEventListener("DOMContentLoaded", onDocumentReady, { once: true });
      }

      return;
    }

    awaitingDocumentElement = false;
    scheduleScan(document, 0);
    startFallbackScan();
  }

  function deactivate() {
    active = false;
    pendingScanRoots.clear();
    observer?.disconnect();
    observer = null;

    if (scheduledScanId !== 0) {
      window.clearTimeout(scheduledScanId);
      scheduledScanId = 0;
    }

    if (fallbackScanIntervalId !== 0) {
      window.clearInterval(fallbackScanIntervalId);
      fallbackScanIntervalId = 0;
    }

    if (awaitingDocumentElement) {
      document.removeEventListener("DOMContentLoaded", onDocumentReady);
      awaitingDocumentElement = false;
    }
  }

  function refreshRoute() {
    if (!started) {
      return false;
    }

    if (canRunOnCurrentPage()) {
      activate();
      return true;
    }

    deactivate();
    return false;
  }

  function handlePageHide(event) {
    stop();
    if (event.persisted === true) {
      window.addEventListener("pageshow", start, { once: true });
    }
  }

  function start() {
    window.removeEventListener("pageshow", start);
    if (started) {
      return;
    }

    started = true;
    window.addEventListener("popstate", refreshRoute);
    window.addEventListener("hashchange", refreshRoute);
    window.addEventListener("pagehide", handlePageHide, { once: true });
    routeCheckIntervalId = window.setInterval(
      refreshRoute,
      CHANNEL_POINT_ROUTE_CHECK_INTERVAL_MS);
    refreshRoute();
  }

  function stop() {
    window.removeEventListener("pageshow", start);
    if (!started) {
      return;
    }

    started = false;
    deactivate();
    if (routeCheckIntervalId !== 0) {
      window.clearInterval(routeCheckIntervalId);
      routeCheckIntervalId = 0;
    }

    window.removeEventListener("popstate", refreshRoute);
    window.removeEventListener("hashchange", refreshRoute);
    window.removeEventListener("pagehide", handlePageHide);
  }

  return {
    claimVisibleBonus,
    isActive: () => active,
    refreshRoute,
    start,
    stop
  };
})();

globalThis.StreamlinkVlcStudioContentController = twitchChannelPointAutoClaim;
twitchChannelPointAutoClaim.start();
