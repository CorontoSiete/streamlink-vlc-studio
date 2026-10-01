`nicewigg-transparent-150.png` is NiceWigg's unmodified public Twitch avatar,
downloaded on 2026-10-01 from the `profileImageURL(width: 150)` returned for login
`nicewigg` (user ID `415954300`):

https://static-cdn.jtvnw.net/jtv_user_pictures/a10b288f-886d-4cc5-b406-9f42213dadb4-profile_image-150x150.png

SHA-256: `e940cc96b903d6f695fcfad0df3ac06ed9bce544f16ec78f9f9ae5a407df6581`.
The 150 x 150 RGBA PNG has 19,942 fully transparent pixels. It reproduces the
default account glyph showing through a successfully loaded avatar. These tests
use the saved bytes so an upstream avatar change cannot remove the regression.
