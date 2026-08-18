(function (global) {
  "use strict";

  const routePolicy = global.StreamlinkVlcStudioPlatformRoutes;
  if (!routePolicy || !Array.isArray(routePolicy.twitch) || !Array.isArray(routePolicy.kick)) {
    throw new Error("Generated platform route policy was not loaded before content-core.js.");
  }

  const TWITCH_NON_CHANNEL_PATHS = new Set(routePolicy.twitch);
  const KICK_NON_CHANNEL_PATHS = new Set(routePolicy.kick);

  function normalizeHost(host) {
    let normalized = String(host || "").toLowerCase();
    if (normalized.startsWith("www.")) {
      normalized = normalized.slice(4);
    }

    if (normalized === "m.twitch.tv" || normalized === "m.kick.com") {
      normalized = normalized.slice(2);
    }

    return normalized;
  }

  function isTwitchHost(host) {
    return normalizeHost(host) === "twitch.tv";
  }

  function isTwitchChannelRoute(rawUrl) {
    const canonical = channelFromUrl(rawUrl, rawUrl);
    return canonical !== null && canonical.startsWith("https://www.twitch.tv/");
  }

  function isSupportedHttpProtocol(url) {
    return url.protocol === "http:" || url.protocol === "https:";
  }

  function hasKnownPlatformHostWithoutScheme(value) {
    const text = String(value || "").trim();
    if (!text || /^[a-z][a-z0-9+.-]*:\/\//i.test(text)) {
      return false;
    }

    try {
      const candidate = new URL(`https://${text}`);
      const normalizedHost = normalizeHost(candidate.hostname);
      return normalizedHost === "twitch.tv" || normalizedHost === "kick.com";
    } catch {
      return false;
    }
  }

  function channelFromUrl(rawUrl, baseUrl) {
    let url;
    try {
      const text = String(rawUrl || "").trim();
      if (!text) {
        return null;
      }

      url = new URL(hasKnownPlatformHostWithoutScheme(text) ? `https://${text}` : text, baseUrl);
    } catch {
      return null;
    }

    const host = normalizeHost(url.hostname);
    if (!isSupportedHttpProtocol(url)) {
      return null;
    }

    const segments = url.pathname.split("/").filter(Boolean);
    if (segments.length !== 1) {
      return null;
    }

    const channel = segments[0].replace(/^@+/, "");
    if (!/^[A-Za-z0-9_.-]{1,80}$/.test(channel)) {
      return null;
    }

    if (host === "twitch.tv") {
      if (TWITCH_NON_CHANNEL_PATHS.has(channel.toLowerCase()) || !/^[A-Za-z0-9_]{3,25}$/.test(channel)) {
        return null;
      }

      return `https://www.twitch.tv/${channel}`;
    }

    if (host === "kick.com") {
      if (KICK_NON_CHANNEL_PATHS.has(channel.toLowerCase())) {
        return null;
      }

      return `https://kick.com/${channel}`;
    }

    return null;
  }

  function platformNameFromUrl(rawUrl) {
    try {
      const url = new URL(String(rawUrl || "").trim());
      if (!isSupportedHttpProtocol(url)) {
        return "stream";
      }

      const host = normalizeHost(url.hostname);
      if (host === "twitch.tv") {
        return "Twitch";
      }

      if (host === "kick.com") {
        return "Kick";
      }
    } catch {
    }

    return "stream";
  }

  function captureStatusFromResponse(response, streamUrl) {
    const platformName = platformNameFromUrl(streamUrl);
    if (response && response.ok === true) {
      return {
        kind: "success",
        message: `Sent ${platformName} stream to Twitch & Kick player.`
      };
    }

    if (response && typeof response.status === "number" && response.status > 0) {
      return {
        kind: "error",
        message: `Twitch & Kick player rejected the ${platformName} stream link (HTTP ${response.status}).`
      };
    }

    return {
      kind: "error",
      message: "Twitch & Kick player is not running. Start the desktop app, then click the stream again."
    };
  }

  function normalizeText(value) {
    return String(value || "").replace(/\s+/g, " ").trim().toLowerCase();
  }

  function getElementLabel(element) {
    const candidates = [];
    if (typeof element.getAttribute === "function") {
      candidates.push(element.getAttribute("aria-label"));
      candidates.push(element.getAttribute("title"));
    }

    candidates.push(element.textContent);
    for (const candidate of candidates) {
      const normalized = normalizeText(candidate);
      if (normalized) {
        return normalized;
      }
    }

    return "";
  }

  function hasUsableGeometry(element) {
    if (typeof element.getClientRects === "function" && element.getClientRects().length === 0) {
      return false;
    }

    if (typeof element.getBoundingClientRect === "function") {
      const rect = element.getBoundingClientRect();
      if (rect && (rect.width <= 0 || rect.height <= 0)) {
        return false;
      }
    }

    return true;
  }

  function isVisibleElement(element) {
    if (!element || element.isConnected === false || !hasUsableGeometry(element)) {
      return false;
    }

    if (typeof global.getComputedStyle !== "function") {
      return true;
    }

    for (let current = element; current; current = current.parentElement) {
      const style = global.getComputedStyle(current);
      if (style
          && (style.display === "none"
            || style.visibility === "hidden"
            || style.visibility === "collapse"
            || Number.parseFloat(style.opacity) <= 0)) {
        return false;
      }
    }

    return true;
  }

  function isButtonLike(element) {
    if (!element || typeof element.getAttribute !== "function") {
      return false;
    }

    const tagName = String(element.tagName || "").toUpperCase();
    const role = normalizeText(element.getAttribute("role"));
    return tagName === "BUTTON" || role === "button";
  }

  function isDisabled(element) {
    if (!element || typeof element.getAttribute !== "function") {
      return true;
    }

    return element.disabled === true
      || element.getAttribute("disabled") !== null
      || normalizeText(element.getAttribute("aria-disabled")) === "true";
  }

  function isChannelPointClaimElement(element) {
    if (!isButtonLike(element) || isDisabled(element) || !isVisibleElement(element)) {
      return false;
    }

    const label = getElementLabel(element);
    return label === "claim bonus";
  }

  function addIfClaimable(results, seen, element) {
    if (!element || seen.has(element) || !isChannelPointClaimElement(element)) {
      return;
    }

    seen.add(element);
    results.push(element);
  }

  function findChannelPointClaimElements(root) {
    const results = [];
    const seen = new Set();
    if (!root) {
      return results;
    }

    addIfClaimable(results, seen, root);

    if (typeof root.querySelectorAll !== "function") {
      return results;
    }

    for (const element of root.querySelectorAll('button, [role="button"]')) {
      addIfClaimable(results, seen, element);
    }

    return results;
  }

  global.StreamlinkVlcStudioContentCore = {
    channelFromUrl,
    captureStatusFromResponse,
    findChannelPointClaimElements,
    isChannelPointClaimElement,
    isTwitchChannelRoute,
    isTwitchHost,
    platformNameFromUrl
  };
})(globalThis);
