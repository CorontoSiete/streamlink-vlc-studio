using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.Follows;

internal static class ChannelFollowScript
{
    internal static string Build(StreamTarget target, string key, bool click, string twitchDisplayName, bool following = true,
        bool retryUndispatched = false) => $$"""
        (() => {
            const expectedOrigin = {{JsonSerializer.Serialize(new Uri(target.Url).GetLeftPart(UriPartial.Authority))}};
            const expectedPath = {{JsonSerializer.Serialize(new Uri(target.Url).AbsolutePath)}};
            if (location.origin !== expectedOrigin || location.pathname.replace(/\/$/, '').toLowerCase() !== expectedPath)
                return 'page';
            const state = window[{{JsonSerializer.Serialize(key)}}] ||= { clicked: false };
            if (twitchCaptureNeeded()) {
                state.listening = true;
                document.addEventListener('click', event => {
                    if (!event.isTrusted) return;
                    const button = event.target instanceof Element ? event.target.closest({{JsonSerializer.Serialize(following ? "button[data-a-target=\"follow-button\"]" : "button[data-a-target=\"unfollow-button\"], button[data-a-target=\"modal-unfollow-button\"]")}}) : null;
                    if (button && (button.closest('.channel-info-content') || button.closest('[role="dialog"], [role="alertdialog"]')?.textContent.includes({{JsonSerializer.Serialize(twitchDisplayName)}})) &&
                        ((button.getAttribute('aria-label') || '').includes({{JsonSerializer.Serialize(twitchDisplayName)}}) || button.dataset.aTarget === 'modal-unfollow-button'))
                        state.trustedFollowClick = true;
                }, true);
            }
            function twitchCaptureNeeded() {
                return {{(target.Platform == PlatformKind.Twitch ? "true" : "false")}} &&
                    {{JsonSerializer.Serialize(twitchDisplayName)}}.length > 0 && !state.listening;
            }
            if (state.clicked) {
                // Twitch can mount an enabled control before its handler has follow data.
                // Only the host's observation of zero native mutation requests permits
                // another click, after rechecking this same page, account and named control.
                if (!{{(target.Platform == PlatformKind.Twitch && following && click && retryUndispatched ? "true" : "false")}} ||
                    !Number.isFinite(state.clickedAt) || performance.now() - state.clickedAt < 1000) return 'pending';
                state.clicked = false;
            }
            const visible = button => {
                const rect = button.getBoundingClientRect(), style = getComputedStyle(button);
                return rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden';
            };
            const twitch = {{(target.Platform == PlatformKind.Twitch ? "true" : "false")}};
            const following = {{(following ? "true" : "false")}};
            if (!following && twitch && state.opened) {
                const controls = [...document.querySelectorAll('button[data-a-target="modal-unfollow-button"]')];
                const buttons = controls.filter(visible);
                if (buttons.length > 1) return 'ambiguous';
                const button = buttons[0];
                if (button) {
                    if (button.disabled || button.getAttribute('aria-disabled') === 'true') return 'waiting';
                    const dialog = button.closest('[role="dialog"], [role="alertdialog"]');
                    if (!dialog || !dialog.textContent.includes({{JsonSerializer.Serialize(twitchDisplayName)}})) return 'identity';
                    if (!{{(click ? "true" : "false")}}) return 'ready';
                    state.clicked = true;
                    button.click();
                    return 'clicked';
                }
                // The initial opening can be ignored while Twitch renders its control.
                // Only retry this noncommitting opener when no confirmation is mounted;
                // a confirmation click sets state.clicked and can never reach this path.
                if (controls.length || !{{(click ? "true" : "false")}} || performance.now() - state.openedAt < 1000) return 'waiting';
                state.opened = false;
            }
            if (!following && !twitch) {
                if (!{{(click ? "true" : "false")}}) return 'ready';
                const cookie = document.cookie.split(';').map(c => c.trim()).find(c => c.startsWith('session_token='));
                if (!cookie) return 'waiting';
                state.clicked = true;
                // Kick's website uses this same authenticated DELETE contract for unfollow.
                void fetch({{JsonSerializer.Serialize($"https://kick.com/api/v2/channels/{target.Channel}/follow")}}, {
                    method: 'DELETE', credentials: 'include', mode: 'same-origin', redirect: 'error', cache: 'no-store',
                    headers: { Accept: 'application/json', 'x-app-platform': 'web', Authorization: `Bearer ${decodeURIComponent(cookie.substring('session_token='.length))}` },
                    signal: AbortSignal.timeout(20000)
                }).catch(() => {});
                return 'clicked';
            }
            let root = document;
            if (twitch) {
                // The inspected live and offline channel layouts put their own controls in this region.
                // A recommendation's Follow button elsewhere on the page must never be selected.
                const regions = [...document.querySelectorAll('.channel-info-content')].filter(visible);
                if (regions.length > 1) return 'ambiguous';
                if (!regions.length) return 'waiting';
                root = regions[0];
            }
            const selector = twitch ? following ? 'button[data-a-target="follow-button"]' : 'button[data-a-target="unfollow-button"]' : 'button[data-testid="follow-button"]';
            if (twitch && following && [...root.querySelectorAll('button[data-a-target="unfollow-button"], button[data-a-target="unfollow-button--with-dropdown"]')].some(visible))
                return 'following';
            const controls = [...root.querySelectorAll(selector)].filter(visible);
            const name = {{JsonSerializer.Serialize(twitchDisplayName)}};
            // Twitch also mounts an unnamed About control in this same region, sometimes
            // before the named channel control. Identify the broadcaster before counting
            // candidates, and wait for the named control instead of clicking the About one.
            const buttons = twitch ? controls.filter(button => name && (button.getAttribute('aria-label') || '').includes(name)) : controls;
            if (buttons.length > 1) return 'ambiguous';
            const button = buttons[0];
            if (!button) return twitch && name && controls.length ? 'identity-pending' : 'waiting';
            if (twitch && {{(click ? "true" : "false")}}) {
                if (!name || !(button.getAttribute('aria-label') || '').includes(name)) return 'identity';
            }
            if (!twitch) {
                // Both authenticated and signed-out Kick buttons share the test ID. The service verifies the session
                // and server follow list first. Never click the followed variant (which opens an unfollow dialog).
                if (button.querySelector('svg[data-ds-icon="HeartFilled"], svg[data-ds-icon="HeartBrokenOutline"]')) return 'following';
                if (!button.querySelector('svg[data-ds-icon="HeartOutline"]')) return 'waiting';
            }
            if (button.disabled || button.getAttribute('aria-disabled') === 'true') return 'waiting';
            if (!{{(click ? "true" : "false")}}) return 'ready';
            if (twitch && !following) {
                state.opened = true;
                state.openedAt = performance.now();
                button.click();
                return 'waiting';
            }
            state.clicked = true;
            state.clickedAt = performance.now();
            button.click();
            return 'clicked';
        })()
        """;

    internal static string ConsumeTrustedFollowClick(StreamTarget target, string key) => $$"""
        (() => {
            if (location.origin !== {{JsonSerializer.Serialize(new Uri(target.Url).GetLeftPart(UriPartial.Authority))}} ||
                location.pathname.replace(/\/$/, '').toLowerCase() !== {{JsonSerializer.Serialize(new Uri(target.Url).AbsolutePath)}})
                return false;
            const state = window[{{JsonSerializer.Serialize(key)}}];
            const trusted = state?.trustedFollowClick === true;
            if (state) state.trustedFollowClick = false;
            return trusted;
        })()
        """;
}
