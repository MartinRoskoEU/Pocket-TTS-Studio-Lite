# Temporary download ownership and recovery

Implemented in 0.2.0, stage 2. This covers temporary model
and runtime downloads. [Model updates](model-updates.md) reuse it; separate
[runtime retirement](runtime-cleanup.md) adds hash-bound inactive-version records.
Installer changes remain a later stage in the [lifecycle contract](release-0.2.0-lifecycle-plan.md).

## Durable ownership

`PocketTtsDownloadOperation` records each new download under the existing private
staging roots:

```text
%LOCALAPPDATA%/PocketTTSStudioLite/
  models/.staging/<operation-id>.json
  models/.staging/<operation-id>/<file-id>.partial
  runtimes/.staging/<operation-id>.json
  runtimes/.staging/<operation-id>/runtime.zip
  runtimes/.staging/<operation-id>/payload/pocket-tts.exe
  runtimes/.staging/<operation-id>/payload/LICENSE
  runtimes/.staging/<operation-id>/payload/runtime-installation.json
```

An empty operation directory is created first. A schema-1 record containing its
GUID, kind, physical store/directory identities and exact permitted relative
filenames is flushed to a new `.tmp` file and renamed to `.json` before any
download payload is created. Records contain no credentials, audio, export
destinations or caller-supplied absolute cleanup paths. Store/directory identity
uses volume serial, file index and creation time; copying a record to a different
store or replacing its operation directory does not transfer ownership.

A complete interrupted `.tmp` publication is recoverable. Truncated JSON,
unsupported schemas/fields, duplicate resources, invalid paths, conflicting
`.tmp`/`.json` records and changed directory identities are preserved. A failure
before journal publication can leave an empty directory or invalid record; these
are not silently adopted. Older unrecorded staging remains untouched.

## Locking and checked deletion

The model installation gate and cross-process `model-install.lock` now remain
held through temporary cleanup. Runtime installation likewise retains
`runtime-update.lock` through cleanup. Startup recovery obtains the corresponding
cross-process lock without waiting; an active operation leaves cleanup pending.
Model publication continues to take its existing model-store/inventory leases
inside the installation lock. Temporary cleanup does not reacquire those leases
or either installation lock, preventing recursive lock acquisition.

The complete recorded layout is checked before the first deletion. The journal
is held read-locked through payload cleanup; existing payload files must be
independent regular files. All temporary file identities are captured during
preflight and rechecked through exclusive deletion handles with parent
directories pinned against replacement. Symlinks, hardlinks and junctions are
rejected. An unexpected operation file/subdirectory preserves that operation
instead of allowing a recursive traversal. Other independently valid operation
records can still be cleaned.

Directories are deleted only when empty, through checked handles using their
expected physical identity. The journal is removed last, after directory cleanup.
Missing payload files or an already removed operation directory are accepted on
retry, covering interruption between deletion and acknowledgement. No recursive
directory deletion or orphan sweep is used. The existing checked model-file
deletion guard remains the default; its local-data mode is used explicitly for
runtime staging and journals.

Startup invokes recovery off the UI thread before existing pending model-removal
recovery and local model/runtime verification. It uses the existing result card
to report completed or pending storage cleanup. Failed immediate cleanup remains
recorded, and installation results distinguish a successful installation from
pending temporary cleanup. Cancellation of startup leaves remaining records;
cleanup of an individual validated operation finishes without cancellation.

Installed model blobs, inventories, selected model, settings, installed runtimes,
bundled runtime, reference audio, external profiles and shared Hugging Face cache
are outside this temporary cleanup. A downloaded runtime moved into its installed
release directory is outside ordinary download-record authority. Verified inactive
installed versions, including unused verified candidates, are handled separately
by [runtime retirement](runtime-cleanup.md), after verifying a healthy active runtime.

## Targeted validation

Run the existing removal-check executable:

```powershell
dotnet run --project tests/PocketTTSStudioLite.ModelRemovalChecks -c Release
```

It links the production operation journal and checked file/directory deletion
code, with only the private root redirected by test infrastructure to isolated
temporary fixture storage. The 27 new checks cover interrupted model/runtime
payloads, completed `.tmp` publication, missing payload/directory retries, unknown
content, locked files, both installation locks, physical directory/store changes,
invalid/duplicate metadata, unrelated valid operations, hardlinks, operation and
payload junctions, changed file identity, cancellation and legacy unrecorded data.
The existing 17 removal/native-lifecycle checks also pass; their separate symbolic
link case is skipped when Windows denies the required privilege. Junction and
hardlink cases run without that skip.

These checks simulate interruption using durable fixtures. They do not download
models/runtime releases, sign in, access real private application data, start the
WinUI application, perform real cloning/export or install/uninstall anything.
Live startup reporting, actual interrupted downloads and power-loss behavior
remain unverified. No new dependency or test framework is introduced.
