# Hugging Face authentication

The application uses Hugging Face's [Public OAuth Device Authorization Flow](https://huggingface.co/docs/hub/oauth)
and [RFC 8628](https://www.rfc-editor.org/rfc/rfc8628). No client secret, embedded
browser, password field, Hub CLI, Python, or OAuth package is used.

## Configuration and components

`PocketTTSStudioLite/Configuration/HuggingFaceOAuthConfiguration.cs` contains the
public registration and fixed official endpoints:

- Client ID: `e5f65004-0823-403e-8c4a-1fd4f23db279`.
- Requested scopes: `openid gated-repos`.
- Device authorization: `POST https://huggingface.co/oauth/device`.
- Token polling: `POST https://huggingface.co/oauth/token`.

`HuggingFaceAuthenticationService` owns protocol handling and the in-memory
session. Its status snapshots expose only UI-safe messages, the public user code,
the verification address, and expiry times. `InstallationAuthenticationDialog`
renders those snapshots only during a requested installation, marshals background
changes through its dispatcher, and uses
`Windows.System.Launcher.LaunchUriAsync` to open the default browser. The OAuth
HTTP client disables redirects and cookies. Runtime and model-discovery services
are unchanged; discovery remains anonymous and is not called by authentication.

Each requested model installation first tries downloading anonymously. If the
official repository requires authorization, the installation automatically opens
a native WinUI `ContentDialog` and starts the existing device flow. Settings has
no persistent authentication section, Sign in/Sign out buttons, or session labels.
The initial form request sends `client_id`
and `scope`. Polling sends `client_id`, `device_code`, and the standard device-code
grant type. It waits for the server interval (five seconds if absent), adds five
seconds after `slow_down`, and uses exponential backoff for connectivity failures,
request timeouts, rate limits, and temporary server failures. Polling stops on
approval, denial, expiration, cancellation, sign-out, or application close.
Each HTTP request has a 30-second timeout; polling is also bounded by the code's
remaining lifetime. Temporary polling failures are visible in the status message.

The live device endpoint returned `https://hf.co/oauth/device`. That official
short address was verified to redirect to `https://huggingface.co/oauth/device`;
the service validates and normalizes it to the canonical address. Only HTTPS
addresses on `huggingface.co`, or that exact short-host/path alias, are accepted.
The visible user code is entered manually in the browser; private device codes
are never added to browser URLs or UI status.

## Session behavior

- Access tokens are private service fields held only in process memory. They are
  not placed in configuration, files, logs, status snapshots, CLI arguments,
  the parent environment, Windows credentials, or Hugging Face's token cache.
  The installation owns its session exclusively. Local voice export does not
  request authentication or receive the installation token.
- The service validates bearer-token type, lifetime, and the required granted
  scopes. Missing response scopes mean the requested scopes, as defined by OAuth.
  It does not use ID tokens to claim a user identity or request profile/email data.
- Returned ID/refresh tokens are not retained or used. There is no silent refresh;
  session expiry clears the local token and requests a fresh interactive sign-in.
- Every installation starts with a cleared session. Success, authentication
  failure, installation failure, and cancellation clear the token reference,
  timer, and pending authorization through the existing local `SignOut` method.
  Cancellation clears it immediately; the workflow also clears it in `finally`
  before refreshing Settings. No token is retained between installations.
  A generation check prevents late authorization responses from restoring a
  cleared session. Window close disposes the service and its HTTP client.
- Clearing references does not promise cryptographic erasure of managed process
  memory. No authentication state is restored on restart.
- Sign out does not log out the browser, revoke the user's Hugging Face app grant,
  or revoke a token server-side. The public OpenID metadata did not advertise a
  revocation endpoint. Pending device codes expire server-side after local cancel.
- `gated-repos` applies to public gated repos the user already has permission to
  access; sign-in does not accept model terms or establish model compatibility.
  The installation dialog visibly links to `https://huggingface.co/kyutai/pocket-tts`
  and explains that conditions must be reviewed and accepted on Hugging Face
  separately when required. Accepting conditions can be necessary before the
  user completes device authorization.

OAuth error descriptions and raw response bodies are never displayed or logged.
Expected failures use fixed, safe messages. Invalid client/scopes, malformed JSON,
unsafe verification URLs, invalid lifetimes, and missing permissions fail closed.
Responses are limited to 64 KiB; supported device lifetimes/intervals are bounded
to one day and token lifetimes to 31 days. Unexpected provider changes are reported
instead of establishing a session.

## Verification — 2026-10-27

**Nine targeted authentication check groups passed**, with no browser authorization
or actual access token obtained:

```powershell
dotnet run --project .\tests\PocketTTSStudioLite.AuthenticationChecks\PocketTTSStudioLite.AuthenticationChecks.csproj --configuration Release -- --live-device
```

The eight fixture groups cover public-client requests and scopes; pending and
slower polling; approval and safe session status; rejection; server/local code
expiration; caller cancellation; cancel/sign-out/disposal races with late tokens;
session expiry; connection backoff; initial offline failure; malformed responses,
registration and permission errors; default intervals; and HTTP request timeout.
The tests use .NET `TimeProvider` with a controlled clock and in-memory HTTP fixtures.
Running without `--live-device` executes only those eight groups.

The live check used the configured client and scopes against the official device
endpoint. It cancelled immediately after receiving valid user-code metadata,
before any token polling or browser launch. Codes and responses were not saved
or printed. This confirms device-request acceptance, not completed consent,
granted scopes, or a successful real token exchange.

The WinUI Release build passed with **0 warnings and 0 errors**. An unpackaged
launch and computer-use inspection confirmed the initial signed-out UI and button
states. The interactive browser launch, code entry, consent, and real signed-in
state are deliberately left for manual verification. No runtime/discovery
regression suite, model downloads, or voice exports were performed.

## Installation dialog and manual validation

The native installation dialog shows authorization progress, the public user code,
expiry, and official authorization address. It opens the browser once for each
new code and offers an Open authorization page button plus a copyable address/code
if browser launch fails. Cancel installation, Escape, or window close cancels the
pending flow and clears the local session. Successful authorization closes the
dialog and resumes the same installation; rejected, expired, or failed flows close
it and report a safe installation error. There are no standalone login actions.

The authentication lifecycle change was verified with an application Release build
and source inspection only. No regression tests, application launch, OAuth requests,
model downloads, or real browser authentication were performed for this change.
The earlier protocol-check evidence above is historical and was not rerun.

For manual validation, request a missing model and confirm anonymous access is
tried first. For gated access, confirm the dialog appears automatically with the
repository conditions link and explanation. Review/accept conditions on Hugging
Face when required, enter the current public code, and authorize the application.
Confirm installation resumes and Settings shows only installation progress/results.
Repeat with cancellation, rejected/expired authorization, missing repository access,
and a download failure. No outcome should leave an app session for the next Install
attempt, and the browser's signed-in state should remain unchanged. Download and
model integrity behavior are documented in [model installation](model-installation.md).
