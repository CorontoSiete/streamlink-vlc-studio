const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

function loadCore() {
  const context = {
    URL,
    globalThis: null,
    getComputedStyle: element => element.style
  };
  context.globalThis = context;
  vm.createContext(context);

  const source = fs.readFileSync(path.join(__dirname, "..", "content-core.js"), "utf8");
  vm.runInContext(source, context, { filename: "content-core.js" });

  return context.StreamlinkVlcStudioContentCore;
}

function loadBackground(overrides = {}) {
  let messageListener = null;
  const context = {
    AbortController,
    clearTimeout: overrides.clearTimeout || clearTimeout,
    fetch: overrides.fetch || (() => Promise.reject(new Error("fetch was not configured"))),
    setTimeout: overrides.setTimeout || setTimeout,
    chrome: {
      runtime: {
        onMessage: {
          addListener(listener) {
            messageListener = listener;
          }
        }
      }
    }
  };
  vm.createContext(context);

  const source = fs.readFileSync(path.join(__dirname, "..", "background.js"), "utf8");
  vm.runInContext(source, context, { filename: "background.js" });
  assert.equal(typeof messageListener, "function");
  return messageListener;
}

class FakeElement {
  constructor(tagName, options = {}) {
    this.tagName = tagName.toUpperCase();
    this.attributes = { ...(options.attributes || {}) };
    this.children = [];
    this.disabled = options.disabled === true;
    this.isConnected = options.isConnected !== false;
    this.textContent = options.textContent || "";
    this.visible = options.visible !== false;
    this.style = {
      display: this.visible ? "block" : "none",
      visibility: "visible",
      opacity: "1",
      ...(options.style || {})
    };

    for (const child of options.children || []) {
      this.append(child);
    }
  }

  append(child) {
    child.parentElement = this;
    this.children.push(child);
  }

  getAttribute(name) {
    return Object.prototype.hasOwnProperty.call(this.attributes, name)
      ? this.attributes[name]
      : null;
  }

  getBoundingClientRect() {
    return this.visible
      ? { width: 24, height: 24 }
      : { width: 0, height: 0 };
  }

  getClientRects() {
    return this.visible ? [{}] : [];
  }

  matches(selector) {
    if (selector !== 'button, [role="button"]') {
      throw new Error(`Unexpected selector: ${selector}`);
    }

    return this.tagName === "BUTTON" || this.getAttribute("role") === "button";
  }

  querySelectorAll(selector) {
    const matches = [];
    const visit = element => {
      for (const child of element.children) {
        if (child.matches(selector)) {
          matches.push(child);
        }

        visit(child);
      }
    };

    visit(this);
    return matches;
  }
}

const core = loadCore();

test("background capture posts the URL and returns the desktop response", async () => {
  let request = null;
  let clearedTimer = null;
  const listener = loadBackground({
    fetch: async (url, options) => {
      request = { url, options };
      return { ok: true, status: 202, statusText: "Accepted" };
    },
    setTimeout: () => 41,
    clearTimeout: timer => {
      clearedTimer = timer;
    }
  });

  const response = await new Promise(resolve => {
    assert.equal(listener({ type: "capture-stream", url: "https://www.twitch.tv/xqc" }, {}, resolve), true);
  });

  assert.equal(request.url, "http://127.0.0.1:39179/capture");
  assert.equal(request.options.method, "POST");
  assert.equal(request.options.body, JSON.stringify({ url: "https://www.twitch.tv/xqc" }));
  assert.equal(request.options.signal.aborted, false);
  assert.equal(response.ok, true);
  assert.equal(response.status, 202);
  assert.equal(response.statusText, "Accepted");
  await new Promise(setImmediate);
  assert.equal(clearedTimer, 41);
});

test("background capture aborts a stalled desktop request", async () => {
  let fireTimeout = null;
  let clearedTimer = null;
  const listener = loadBackground({
    fetch: (_url, options) => new Promise((resolve, reject) => {
      options.signal.addEventListener("abort", () => reject(new Error("capture timed out")), { once: true });
    }),
    setTimeout: callback => {
      fireTimeout = callback;
      return 73;
    },
    clearTimeout: timer => {
      clearedTimer = timer;
    }
  });

  const responsePromise = new Promise(resolve => {
    assert.equal(listener({ type: "capture-stream", url: "https://kick.com/xqc" }, {}, resolve), true);
  });
  assert.equal(typeof fireTimeout, "function");
  fireTimeout();

  const response = await responsePromise;
  assert.equal(response.ok, false);
  assert.match(response.error, /capture timed out/);
  await new Promise(setImmediate);
  assert.equal(clearedTimer, 73);
});

test("background ignores unrelated messages", () => {
  const listener = loadBackground();
  assert.equal(listener(null, {}, () => {}), false);
  assert.equal(listener({ type: "other", url: "https://kick.com/xqc" }, {}, () => {}), false);
  assert.equal(listener({ type: "capture-stream", url: 123 }, {}, () => {}), false);
});

test("normalizes Twitch and Kick channel links for capture", () => {
  assert.equal(
    core.channelFromUrl("https://www.twitch.tv/xqc?some=value", "https://www.twitch.tv/"),
    "https://www.twitch.tv/xqc");
  assert.equal(
    core.channelFromUrl("/summit1g", "https://www.twitch.tv/directory"),
    "https://www.twitch.tv/summit1g");
  assert.equal(
    core.channelFromUrl("https://kick.com/some-channel", "https://kick.com/"),
    "https://kick.com/some-channel");
  assert.equal(
    core.channelFromUrl("https://m.kick.com/some-channel", "https://m.kick.com/"),
    "https://kick.com/some-channel");
});

test("normalizes scheme-less Twitch and Kick channel links", () => {
  assert.equal(
    core.channelFromUrl("www.twitch.tv/summit1g?ref=home", "https://www.twitch.tv/"),
    "https://www.twitch.tv/summit1g");
  assert.equal(
    core.channelFromUrl("kick.com/some-channel", "https://kick.com/"),
    "https://kick.com/some-channel");
  assert.equal(
    core.channelFromUrl("twitch.tv:443/summit1g", "https://www.twitch.tv/"),
    "https://www.twitch.tv/summit1g");
  assert.equal(
    core.channelFromUrl("kick.com:443/some-channel", "https://kick.com/"),
    "https://kick.com/some-channel");
  assert.equal(core.channelFromUrl("twitch.tv:notaport/xqc", "https://www.twitch.tv/"), null);
});

test("identifies capture feedback platform names", () => {
  assert.equal(core.platformNameFromUrl("https://www.twitch.tv/xqc"), "Twitch");
  assert.equal(core.platformNameFromUrl("https://kick.com/xqc"), "Kick");
  assert.equal(core.platformNameFromUrl("not a url"), "stream");
});

test("reports successful stream capture feedback", () => {
  const status = core.captureStatusFromResponse({ ok: true, status: 202 }, "https://www.twitch.tv/xqc");
  assert.equal(status.kind, "success");
  assert.equal(status.message, "Sent Twitch stream to Twitch & Kick player.");
});

test("reports desktop app missing capture feedback", () => {
  const status = core.captureStatusFromResponse({ ok: false, error: "TypeError: Failed to fetch" }, "https://kick.com/xqc");
  assert.equal(status.kind, "error");
  assert.equal(status.message, "Twitch & Kick player is not running. Start the desktop app, then click the stream again.");
});

test("reports rejected stream capture feedback with HTTP status", () => {
  const status = core.captureStatusFromResponse({ ok: false, status: 400 }, "https://kick.com/xqc");
  assert.equal(status.kind, "error");
  assert.equal(status.message, "Twitch & Kick player rejected the Kick stream link (HTTP 400).");
});

test("rejects non-channel Twitch and Kick links", () => {
  assert.equal(core.channelFromUrl("", "https://www.twitch.tv/xqc"), null);
  assert.equal(core.channelFromUrl("https://www.twitch.tv/videos/123456", "https://www.twitch.tv/"), null);
  assert.equal(core.channelFromUrl("https://www.twitch.tv/directory", "https://www.twitch.tv/"), null);
  assert.equal(core.channelFromUrl("https://www.twitch.tv/login", "https://www.twitch.tv/"), null);
  assert.equal(core.channelFromUrl("https://www.twitch.tv/signup", "https://www.twitch.tv/"), null);
  assert.equal(core.channelFromUrl("https://www.twitch.tv/xqc/videos", "https://www.twitch.tv/"), null);
  assert.equal(core.channelFromUrl("https://kick.com/search", "https://kick.com/"), null);
  assert.equal(core.channelFromUrl("https://kick.com/login", "https://kick.com/"), null);
  assert.equal(core.channelFromUrl("https://kick.com/register", "https://kick.com/"), null);
  assert.equal(core.channelFromUrl("https://kick.com/xqc/clips", "https://kick.com/"), null);
  assert.equal(core.channelFromUrl("https://example.com/xqc", "https://www.twitch.tv/"), null);
});

test("identifies the visible Twitch channel point bonus button", () => {
  const button = new FakeElement("button", {
    attributes: { "aria-label": "Claim Bonus" }
  });

  assert.equal(core.isChannelPointClaimElement(button), true);
});

test("does not claim disabled, hidden, or unrelated buttons", () => {
  const disabled = new FakeElement("button", {
    attributes: { "aria-label": "Claim Bonus" },
    disabled: true
  });
  const ariaDisabled = new FakeElement("button", {
    attributes: { "aria-disabled": "true", "aria-label": "Claim Bonus" }
  });
  const hidden = new FakeElement("button", {
    attributes: { "aria-label": "Claim Bonus" },
    visible: false
  });
  const transparentParent = new FakeElement("div", {
    children: [new FakeElement("button", { attributes: { "aria-label": "Claim Bonus" } })],
    style: { opacity: "0.0" }
  });
  const drop = new FakeElement("button", {
    attributes: { "aria-label": "Claim Drop" }
  });

  assert.equal(core.isChannelPointClaimElement(disabled), false);
  assert.equal(core.isChannelPointClaimElement(ariaDisabled), false);
  assert.equal(core.isChannelPointClaimElement(hidden), false);
  assert.equal(core.isChannelPointClaimElement(transparentParent.children[0]), false);
  assert.equal(core.isChannelPointClaimElement(drop), false);
});

test("finds claimable button descendants without returning unrelated controls", () => {
  const claimButton = new FakeElement("button", {
    attributes: { "aria-label": "Claim Bonus" }
  });
  const roleButton = new FakeElement("div", {
    attributes: { role: "button", title: "Claim Bonus" }
  });
  const unrelated = new FakeElement("button", {
    attributes: { "aria-label": "Subscribe" }
  });
  const root = new FakeElement("div", {
    children: [unrelated, claimButton, roleButton]
  });

  const results = core.findChannelPointClaimElements(root);
  assert.equal(results.length, 2);
  assert.equal(results[0], claimButton);
  assert.equal(results[1], roleButton);
});
