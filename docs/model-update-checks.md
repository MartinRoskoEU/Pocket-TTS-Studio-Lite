# Anonymous model update checks

Settings > Models offers **Check for updates** in place of Refresh models.
Clicking it first reruns the existing local runtime/model verification without
activation or navigation, then asynchronously compares registered installed
models with the official `kyutai/pocket-tts` main revision. The header and each
registered model being processed show **Checking for updates...**. The button
and conflicting model/runtime installation actions are disabled during a manual
check. Automatic checks disable only duplicate model checks, allowing model
installation, runtime management, navigation, and Voice Cloning to continue.
Existing Voice Cloning remains available with its normal local verification.

`PocketTtsModelUpdateService` reads `model-inventory.json` without changing it.
Only schema-2 private registered installations are accepted. Shared HF cache
files and unregistered private blobs are not installed models and are not checked
remotely. Unsupported development inventory requires manual cleanup, not migration. Each inventory record is validated and its files are
reverified in private storage with the existing file-size/hash verifier and
shared model-store operation leases. External paths, links, and noncanonical blob
references cannot qualify for comparison. Missing, damaged, invalid,
or unsupported-source records receive **Unable to check** and do not trigger a
remote query for that model. Runtime compatibility does not decide update status:
a verified Incompatible model can still be compared, while its export remains
blocked by the unchanged compatibility rules.

## Background scheduling and persistent results

After local startup verification and initial navigation/activation have finished,
`MainWindow` queues a background check without awaiting it. All inventory/settings
I/O, eligibility decisions, file verification, and network requests run off the UI
thread through `PocketTtsModelUpdateCoordinator`, which reuses the existing
`PocketTtsModelUpdateService`. Automatic failures produce no dialogs, popups, or
global error banner; a failed model comparison can show the existing Unable to
check indicator. Up-to-date models receive no new notifications.

Successful check settings live at
`%LOCALAPPDATA%\PocketTTSStudioLite\model-update-checks.json`, separate from
`model-inventory.json`. The schema-versioned document contains a UTC
`LastSuccessfulCheckUtc` plus each successful model result and its own UTC check
time. A SHA-256 fingerprint of the complete inventory record binds each cached
result to its alias, repositories, commits, required paths/sizes/hash algorithms/
hashes, private blob locations, and retained configuration. Old shared-path cache
identities never match a newly installed private record. This fingerprint does
not replace actual model integrity verification.

Automatic checks skip an identity whose last successful comparison is less than
24 hours old. Missing/new/changed identities remain eligible even if other models
were checked recently. A successful installation queues another eligibility pass;
if a check is already running, that request waits until it finishes and newly
registered models are then considered. No continuous retry loop or polling timer
is introduced. Failed checks remain eligible at the next scheduled opportunity.
Manual **Check for updates** always bypasses the interval.

Only a complete batch of successful Up to date/Update available comparisons,
with every targeted inventory identity still unchanged after the check, advances
persisted timestamps. Failed, cancelled, incomplete, or identity-changed batches
leave the previous settings and timestamps unchanged. Successful manual checks
update the same cache/timestamps. Both successful states count as completed
checks; detecting an available update never means it was installed.

Settings use an in-process gate and cross-process exclusive lock spanning cache
selection, network comparison, and save. Writes follow existing local-data
conventions: a flushed temporary JSON file, atomic replacement, and a
`model-update-checks.previous.json` backup. No model inventory or asset is written.
Absent, invalid, unsupported, or future-dated cache data is ignored rather than
used to claim success or suppress a check. A later complete success can replace
invalid cache data, retaining the previous file as a backup. Persistence failures
leave successful check times unchanged; background persistence errors are silent,
while a manual check can show a safe cache-save warning.

Cached results are displayed only when their fingerprint matches the locally
initialized inventory record and the existing local file verification succeeded.
Confirmed [model removal](model-removal.md) invalidates the removed alias under
the same update writer guard. If invalidation cannot complete, the inventory's
pending cleanup remains durable and its aliases are excluded from cache matching,
even after an identical reinstall, until recovery finishes. Other aliases retain
their results and successful timestamps.
Damaged or changed installations cannot reuse a cached Up to date label. Cache
results include the last successful UTC check time in their tooltip; they describe
that verified comparison, not an unconditional assertion about future upstream
changes. Completing a background check updates the same per-model indicators.
Page changes preserve state, and local refresh keeps only still-matching results.
Manual/automatic checks cannot run concurrently or duplicate requests; checking
is disabled only while another check or a conflicting manual operation runs,
never merely because of the interval.

## Anonymous metadata requests

The UI uses a dedicated `HttpClient` with redirects, cookies, and Windows
credentials disabled. The service has no reference to the authentication service,
does not read credential stores or environment tokens, and refuses a client with
an Authorization default header. It never initiates OAuth or performs a separate
connectivity probe. It requests JSON metadata only, never model file contents:

1. Fetch `/api/models/kyutai/pocket-tts/revision/main?blobs=true` once for the
   eligible installations in this check.
2. Require the official repository identity and a complete main commit SHA.
3. If a required file's comparable identity/size is absent or unusable, query
   `/api/models/kyutai/pocket-tts/tree/{resolved-main-commit}/{directory}` with
   `recursive=false&expand=false`. Cache each directory response for this check.
   Fallback requests use the resolved commit so a moving main branch cannot mix
   different revisions within the comparison. A missing target in the returned
   listing remains unavailable; absence is never interpreted as an installed update.

Every request has a 10-second deadline and a 16 MiB metadata response limit.
Offline operation, timeouts, HTTP errors (including gating/rate limits), malformed
JSON, and unavailable fields produce safe unavailable results. Response bodies,
transport exceptions, and sensitive payloads are never displayed. Automatic
model checks run only after local initialization/navigation, or after successful
installation, subject to the identity-specific interval. Runtime update checking
remains a separate existing feature.

## File comparison rules

The installed inventory's expected hashes and sizes are the baseline; the
repository commit itself is not compared to declare an update.

- **LFS/Xet weights:** compare the actual payload SHA-256 in model-info
  `lfs.sha256` with the stored `SHA256`. A tree fallback may use `lfs.oid` only
  when it is a valid full 64-character hexadecimal payload hash. Neither the
  top-level Git pointer OID nor `xetHash` is a payload SHA-256 substitute.
- **Ordinary Git files:** compare model-info `blobId` or tree `oid` with the
  stored `GitBlobSHA1`, including the existing Git blob header convention. A
  transition to large-file storage cannot be equated to an ordinary Git blob
  comparison and remains unavailable if no comparable identity exists.
- Reject masked values such as asterisks, invalid hash lengths, missing/nonpositive
  sizes, and contradictory large-file size metadata. Equal content hashes with
  different sizes are inconsistent and receive an unavailable result.

All required files must have complete comparable identities and sizes:

| Update label | Meaning |
| --- | --- |
| Up to date | Every required file hash and size matches the installed inventory. |
| Update available | The complete comparison succeeded and at least one required file differs. Nothing was downloaded or installed. |
| Unable to check | Local verification, metadata access, or any required comparison could not complete reliably. |

These labels appear as a separate secondary line below the existing
Installed/Not installed/Unverified/Incompatible status. Details are available in
the update label's tooltip. Uninstalled/unregistered rows have no update label.
Successful results are persisted and restored by exact installed identity;
results also remain in memory across page changes. A local refresh discards only
results that no longer match the verified installation identity.

The method detects changes to the recorded paths only. It does not discover new
model aliases, changed runtime configurations, renamed files, or new dependencies,
and does not establish compatibility of changed weights. Anonymous metadata
access may change upstream: the investigation found public model-info SHA-256s
and Git OIDs but redacted tree LFS/Xet hashes and authentication-gated paths-info.
Masked/failed responses must never become **Up to date**. No paths-info endpoint
or download endpoint is used by this checker.

## Scope and verification

This feature never downloads or installs updates, writes inventory, changes model
files, or requests authentication. Only the separate update-check settings file is
persisted after complete success. Model installation, runtime validation/update
services, startup activation, and local Voice Cloning retain their existing behavior.
No dependencies or test infrastructure were added. Verification consists of the
application Release build and source review only; no regression tests or app launch.
No authentication, model downloads, or live execution of the automatic checker
was performed during development. Startup responsiveness, interval skipping, new
installations, manual override, failures, identity invalidation, and cross-instance
concurrency remain for manual checking.
