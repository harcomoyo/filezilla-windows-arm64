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

## How to use

You can directly run `filezilla.exe` after extracting the Zip archive. The settings of FileZilla are placed in `C:\Users\<user_name>\AppData\Roaming\FileZilla\`.

### Install FileZilla shell extension

With the shell extension you can drag and drop files from a remote server in FileZilla to the File Explorer of Windows. Run 
```
regsvr32  .\fzshellext_64.dll
```
in Command Prompt, and then reboot the system.
