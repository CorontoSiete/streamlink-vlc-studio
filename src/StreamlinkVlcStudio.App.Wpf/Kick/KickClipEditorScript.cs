using System.Text.Json;

namespace StreamlinkVlcStudio.App.Wpf.Kick;

internal static class KickClipEditorScript
{
    // Verified in Kick's player bundle on 2026-09-26. Track registration so an event is never
    // silently dispatched before the React provider mounts. No website credentials leave the page.
    internal static string Install(string key) => $$"""
        (() => {
            if (window !== window.top || location.origin !== 'https://kick.com') return;
            const listeners = new Map();
            const add = window.addEventListener;
            const remove = window.removeEventListener;
            const capture = options => typeof options === 'boolean' ? options : !!options?.capture;
            window.addEventListener = function(type, listener, options) {
                add.call(this, type, listener, options);
                if (this === window && type === 'openClipCreator' && listener) {
                    const flags = listeners.get(listener) || new Set();
                    flags.add(capture(options));
                    listeners.set(listener, flags);
                }
            };
            window.removeEventListener = function(type, listener, options) {
                remove.call(this, type, listener, options);
                if (this === window && type === 'openClipCreator') {
                    const flags = listeners.get(listener);
                    flags?.delete(capture(options));
                    if (flags?.size === 0) listeners.delete(listener);
                }
            };
            let submitted = false;
            const bridge = (path, open) => {
                if (location.origin !== 'https://kick.com' ||
                    location.pathname.replace(/\/$/, '').toLowerCase() !== path) return 'channel';
                if (listeners.size === 0) return 'loading';
                if (open) window.dispatchEvent(new CustomEvent('openClipCreator', { detail: { mode: 'livestream' } }));
                return 'ready';
            };
            bridge.publish = (path, title, canPublish) => {
                if (location.origin !== 'https://kick.com' ||
                    location.pathname.replace(/\/$/, '').toLowerCase() !== path) return 'channel';
                if (submitted) return 'submitted';
                const input = document.querySelector('[data-testid="clip-creator-title"]');
                const publish = document.querySelector('[data-testid="clip-creator-publish"]');
                if (!input || !publish) {
                    return document.querySelector('input[type="password"]') ? 'signin' : 'loading';
                }
                if (!(input instanceof HTMLInputElement || input instanceof HTMLTextAreaElement) ||
                    !(publish instanceof HTMLButtonElement) || input.form !== publish.form || !input.form) return 'loading';
                if (input.disabled) return 'loading';
                if (input.value !== title) {
                    const prototype = input instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
                    Object.getOwnPropertyDescriptor(prototype, 'value').set.call(input, title);
                    input.dispatchEvent(new Event('input', { bubbles: true }));
                    input.dispatchEvent(new Event('change', { bubbles: true }));
                    return 'editing';
                }
                if (!canPublish || publish.disabled || publish.getAttribute('aria-disabled') === 'true') return 'editing';
                // Submit once. An uncertain response must never cause a duplicate clip.
                submitted = true;
                publish.click();
                return 'submitted';
            };
            window[{{JsonSerializer.Serialize(key)}}] = bridge;
        })();
        """;

    internal static string Check(string key, string path, bool open) => $$"""
        (() => {
            const bridge = window[{{JsonSerializer.Serialize(key)}}];
            return typeof bridge === 'function' ? bridge({{JsonSerializer.Serialize(path)}}, {{(open ? "true" : "false")}}) : 'loading';
        })();
        """;

    internal static string Publish(string key, string path, string title, bool canPublish) => $$"""
        (() => {
            const bridge = window[{{JsonSerializer.Serialize(key)}}];
            return typeof bridge?.publish === 'function'
                ? bridge.publish({{JsonSerializer.Serialize(path)}}, {{JsonSerializer.Serialize(title)}}, {{(canPublish ? "true" : "false")}})
                : 'loading';
        })();
        """;
}
