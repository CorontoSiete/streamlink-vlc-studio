# Public channel profile fixture

`imperialhal-2026-10-22.json` is the anonymous response from Twitch's GraphQL
endpoint to the production `StreamStudioChannelProfile` query on 2026-10-22.
Request IDs and timing metadata were removed. No account credentials were used.

The short `description` is empty. The introduction is in the seventh default
panel, with no title, after unrelated sponsor terms. This guards against claiming
the channel has no biography or substituting the first nonempty panel as a bio.
The browser regression substitutes deterministic artwork for remote panel images.
