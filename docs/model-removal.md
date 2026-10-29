# Model removal

Settings > Models offers **Remove** for every registered model, including
Unverified and Incompatible entries. The native confirmation names the model
and defaults to Cancel. Progress, cancellation, completion, failures, and partial
cleanup appear in the existing Fluent notification card between Runtime and
Models. Removal refreshes the list without navigation or activating another
model. An active removed model is deactivated; exported profiles are preserved.

## Commit and recovery

`PocketTtsModelRemovalService` takes the installer's cross-process installation
lock, then the exclusive private model operation lock. Busy operations are
refused. Removal rechecks the confirmation's exact registration identity.

Schema 2 has an optional `PendingCleanups` array, absent in existing inventories.
Each entry has an operation ID, alias, and canonical private blob references.
Unregistration and its journal entry are committed in one flushed atomic
inventory replacement under the existing inventory writer lock. No blob is
deleted before that commit. No schema conversion or migration is introduced.
Backups are not automatically restored and cannot substitute for reinstalling
deleted files.

Cleanup derives references from **every** remaining record, including Unverified
and Incompatible models. Shared blobs are preserved. Missing files are already
cleaned. Unreferenced candidates are deleted through validated exclusive handles
while parent directories are pinned against replacement. External paths,
reparse points, junctions, hardlinks, directories, and unexpected ownership
metadata are rejected. Corrupt regular blobs can be removed without passing
installation content verification.

Unparseable ownership metadata blocks unregistration instead of guessing what
other models need. Busy or unsafe files found after commit leave cleanup pending.
The journal stays until file cleanup and update-result invalidation complete.
Startup and subsequent removals retry under the same locks, rereading current
references; newly reinstalled/reused blobs remain protected. Repeated attempts
are safe after deletion followed by a crash before journal acknowledgement.

Cancellation is available before commit. After commit it cannot restore an
installed registration; window close can interrupt cleanup and leave recovery
work. Notifications report actual deleted bytes, retained shared references, and
pending failures. Missing, retained, or undeleted files reclaim no reported space.
An attempt can also finish work from previous removals. No recursive removal,
orphan/staging sweep, or Hugging Face cache access is performed.

## Native operations and stale selection

Every managed CLI invocation holds a private operation reader lease. A marker is
flushed under `models/.native-operations` before creation. `CreateProcessW` with
`PROC_THREAD_ATTRIBUTE_JOB_LIST` associates the suspended child with a named
kill-on-close job before any native code runs. Only standard-stream pipe handles
are inherited; arguments and the existing child environment are preserved.
Descendants are terminated and the job's active count must reach zero before
the marker is cleared. App termination also closes its job handles. See Microsoft
[process attributes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute)
and [job objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects).

Removal refuses active jobs and independently running `pocket-tts` processes.
Recovery clears a valid marker only when its job is empty or absent. Malformed,
inaccessible, or uncertain native state requires explicit manual reconciliation;
root exit alone is never treated as proof of complete child cleanup. The process
name check is conservative and can block for another project's CLI. Manually
launched tools that bypass application locks are outside managed coordination.

Startup activation and every export recheck the exact registration under a
reader lease. Stale selections cannot export removed/replaced registrations,
even when their shared files remain. Removing the active model clears its active
state and ComboBox selection without activating another model. Removing the active
or saved alias also writes a null alias to the separate `model-selection.json`
preference, preserving empty selection on restart even if other usable models remain.
This includes an active model chosen automatically on first run. Removing another
model preserves the current preference. Preference-write failures are reported in
the removal result. Removal invalidates the alias's persistent
update results using the existing writer guard. If busy or unwritable, cleanup
stays pending. Pending aliases cannot match update caches, including an identical
reinstall, until invalidation finishes. Other models' results are retained.

## Verification

`tests/PocketTTSStudioLite.ModelRemovalChecks` links production removal, inventory,
storage, update, and native code into temporary storage; only runtime-directory
infrastructure is substituted. Checks cover shared/last-owner deletion,
unverified records, busy locks/files, interrupted cleanup, re-registration,
cancellation/stale confirmation, unsafe references, hardlinks, native markers,
argument/pipe preservation, and descendant termination. No real models, GUI,
authentication, or network requests are used. Symlink creation is explicitly
skipped when Windows privilege/Developer Mode is unavailable.

Run the focused checks with:

```powershell
dotnet run --project .\tests\PocketTTSStudioLite.ModelRemovalChecks\PocketTTSStudioLite.ModelRemovalChecks.csproj --configuration Release
```

Development validation passed 17 focused checks; symlink creation was skipped
because Windows denied the required privilege. Junction and hardlink rejection
were exercised successfully. These checks do not uninstall real models.
The application Release build passed with zero warnings and zero errors.

Real-model uninstall, dialog/theme rendering, and actual power-loss recovery
remain for manual validation. No repair, migration, or orphan collector is added.
