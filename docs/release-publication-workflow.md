# Release publication and retry

The protected stable-release job builds and validates the installers, signs the
update manifest, runs the installation/upgrade smoke test, and attests the
reviewed files before calling `scripts/publish-release.ps1`. That script keeps
the release as a draft until all seven uploaded assets match the local release
set. Normal users and the in-app updater receive the release only after this
verification succeeds.

## Retry an interrupted release

Rerun the failed protected workflow for the same stable tag and source commit.
The publication script recognizes its draft by an exact repository/tag/commit
marker in the release notes and the draft's target commit. It reuses files with
matching names, upload states, sizes, and SHA-256 digests, and replaces missing,
unfinished, or changed uploads. A rebuilt file can have different bytes, so a
full job rerun may need to replace more files than an interrupted upload alone.
Validation artifacts, reviewed release artifacts, and installer logs include the
workflow attempt number in their names, so reruns retain previous evidence and
avoid colliding with an earlier attempt's uploads.

Failures before publication leave the draft available for another attempt.
An absent or mismatched GitHub digest blocks publication. Unexpected or duplicate
asset names require inspection; the script does not silently delete them.
Manually created drafts, drafts for another commit, prereleases, and already
published releases are rejected without modification. If GitHub published the
release but the final response was lost, inspect the existing public release;
a rerun intentionally refuses to replace it.

Release jobs share a concurrency group. Preflight checks all pages of existing
releases, rejects an equal or newer final version, and verifies the remote tag's
commit. The same checks run again after uploading. A failed GitHub request or
malformed response stops the operation rather than implying a release is absent.

## Local verification

Run these from the repository with PowerShell 7:

```powershell
pwsh -NoProfile -File .\scripts\tests\release-publication.tests.ps1
pwsh -NoProfile -File .\scripts\tests\tooling.tests.ps1
```

The publication tests generate a temporary RSA key and signed fixture assets,
then simulate the GitHub CLI. They exercise publication, partial-upload and
publication retries, asset reuse/replacement, changed tags, newer releases,
foreign drafts, pagination, network failures, corrupt assets, and invalid
signatures. They make no network requests and do not install or publish anything.
CI runs them in the validation job before the protected release job.

The preflight command is read-only and needs GitHub CLI authentication:

```powershell
.\scripts\publish-release.ps1 -Preflight -Tag v1.7.7 `
    -Commit '<40-character source commit>' `
    -Repository CorontoSiete/streamlink-vlc-studio
```

Running without `-Preflight` publishes a release and belongs in the protected
workflow after its build, signature, installer, and provenance checks.

GitHub documents [draft creation](https://cli.github.com/manual/gh_release_create),
[asset replacement](https://cli.github.com/manual/gh_release_upload),
[draft publication](https://cli.github.com/manual/gh_release_edit), and
[release asset digests](https://docs.github.com/en/rest/releases/assets).
