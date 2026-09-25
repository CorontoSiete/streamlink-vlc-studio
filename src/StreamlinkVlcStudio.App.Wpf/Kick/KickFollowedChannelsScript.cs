using System.Text.Json;
using StreamlinkVlcStudio.Infrastructure.Viewers;

namespace StreamlinkVlcStudio.App.Wpf.Kick;

internal static class KickFollowedChannelsScript
{
    // The website uses session_token as a Bearer header with cookies included.
    // Keep it inside Kick's browser origin; only the follow-list response leaves it.
    internal static string Build(string requestId, string url, string sessionId, bool firstPage) => $$"""
        void (async () => {
            const id = {{JsonSerializer.Serialize(requestId)}};
            const send = value => window.chrome.webview.postMessage({ id, ...value });
            try {
                if (location.origin !== 'https://kick.com') { send({ error: 'origin' }); return; }
                const cookie = document.cookie.split(';').map(c => c.trim()).find(c => c.startsWith('session_token='));
                const token = cookie ? decodeURIComponent(cookie.substring('session_token='.length)) : '';
                if (!token) { send({ error: 'signin' }); return; }
                const fingerprint = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(token)))).join(',');
                const sessionId = {{JsonSerializer.Serialize(sessionId)}};
                if ({{(firstPage ? "true" : "false")}}) window.__streamStudioKickImport = { sessionId, fingerprint };
                const session = window.__streamStudioKickImport;
                if (!session || session.sessionId !== sessionId || session.fingerprint !== fingerprint) {
                    send({ error: 'account' }); return;
                }
                const response = await fetch({{JsonSerializer.Serialize(url)}}, {
                    method: 'GET', credentials: 'include', mode: 'same-origin', redirect: 'error', cache: 'no-store',
                    headers: { Accept: 'application/json', 'x-app-platform': 'web', Authorization: `Bearer ${token}` },
                    signal: AbortSignal.timeout(20000)
                });
                if (!response.ok) { send({ status: response.status }); return; }
                const reader = response.body.getReader();
                const decoder = new TextDecoder();
                let size = 0, body = '';
                try {
                    for (;;) {
                        const { done, value } = await reader.read();
                        if (done) break;
                        size += value.byteLength;
                        if (size > {{KickFollowedChannelsReader.MaximumResponseBytes}}) {
                            await reader.cancel(); send({ error: 'size' }); return;
                        }
                        body += decoder.decode(value, { stream: true });
                    }
                    body += decoder.decode();
                } finally { reader.releaseLock(); }
                const current = document.cookie.split(';').map(c => c.trim()).find(c => c.startsWith('session_token='));
                if (current !== cookie) { send({ error: 'account' }); return; }
                send({ status: response.status, body });
            } catch { send({ error: 'network' }); }
        })();
        """;
}
