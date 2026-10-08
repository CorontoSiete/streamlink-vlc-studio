# Post-update changelog

The first normal launch of an updated version opens **Settings > Changelog** and selects that version's notes. Users can return through the settings sidebar, the compact settings selector, or **Settings > Advanced > Updates > View changelog**. The version selector also provides earlier bundled notes.

The changelog leads with each release's authored title and introduction in a simple reading layout. A compact version selector provides release history, with a quiet installed-release label and a shortcut back to the installed version when browsing older notes. Changes use clear section headings, bold feature titles, quieter descriptions, and consistent spacing in every theme. **How to update** expands installation and verification instructions in a lightweight row below the changes.

The app reads `docs/releases/v<version>.md` from embedded assembly resources. It uses the installed application assembly's version, rather than the newest release available online. This works offline and in single-file installations, and never substitutes another version's notes.

Each version must have a nonempty notes file before the app can build. GitHub publication uses that same file. Add the actual release changes to `docs/releases/v<version>.md` when preparing a new version; headings, paragraphs, bullet lists, bold text, code, and web links are rendered in the app. No changelog content is generated from assumptions.

Use a brief introduction, descriptive `##` sections, and bold lead-ins to make changes easy to scan. The leading `#` heading and introduction appear above the changes; subsequent headings, lists, quotes, and fenced samples stay in the changes. Bullet items beginning with a bold lead-in display the title above the description. An optional `## Updating` section and its remaining content appear in the expandable update guide; that heading inside a fenced code sample stays with the notes. The complete authored Markdown remains the release publication source.

`Updates.LastSeenChangelogVersion` is saved through the existing settings autosave flow when the installed version's page becomes visible. An app closed before presentation leaves the version pending. Repeat launches and downgrades do not reopen already viewed notes; manual access remains available. Existing installations without this field show their current notes once. Fresh installations establish a baseline and finish account setup first.

The startup decision follows consumption of the updater's installation result, regardless of the automatic-check preference. Failed or canceled installations and persistent repair notices keep the notes pending until a successful installation or repair. A successful result for the installed version takes precedence over a stale repair notice left by failed cleanup. Notes are shown only for the executable version actually running. A setup dialog finishes before changelog startup runs.

## Validation

`scripts/dev.ps1 Test -Filter 'changelog:'` covers embedded content against the publication files, missing/empty/whitespace build rejection, fresh and existing installs, version changes, delayed or unreadable completion records, persistent repairs, formatting and safe links, visible-page persistence, history navigation, header and update-guide separation, and wide/compact layouts in all eight themes. The UI test checks the rendered version label and full scroll reachability with the update guide expanded and collapsed. Set `SVS_RESPONSIVE_SCREENSHOTS` to save current-release, historical-release, and update-guide renders during validation.
