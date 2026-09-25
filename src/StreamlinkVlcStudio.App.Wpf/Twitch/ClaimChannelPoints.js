// Only the Twitch bonus control is eligible. Never click rewards or generic buttons.
// Selector verified against BetterTTV's maintained channel_points implementation:
// https://github.com/night/betterttv/blob/master/src/modules/channel_points/index.js
(channel) => {
    if (location.origin !== 'https://www.twitch.tv' ||
        location.pathname.replace(/\/$/, '').toLowerCase() !== '/popout/' + channel + '/chat') {
        return 'Waiting for this channel\'s Twitch chat. Use Sign in for bonuses if needed.';
    }

    // Chat exposes already offered bonuses. Do not start a player or simulate
    // watch time: eligibility and the eventual award are determined by Twitch.
    const state = window.__streamStudioBonusState ??= { lastAttempt: -Infinity };
    const now = performance.now();
    for (const icon of document.querySelectorAll('.claimable-bonus__icon')) {
        const button = icon.closest('button');
        if (!button || !icon.isConnected || button.disabled ||
            button.getAttribute('aria-disabled') === 'true' ||
            button.getAttribute('aria-busy') === 'true' ||
            button.className.includes('ScCoreButtonDestructive') ||
            icon.className.includes('ScCoreButtonDestructive') ||
            !button.getClientRects().length || getComputedStyle(button).visibility !== 'visible') continue;

        // Retry a rejected click after a minute. The cooldown survives React
        // replacing the button and is unaffected by system clock changes.
        // A disappearing control is not proof of a server-side claim.
        if (now - state.lastAttempt < 60000) continue;
        state.lastAttempt = now;
        button.click();
        return 'Bonus claim clicked; waiting for Twitch.';
    }

    return 'Checking chat for available bonuses (no background video).';
}
