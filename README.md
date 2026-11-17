# filezilla-windows-arm64

[FileZilla](https://filezilla-project.org/) is a free open source FTP and SFTP client. This repository releases Windows on ARM64 (WoA) build of FileZilla, which can be downloaded at the [release](https://github.com/minnyres/filezilla-windows-arm64/releases) page.

Inspired by the work of [driver1998](https://github.com/driver1998/filezilla-woa). We have another project building [FileZilla FTP Server for WoA](https://github.com/minnyres/filezilla-server-windows-arm64).

## Custom patch: auto-refresh all tabs

This fork adds `patches/0004-auto-refresh-all-tabs.patch`, which applies to the FileZilla 3.71.1 sources during the build:

- Native timer inside FileZilla that refreshes the remote directory listing of **every** context/tab, without switching the visible tab and without any external process.
- Tabs that are disconnected or busy (active transfers) are skipped via `CState::IsRemoteConnected()` / `CState::IsRemoteIdle()`.
- Menu: **Server > Auto-refresh all tabs**
  - `Enabled` (checkable, on by default)
  - `Set interval...` (1–3600 seconds, default 20)
- Both settings are stored in the regular FileZilla options (`filezilla.xml`), so they persist across restarts.

## Local API, MCP, and CLI (development)

The local API is an opt-in source patch, separate from auto-refresh. It does
not automate tabs, expose passwords, or add remote shell access. Enable it in
the patched build using **Server > API > Enabled**. It is disabled by default.
The already-installed auto-refresh build does not have this API until a new
build is explicitly installed.

Transport: UTF-8 newline-delimited JSON-RPC 2.0 over the Windows named pipe
`filezilla-api-<PID>`. The pipe permits only the Windows user running FileZilla
and rejects remote clients. There is no HTTP listener and no token in this
initial implementation. Other programs running as that user can access it;
the ACL does not protect against malware running under the same account.

Initial methods:

| Method | Behavior |
| --- | --- |
| `api.info` | API version and process information |
| `sessions.list` | Opaque session IDs, connection state, and paths |
| `sessions.files` | Paginated cached remote listing, not file contents |
| `sessions.refresh` | Request a remote refresh on one connected, idle session |
| `queue.summary` | Aggregate queue state |

Session IDs are valid only for the current FileZilla process. A refresh response
means that a listing was requested, not that the server has already returned it.
Fetch the cached listing again after completion. No connect, upload, download,
delete, rename, arbitrary FTP command, or SSH execution method is provided.
The listing reports `cached: true`, `stale: true` (no freshness guarantee), and
`available: false` when no cached listing exists. Pagination uses `offset >= 0`
and `limit` from 1 through 1000. Queue summary exposes `active` and `file_count`,
not per-file progress. API refresh rejects synchronized browsing as well as busy
or disconnected sessions, avoiding a synchronous modal dialog from that path.

Build and test the bridge with the .NET 8 SDK:

```powershell
dotnet run --project bridge/tests/FileZillaBridge.Tests.csproj -c Release
dotnet publish bridge/FileZillaBridge.csproj -c Release -r win-arm64 --self-contained true -o bridge-artifact
```

The self-contained ARM64 artifact includes its runtime. The MCP entry point uses
stdio; the same executable also provides CLI commands. MCP configuration in a
client such as Kilo is a separate opt-in step and is not changed by this build.
When multiple FileZilla API endpoints are present, select the process explicitly
instead of relying on an arbitrary tab/window selection.

Examples from the published bridge directory:

```powershell
.\filezilla-bridge.exe info
.\filezilla-bridge.exe sessions list
.\filezilla-bridge.exe --pid 1234 files list --session s-1 --limit 100
.\filezilla-bridge.exe --pid 1234 refresh --session s-1
.\filezilla-bridge.exe queue summary
.\filezilla-bridge.exe mcp
```

Use session IDs returned by `sessions list`, not tab indexes or titles.
The MCP tools are `sessions_list`, `sessions_files`, `sessions_refresh`, and
`queue_summary`. The server negotiates MCP `2024-11-05` or `2025-03-26` and
writes protocol messages only to stdout. Diagnostics go to stderr. An MCP
client should launch `filezilla-bridge.exe` with argument `mcp` (plus
`--pid <PID>` if needed); this README does not install client configuration.

The bridge checks the actual pipe server PID and requires its executable name
to be `filezilla.exe`. This rejects a pipe claimed by an unrelated process;
it is not a code-signature check and does not establish trust in arbitrary
executables renamed by the same Windows user.

The existing CI uploads `filezilla_arm64` (patched application). Publish the
bridge locally with the command above. A separate bridge CI job is prepared in
local commit `6779de9`, but was not pushed because the current GitHub OAuth
grant lacks the `workflow` scope. This branch deliberately preserves the
existing workflow and does not request additional account permissions.
Mock bridge tests do not
replace validation against a running patched FileZilla instance. Live testing
must use a separate configuration and must not replace or close an existing
FileZilla session without permission.

## Running FileZilla

You can directly run `filezilla.exe` after extracting the Zip archive. The settings of FileZilla are placed in `C:\Users\<user_name>\AppData\Roaming\FileZilla\`.

### Install FileZilla shell extension

With the shell extension you can drag and drop files from a remote server in FileZilla to the File Explorer of Windows. Run
```
regsvr32  .\fzshellext_64.dll
```
in Command Prompt, and then reboot the system.
