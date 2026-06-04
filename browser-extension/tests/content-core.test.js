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
  assert.equal(core.channelFromUrl("https://kick.com/search", "https://kick.com/"), null);
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
  const drop = new FakeElement("button", {
    attributes: { "aria-label": "Claim Drop" }
  });

  assert.equal(core.isChannelPointClaimElement(disabled), false);
  assert.equal(core.isChannelPointClaimElement(ariaDisabled), false);
  assert.equal(core.isChannelPointClaimElement(hidden), false);
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
