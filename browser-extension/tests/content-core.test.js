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

  for (const script of ["platform-routes.generated.js", "content-core.js"]) {
    const source = fs.readFileSync(path.join(__dirname, "..", script), "utf8");
    vm.runInContext(source, context, { filename: script });
  }

  return context.StreamlinkVlcStudioContentCore;
}

function loadBackground(overrides = {}) {
  let messageListener = null;
  const context = {
    AbortController,
    URL,
    clearTimeout: overrides.clearTimeout || clearTimeout,
    fetch: overrides.fetch || (() => Promise.reject(new Error("fetch was not configured"))),
    setTimeout: overrides.setTimeout || setTimeout,
    importScripts: (...scripts) => {
      for (const script of scripts) {
        const source = fs.readFileSync(path.join(__dirname, "..", script), "utf8");
        vm.runInContext(source, context, { filename: script });
      }
    },
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
    this.clickCount = 0;
    this.queryCount = 0;
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

  click() {
    this.clickCount += 1;
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
    this.queryCount += 1;
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

test("background rejects invalid and non-canonical channel URLs", async () => {
  const listener = loadBackground();

  for (const url of [
    "https://www.twitch.tv/videos/123456",
    "https://www.twitch.tv/xqc?from=home",
    "https://example.com/xqc",
    " https://www.twitch.tv/xqc"
  ]) {
    const response = await new Promise(resolve => {
      assert.equal(listener({ type: "capture-stream", url }, {}, resolve), true);
    });
    assert.equal(response.ok, false);
    assert.equal(response.status, 400);
  }
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
  assert.equal(core.platformNameFromUrl("ftp://www.twitch.tv/xqc"), "stream");
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
  assert.equal(core.channelFromUrl("ftp://www.twitch.tv/xqc", "https://www.twitch.tv/"), null);
  assert.equal(core.channelFromUrl("javascript://twitch.tv/xqc", "https://www.twitch.tv/"), null);
  assert.equal(core.channelFromUrl("https://www.twitch.tv/videos/123456", "https://www.twitch.tv/"), null);
  assert.equal(core.channelFromUrl("https://www.twitch.tv/directory", "https://www.twitch.tv/"), null);
  assert.equal(core.channelFromUrl("https://www.twitch.tv/login", "https://www.twitch.tv/"), null);
  assert.equal(core.channelFromUrl("https://www.twitch.tv/signup", "https://www.twitch.tv/"), null);
  assert.equal(core.channelFromUrl("https://www.twitch.tv/creatorcamp", "https://www.twitch.tv/"), null);
  assert.equal(core.channelFromUrl("https://www.twitch.tv/xqc/videos", "https://www.twitch.tv/"), null);
  assert.equal(core.channelFromUrl("https://kick.com/search", "https://kick.com/"), null);
  assert.equal(core.channelFromUrl("https://kick.com/login", "https://kick.com/"), null);
  assert.equal(core.channelFromUrl("https://kick.com/register", "https://kick.com/"), null);
  assert.equal(core.channelFromUrl("https://kick.com/browse", "https://kick.com/"), null);
  assert.equal(core.channelFromUrl("https://kick.com/xqc/clips", "https://kick.com/"), null);
  assert.equal(core.channelFromUrl("https://example.com/xqc", "https://www.twitch.tv/"), null);
});

test("generated browser routes exactly match the shared route policy", () => {
  const shared = JSON.parse(fs.readFileSync(
    path.join(__dirname, "..", "..", "shared", "platform-routes.json"),
    "utf8"));
  const context = { globalThis: null };
  context.globalThis = context;
  vm.createContext(context);
  const source = fs.readFileSync(
    path.join(__dirname, "..", "platform-routes.generated.js"),
    "utf8");
  vm.runInContext(source, context, { filename: "platform-routes.generated.js" });

  assert.deepEqual(Array.from(context.StreamlinkVlcStudioPlatformRoutes.twitch), shared.twitch);
  assert.deepEqual(Array.from(context.StreamlinkVlcStudioPlatformRoutes.kick), shared.kick);
});

test("manifest limits background host access to the loopback capture endpoint", () => {
  const manifest = JSON.parse(fs.readFileSync(
    path.join(__dirname, "..", "manifest.json"),
    "utf8"));

  assert.deepEqual(manifest.host_permissions, ["http://127.0.0.1:39179/*"]);
  assert.deepEqual(manifest.content_scripts[0].matches, [
    "https://twitch.tv/*",
    "https://www.twitch.tv/*",
    "https://m.twitch.tv/*",
    "https://kick.com/*",
    "https://m.kick.com/*",
    "https://www.kick.com/*"
  ]);
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
  const prefixSpoof = new FakeElement("button", {
    attributes: { "aria-label": "Claim Bonus and subscribe" }
  });

  assert.equal(core.isChannelPointClaimElement(disabled), false);
  assert.equal(core.isChannelPointClaimElement(ariaDisabled), false);
  assert.equal(core.isChannelPointClaimElement(hidden), false);
  assert.equal(core.isChannelPointClaimElement(transparentParent.children[0]), false);
  assert.equal(core.isChannelPointClaimElement(drop), false);
  assert.equal(core.isChannelPointClaimElement(prefixSpoof), false);
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

function loadShippedContentController(initialUrl) {
  const timeoutCallbacks = new Map();
  const intervalCallbacks = new Map();
  const windowListeners = new Map();
  const documentListeners = new Map();
  const observers = [];
  const sentMessages = [];
  let nextTimerId = 1;

  class FakeAnchor {
    constructor(href) {
      this.href = href;
    }
  }

  class FakeMutationObserver {
    constructor(callback) {
      this.callback = callback;
      this.disconnected = false;
      observers.push(this);
    }

    observe(target, options) {
      this.target = target;
      this.options = options;
    }

    disconnect() {
      this.disconnected = true;
    }
  }

  const documentElement = new FakeElement("html");
  const document = {
    documentElement,
    addEventListener(name, callback) {
      documentListeners.set(name, callback);
    },
    removeEventListener(name, callback) {
      if (documentListeners.get(name) === callback) {
        documentListeners.delete(name);
      }
    },
    getElementById() {
      return null;
    },
    querySelectorAll(selector) {
      return documentElement.querySelectorAll(selector);
    }
  };
  const parsedInitialUrl = new URL(initialUrl);
  const context = {
    URL,
    Date,
    MutationObserver: FakeMutationObserver,
    HTMLAnchorElement: FakeAnchor,
    chrome: {
      runtime: {
        lastError: null,
        sendMessage(message) {
          sentMessages.push(message);
        }
      }
    },
    document,
    location: {
      href: parsedInitialUrl.href,
      hostname: parsedInitialUrl.hostname
    },
    getComputedStyle: element => element.style,
    requestAnimationFrame: callback => callback(),
    setTimeout(callback) {
      const id = nextTimerId++;
      timeoutCallbacks.set(id, callback);
      return id;
    },
    clearTimeout(id) {
      timeoutCallbacks.delete(id);
    },
    setInterval(callback) {
      const id = nextTimerId++;
      intervalCallbacks.set(id, callback);
      return id;
    },
    clearInterval(id) {
      intervalCallbacks.delete(id);
    },
    addEventListener(name, callback) {
      windowListeners.set(name, callback);
    },
    removeEventListener(name, callback) {
      if (windowListeners.get(name) === callback) {
        windowListeners.delete(name);
      }
    }
  };
  context.window = context;
  context.globalThis = context;
  vm.createContext(context);
  for (const script of ["platform-routes.generated.js", "content-core.js", "content.js"]) {
    const source = fs.readFileSync(path.join(__dirname, "..", script), "utf8");
    vm.runInContext(source, context, { filename: script });
  }

  return {
    controller: context.StreamlinkVlcStudioContentController,
    documentElement,
    observers,
    sentMessages,
    runTimeouts() {
      const callbacks = [...timeoutCallbacks.values()];
      timeoutCallbacks.clear();
      for (const callback of callbacks) {
        callback();
      }
    },
    navigate(url) {
      const parsed = new URL(url);
      context.location.href = parsed.href;
      context.location.hostname = parsed.hostname;
      context.StreamlinkVlcStudioContentController.refreshRoute();
    },
    dispatchWindowEvent(name, event = {}) {
      windowListeners.get(name)?.(event);
    },
    dispatchDocumentEvent(name, event) {
      documentListeners.get(name)?.(event);
    },
    createAnchor(href) {
      return new FakeAnchor(href);
    }
  };
}

test("shipped controller scans added subtrees and tears down across SPA routes", () => {
  const runtime = loadShippedContentController("https://www.twitch.tv/xqc");
  assert.equal(runtime.controller.isActive(), true);
  assert.equal(runtime.observers.length, 1);
  runtime.runTimeouts();

  const documentQueryCount = runtime.documentElement.queryCount;
  const claim = new FakeElement("button", {
    attributes: { "aria-label": "Claim Bonus" }
  });
  const addedSubtree = new FakeElement("section", { children: [claim] });
  runtime.observers[0].callback([{
    type: "childList",
    target: runtime.documentElement,
    addedNodes: [addedSubtree]
  }]);
  runtime.runTimeouts();

  assert.equal(claim.clickCount, 1);
  assert.equal(addedSubtree.queryCount, 1);
  assert.equal(runtime.documentElement.queryCount, documentQueryCount);

  const firstObserver = runtime.observers[0];
  runtime.navigate("https://www.twitch.tv/directory");
  assert.equal(runtime.controller.isActive(), false);
  assert.equal(firstObserver.disconnected, true);

  runtime.navigate("https://www.twitch.tv/summit1g");
  assert.equal(runtime.controller.isActive(), true);
  assert.equal(runtime.observers.length, 2);

  runtime.controller.stop();
  assert.equal(runtime.controller.isActive(), false);
  assert.equal(runtime.observers[1].disconnected, true);
});

test("shipped controller resumes after a back-forward cache restore", () => {
  const runtime = loadShippedContentController("https://www.twitch.tv/xqc");
  const firstObserver = runtime.observers[0];

  runtime.dispatchWindowEvent("pagehide", { persisted: true });
  assert.equal(runtime.controller.isActive(), false);
  assert.equal(firstObserver.disconnected, true);

  runtime.dispatchWindowEvent("pageshow", { persisted: true });
  assert.equal(runtime.controller.isActive(), true);
  assert.equal(runtime.observers.length, 2);
});

test("shipped capture handler ignores synthetic clicks", () => {
  const runtime = loadShippedContentController("https://www.twitch.tv/directory");
  const anchor = runtime.createAnchor("https://www.twitch.tv/xqc");
  runtime.dispatchDocumentEvent("click", {
    isTrusted: false,
    defaultPrevented: false,
    button: 0,
    ctrlKey: false,
    metaKey: false,
    shiftKey: false,
    altKey: false,
    composedPath: () => [anchor]
  });

  assert.equal(runtime.sentMessages.length, 0);
});

test("shipped capture handler accepts a trusted unmodified channel click", () => {
  const runtime = loadShippedContentController("https://www.twitch.tv/directory");
  const anchor = runtime.createAnchor("https://www.twitch.tv/xqc?from=home");
  let prevented = false;
  let propagationStopped = false;
  runtime.dispatchDocumentEvent("click", {
    isTrusted: true,
    defaultPrevented: false,
    button: 0,
    ctrlKey: false,
    metaKey: false,
    shiftKey: false,
    altKey: false,
    composedPath: () => [anchor],
    preventDefault() {
      prevented = true;
    },
    stopImmediatePropagation() {
      propagationStopped = true;
    }
  });

  assert.equal(runtime.sentMessages.length, 1);
  assert.equal(runtime.sentMessages[0].type, "capture-stream");
  assert.equal(runtime.sentMessages[0].url, "https://www.twitch.tv/xqc");
  assert.equal(prevented, true);
  assert.equal(propagationStopped, true);
});
