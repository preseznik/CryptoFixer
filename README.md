# CryptoFixer

CryptoFixer is a Windows desktop app (WPF) that shows which **Schannel security protocols, ciphers, hashes and key exchange algorithms** are enabled on a Windows Server or Windows machine and lets you enable or disable them.

It manages the standard Schannel registry settings:

```
HKLM\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\
    Protocols\<Protocol>\{Server|Client}
        Enabled            (DWORD)
        DisabledByDefault  (DWORD)
    Ciphers\<Cipher>                 Enabled (DWORD)
    Hashes\<Hash>                    Enabled (DWORD)
    KeyExchangeAlgorithms\<Name>     Enabled (DWORD)
```

- **Protocols** (per Server and Client role): PCT 1.0, Multi-Protocol Unified Hello, SSL 2.0, SSL 3.0, TLS 1.0, TLS 1.1, TLS 1.2, TLS 1.3, DTLS 1.0, DTLS 1.2.
- **Ciphers**: NULL, DES 56/56, RC2 40/128, 56/128 and 128/128, RC4 40/128, 56/128, 64/128 and 128/128, Triple DES 168 (and the legacy key name Triple DES 168/168), AES 128/128, AES 256/256.
- **Hashes**: MD5, SHA (SHA-1), SHA256, SHA384, SHA512.
- **Key exchange**: PKCS (RSA), Diffie-Hellman, ECDH. The Diffie-Hellman and PKCS key-size values are shown read-only.

It works on the local computer and on **remote machines** (over WinRM or DCOM; see [Remote machines](#remote-machines)).

**Download:** get `CryptoFixer-win-x64.exe` (or the zip) from the [latest release](https://github.com/preseznik/CryptoFixer/releases/latest). It is a single self-contained exe: no installer and no .NET runtime needed. The exe isn't code-signed, so Windows SmartScreen may warn the first time you run it (*More info* → *Run anyway*). You can check the download against the `.sha256` file published with the release.

![Main window, compact view, with the machines panel open and the Modern preset staged](docs/screenshot.png)

| Compact view (default) | Standard view |
|---|---|
| ![Compact view](docs/screenshot-compact.png) | ![Standard view](docs/screenshot-standard.png) |
| **List view** | **Remote machine (read-only connection)** |
| ![List view](docs/screenshot-list.png) | ![Remote machine](docs/screenshot-remote.png) |
| **Ciphers section** | **Ciphers section, List view** |
| ![Ciphers section](docs/screenshot-ciphers.png) | ![Ciphers section in List view](docs/screenshot-ciphers-list.png) |

## Requirements

- Windows Server 2016 or later, or Windows 10/11. Older builds also work, and their OS defaults are shown.
- **Administrator rights** to make changes. The executable asks for elevation (UAC). Without elevation it opens **read-only**.
- **A restart is required** before Schannel changes (protocols, ciphers, hashes and key exchange) take effect.
- For remote machines: WinRM (or DCOM) access to the target and administrator rights there. See [Remote machines](#remote-machines).
- No installer and no .NET runtime: the release is one self-contained `CryptoFixer.exe` (see [Installing on a server](#installing-on-a-server)).
- To build: the .NET 10 SDK. Self-contained builds don't need a .NET runtime on the server.

## Using the app

The **section switcher** above the table selects *Protocols*, *Ciphers*, *Hashes* or *Key exchange*. Staged changes are kept per section, and **Apply** writes the changes from all sections together.

Each protocol has a **Server** and a **Client** cell. Each cell shows:

- **Setting** (drop-down):
  - `OS default (…)`: no registry values, so Windows uses its built-in default for this build (shown in brackets).
  - `Enabled`: writes `Enabled=1`, `DisabledByDefault=0`.
  - `Disabled`: writes `Enabled=0`, `DisabledByDefault=1`.
  - `Custom (keep current values)`: appears only when the registry already holds a combination this app doesn't write, such as `Enabled=1` with `DisabledByDefault=1`. The app leaves those values unchanged.
- **Effective state**: what Schannel actually uses. It is one of *Enabled*, *Enabled (on request only)* (meaning `DisabledByDefault=1`, so only apps that explicitly ask for the protocol can use it), *Disabled*, or *Not supported* on this Windows build (any registry values are ignored).
- **Source**: either *Set in registry* or *OS default (not configured)*. Hover over a cell to see the raw registry values.

### Ciphers, hashes and key exchange

These settings have no Server/Client split. Each algorithm has one **Enabled** setting that applies to all Schannel connections:

- `OS default (…)`: no `Enabled` value. The bracket shows what this Windows build does by default (see [OS defaults](#os-defaults)).
- `Enabled`: writes `Enabled=0xffffffff`.
- `Disabled`: writes `Enabled=0`, which blocks the algorithm in every cipher suite that uses it.

Any other non-zero value counts as enabled (Microsoft uses `1` for Diffie-Hellman). Reset removes only the `Enabled` value; Diffie-Hellman and PKCS key sizes (`ServerMinKeyBitLength`, `ClientMinKeyBitLength`, `ClientMaxKeyBitLength`) are shown in the tooltip and are never changed.

Markers:

- **Deprecated**: NULL, DES, RC2, RC4 and Triple DES (like SSL and TLS 1.0/1.1 among the protocols). The *Deprecated* filter shows them.
- **`*` / inferred**: Microsoft doesn't state this default; it is inferred from the cipher suite tables.
- **undocumented**: Microsoft doesn't document this key at all (AES, SHA256/384/512 and ECDH). Schannel does read it, but test before relying on it.
- **Cipher suites** column: how many suites in the active cipher suite list use the algorithm, for example `6 suites`, `none listed`, or `4 listed · blocked` when the algorithm is disabled but suites that need it are still listed (those suites are then unusable). Hover for the suite names. See [Ciphers and cipher suites](#ciphers-and-cipher-suites).

### Workflow

1. Change the drop-downs. Changes are **staged** (highlighted) and nothing is written yet.
2. Click **Apply changes…**. A confirmation lists every change and shows warnings, for example:
   - no TLS 1.2/1.3 would be left for a role, which breaks RDP, WinRM and HTTPS
   - you are enabling a deprecated or unsupported protocol, or a deprecated cipher
   - you are disabling TLS 1.0/1.1 that is still in use
   - both AES ciphers, SHA-1, PKCS, ECDH or every key exchange algorithm would be disabled
3. Before writing, the app **automatically saves a `.reg` backup** of the `Protocols`, `Ciphers`, `Hashes` and `KeyExchangeAlgorithms` trees to `Documents\CryptoFixer\Backups\Schannel-<machine>-<timestamp>.reg`. If the backup fails, nothing is applied.
4. **Restart Windows** for the changes to take effect. The app offers to restart (with a 10-second delay) or you can restart later.

Other actions:

- **Reset to OS default** (per row): removes `Enabled` and `DisabledByDefault` for both roles of a protocol, or `Enabled` for an algorithm. To reset everything, use the *OS defaults* preset.
- **Presets ▾**: stages a built-in or saved configuration (see [Presets](#presets)).
- **Export backup…**: saves a `.reg` backup on demand.
- **Discard**: drops all staged changes.

### Search, filter and sort

The toolbar above the table narrows and orders the list for the current section. Filters only change what is shown: staged changes stay staged when their rows are hidden, and **Apply** always applies all of them.

- **Search** (Ctrl+F): matches names and descriptions. It ignores case and spaces, so `tls1.2` finds TLS 1.2 and `rc4128` finds RC4 128/128. With several words, all of them must match. Esc clears the search.
- **Show**: *All*, *Enabled*, *Disabled*, *Not supported*, *Set in registry*, *Using OS default*, *Pending changes* or *Deprecated*. A protocol is shown when the selected side(s) match.
- **Side** (Protocols only): *Server and client*, *Server only* or *Client only*. This hides the other column, and the **Show** filter then only looks at that side.
- **Sort**: for protocols *Protocol version* (oldest first, the default), *Name*, *Server state*, *Client state*, *Server: set in registry first* or *Client: set in registry first*. For ciphers, hashes and key exchange *Strength (weakest first)*, *Name*, *State* or *Set in registry first*. The ↑/↓ button reverses the order.
- **Column headers** (Standard and Compact views): click once to sort ascending, again for descending, and a third time to go back to the default order.
- **Clear filters** (next to the result count) resets search, Show and Side.

### Display modes

Use **View** (right of the result count) to switch how the protocols are shown. Search, filters, sorting and staged changes work the same in every mode.

- **Standard**: the full table with descriptions and the full pending-change text under each setting.
- **Compact** (the default): one line per protocol, with smaller text and short labels (for example `Enabled · OS default` or `Enabled → Disabled`, and *Reset* for *Reset to OS default*). The description and raw registry values are in tooltips. When the window is narrow, for example with the machines panel open, long labels are shortened with "…"; hover over a cell for the full text.
- **List**: one card per protocol with the description, its Server and Client settings side by side, and a *pending change* badge. List has no column headers, so sort with the **Sort** drop-down.

The selected mode is remembered in `CryptoFixer.settings.json` next to the executable (see [Portable files](#portable-files)). Compact is used when no mode has been saved yet, for example on a new install; a saved choice always wins.

### Presets

A preset is a named set of protocol, cipher, hash and key exchange settings. Choosing a preset from **Presets ▾** only **stages** its settings. Review them and click **Apply changes…** as usual.

Built-in presets:

- **Modern: TLS 1.2 and 1.3 only**:
  - disables PCT, Unified Hello, SSL 2.0/3.0 and TLS 1.0/1.1
  - disables DTLS 1.0 where DTLS 1.2 is available
  - enables TLS 1.2, TLS 1.3 and DTLS 1.2 where this Windows build supports them, and removes their settings where it doesn't
  - disables the NULL, DES, RC2, RC4 and Triple DES ciphers and enables AES 128 and AES 256
  - leaves hashes and key exchange unchanged

  Check TLS 1.0/1.1 and 3DES compatibility (for example very old clients or appliances) before applying it.
- **OS defaults: remove all settings**: removes every protocol `Enabled`/`DisabledByDefault` value and every cipher, hash and key exchange `Enabled` value so Windows uses its built-in defaults. Key-size values are kept.

Your own presets:

- **Save current settings as preset** saves the value of every drop-down, including staged changes, under a name. Saving under an existing name asks before overwriting. Custom values can't be stored and are left out.
- **Delete preset** removes one saved preset.
- **Create presets file and open in Notepad** (shown as **Edit presets file in Notepad** once the file exists) lets you write presets by hand. Use **Reload presets file** afterwards.

Presets are stored in `CryptoFixer.presets.json` next to the executable, so they travel with the app when you copy its folder to another server. The file is plain JSON:

```jsonc
{
  "formatVersion": 2,
  "help": "…",
  "presets": [
    {
      "name": "Web servers",
      "description": "TLS 1.2+ for IIS, keep TLS 1.0 client for a legacy partner",
      "savedAt": "2026-11-20T14:05:00+01:00",   // optional, informational
      "savedFrom": "WEB01, Windows Server 2022 Datacenter (build 20348)", // optional, informational
      "protocols": {
        "SSL 3.0": "disabled",                                // same value for Server and Client
        "TLS 1.0": { "server": "disabled", "client": "enabled" },
        "TLS 1.1": "disabled",
        "TLS 1.2": "enabled",
        "TLS 1.3": { "server": "osDefault" }                  // only the Server side
      },
      "ciphers": {                                            // optional (format version 2)
        "Triple DES 168": "disabled",
        "RC4 128/128": "disabled",
        "AES 256/256": "enabled"
      },
      "hashes": { "MD5": "disabled" },                        // optional
      "keyExchange": { "Diffie-Hellman": "osDefault" }        // optional
    }
  ]
}
```

- Protocol names are the registry key names shown in the app (`PCT 1.0`, `Multi-Protocol Unified Hello`, `SSL 2.0` … `DTLS 1.2`). Case doesn't matter.
- Cipher, hash and key exchange names are the registry key names too, including the `/` (for example `RC4 128/128`, `Triple DES 168/168`, `SHA`, `PKCS`, `ECDH`). Case doesn't matter.
- Values are `enabled`, `disabled` or `osDefault` (removes the registry values). `on`/`off`, `enable`/`disable` and `default`/`notConfigured` are accepted too.
- Protocols, sides or algorithms that a preset leaves out are not changed when you load it.
- `Enabled` is not staged for a protocol that this Windows build doesn't support. The status line reports what was skipped.
- Files written by version 1.1 and earlier (`"formatVersion": 1`, protocols only) load unchanged. CryptoFixer writes version 2 only when a preset contains cipher, hash or key exchange settings, so protocol-only files stay at version 1. Version 1.1 loads a version 2 file with a warning and ignores the new sections, but drops them if it saves the file, so don't share a presets file between old and new versions.
- Reading is lenient. Comments and trailing commas are allowed, and invalid entries are skipped with a warning in the status line. Comments are lost when the app saves the file.
- If the file can't be parsed, the app shows the error and keeps it unchanged. When you next save a preset, it offers to rename the broken file to `CryptoFixer.presets.json.invalid-<date>` and start a new one.

### Portable files

CryptoFixer keeps its own files next to `CryptoFixer.exe` (resolved from the executable's folder, not `%APPDATA%`):

| File | Contents | Created |
|---|---|---|
| `CryptoFixer.presets.json` | Your saved presets | When you save the first preset, or with *Create presets file…* |
| `CryptoFixer.settings.json` | `{ "formatVersion": 1, "displayMode": "standard" \| "compact" \| "list", "checkForUpdates": true \| false }` | When you change the display mode or the update check setting |
| `CryptoFixer.machines.json` | Your remote machines (see [Remote machines](#remote-machines)); passwords only DPAPI-encrypted | When you add the first machine |

A missing file means "no presets", "default settings" or "no remote machines". A corrupt settings file is reported in the status line, and the app uses the defaults (Compact view, update check on) without overwriting the file until you change a setting. The folder must be writable for saving. If it isn't, the app tells you and keeps working.

### Restoring a backup

A backup restores the `Protocols`, `Ciphers`, `Hashes` and `KeyExchangeAlgorithms` trees to their earlier state. It also removes any `Enabled`/`DisabledByDefault` values the app added afterwards.

```powershell
reg import "$env:USERPROFILE\Documents\CryptoFixer\Backups\Schannel-<machine>-<timestamp>.reg"
```

Then restart Windows.

A backup of a remote machine is saved **on this PC** and named after it (`Schannel-<host>-<timestamp>.reg`). The file uses plain `HKEY_LOCAL_MACHINE` paths, so copy it to that machine and run the same `reg import` there, then restart it. Backups made by version 1.1 and earlier are named `Schannel-Protocols-…` and cover only the protocols.

### Read-only mode

Start with `--read-only` (or `/readonly`) to view the configuration without allowing changes, even when elevated. The app also opens read-only when it runs without administrator rights. In read-only mode the registry layer itself refuses writes. `--read-only` also makes every remote connection read-only.

You can still stage settings and presets to preview their effect, and you can save presets. **Apply changes…** and the restart prompt are disabled.

### Updates

CryptoFixer updates itself from the [public releases of this repository](https://github.com/preseznik/CryptoFixer/releases). The header shows the running version and the result of the last update check; **Updates ▾** has *Check for updates now*, *Check for updates at startup* and *Open releases page*.

- **Startup check.** A couple of seconds after start, the app asks the GitHub API (`https://api.github.com/repos/preseznik/CryptoFixer/releases/latest`, unauthenticated, through the system proxy) for the latest release. It runs in the background and never blocks the window. Being offline, a GitHub rate limit or a missing release only changes the text next to the version; hover over it for details. Drafts and prereleases are never offered.
- **Turning it off.** Untick *Updates ▾ › Check for updates at startup*, or set `"checkForUpdates": false` in `CryptoFixer.settings.json` next to the exe. *Check for updates now* still works.
- **Installing.** When a newer version exists, a blue bar offers it. *Update now…* shows the release notes, then:
  1. downloads `CryptoFixer-win-x64.exe` from the release into `%TEMP%\CryptoFixer-update\`,
  2. checks its SHA-256 hash against the release's `.sha256` file and refuses to install on a mismatch (it also checks that the file reports the release's version),
  3. renames the running `CryptoFixer.exe` to `CryptoFixer.exe.old`, moves the new exe into place and restarts with the same command-line arguments and elevation,
  4. deletes `CryptoFixer.exe.old` on the next start and reports "Updated from X to Y" in the status line.

  If anything fails before the new exe is in place, the old version is kept (or put back). `CryptoFixer.presets.json` and `CryptoFixer.settings.json` are never touched. Staged changes that weren't applied are discarded by the restart; the dialog warns about them.
- **When it can't update itself**, for example when the exe's folder isn't writable (such as `Program Files` without elevation) or you run a development build, the dialog offers the release page instead so you can replace the exe by hand.
- **Trust.** The SHA-256 check protects against corrupted or incomplete downloads. It is not a signature: both files come from the same GitHub release, so it can't detect a compromised release. The exe isn't code-signed, which is also why SmartScreen may warn about it.
- Updating works in read-only mode too. It never changes the Schannel registry settings.

## Remote machines

![Connection error with an explanation](docs/screenshot-remote-error.png)

Click **☰** (left of the title) or press **Ctrl+M** to slide out the **Machines** panel. Click ✕ or press Esc to close it (in the filter box, Esc clears the filter first).

- **This computer** is always first and can't be removed.
- **Select a machine** to connect and show its settings. The header shows which computer you are looking at; a blue chip means a remote machine. Everything works as it does locally: search, filters, sorting, presets, staging, **Apply changes…**, backups, OS defaults (detected from the remote Windows build) and the restart prompt. Switching machines with staged changes asks before discarding them.
- **Add…** opens a dialog for the host name or IP address, a display name, the connection type and the credentials. **Edit…** (or F2) changes an entry. **Remove** (or Delete) asks first. Right-click an entry for *Connect*, *Edit…* and *Remove…*.
- **Filter** matches the name, host name or IP address.
- **Sort** by *Name* or *IP address*. IP sorting is numeric: IPv4 addresses octet by octet (so `192.0.2.4` comes before `192.0.2.15`), then IPv6 addresses, then host names alphabetically.
- **Status dot**: grey = not connected yet, amber = connecting, green = connected, red = failed. Hover over an entry for details.

If a connection fails, the table area explains why and offers *Try again*, *Edit connection…* and *Back to this computer*. The app recognises access denied, wrong user name or password, WinRM not reachable or firewalled, host not found, no response, Kerberos not usable (IP addresses and non-domain targets), HTTPS certificate problems, and DCOM/RPC not reachable.

### Connections and credentials

CryptoFixer uses **CIM/WMI** (the `StdRegProv` registry provider) to read and write the same `SCHANNEL\Protocols` values on the target. Connection types:

| Connection | Port | Notes |
|---|---|---|
| **WinRM (HTTP)**, recommended | 5985 | On by default on Windows Server 2012 and later. Traffic is encrypted by Kerberos/NTLM. |
| **WinRM (HTTPS)** | 5986 | Needs an HTTPS listener with a certificate the client trusts. Use it for IP addresses and computers outside your domain. |
| **DCOM** | 135 plus dynamic RPC ports | Fallback for computers without WinRM. Usually blocked by firewalls. |

Credentials are either **your current Windows account** (Kerberos single sign-on) or **another account** (`DOMAIN\user`, `user@domain` or `.\user` for a local account on the target) with a password.

- **Passwords are never stored in plain text.** If you tick *Save the password (encrypted)*, it is encrypted with Windows **DPAPI** for the current user and stored in `CryptoFixer.machines.json`. Only the same Windows user on the same PC can decrypt it. Anywhere else (another PC, or another user) CryptoFixer asks for the password again instead of failing.
- Unsaved passwords are kept in memory until CryptoFixer closes. Passwords are never shown or logged.
- If the target rejects the user name or password, the next attempt asks for it again.
- The current Windows account can't be replaced by other credentials for `localhost`; that is a WMI rule.

### Prerequisites on the target

- **Administrator rights** on the target for the account you connect with. By default, WinRM and remote WMI only accept administrators.
- **WinRM enabled** (default on Windows Server 2012+). If it isn't, run `Enable-PSRemoting` (or `winrm quickconfig`) on the target. Allow TCP 5985 (and 5986 for HTTPS) in its firewall.
- **IP addresses and non-domain computers.** Kerberos only works with domain computer names. For an IP address, or a computer outside your domain, either use **WinRM (HTTPS)**, or add the target to the WinRM **TrustedHosts** list on *this* computer and connect with a user name and password (NTLM), for example:

  ```powershell
  Set-Item WSMan:\localhost\Client\TrustedHosts -Value 10.0.0.5 -Concatenate
  ```

  CryptoFixer never changes TrustedHosts, WinRM, the firewall or any other system configuration; it tells you what to change.
- **Local accounts** (not domain accounts) in the Administrators group are filtered by UAC over the network. Use the built-in Administrator account or a domain account, or set `LocalAccountTokenFilterPolicy=1` on the target (`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System`); know the security trade-off first.
- **DCOM**: allow *Windows Management Instrumentation (DCOM-In)* and the WMI rules in the target's firewall.

### Applying changes remotely

- Before writing, CryptoFixer saves a `.reg` backup of the target's Schannel trees **on this PC**, named after the target (see [Restoring a backup](#restoring-a-backup)). If the backup fails, nothing is applied.
- Ciphers, hashes and key exchange work on remote machines exactly as they do locally. Key names that contain `/` (such as `RC4 128/128`) are created as a single key through StdRegProv `CreateKey`.
- After applying, it offers to **restart the remote machine** (Win32_OperatingSystem `Win32ShutdownTracker`: a planned, forced restart after about 10 seconds with a comment in the event log). You can also restart later with the *Restart &lt;name&gt;…* button in the restart notice.
- Whether you can apply changes to a remote machine depends on the account used for the connection, not on whether CryptoFixer runs elevated. `--read-only` makes every connection read-only.

### Machine list file

`CryptoFixer.machines.json` sits next to the exe, like the presets:

```jsonc
{
  "formatVersion": 1,
  "machines": [
    { "name": "web01", "host": "10.0.0.5", "protocol": "wsman", "credential": "current" },
    { "name": "db01", "host": "db01.contoso.com", "protocol": "wsman-https",
      "credential": { "userName": "CONTOSO\\admin", "protectedPassword": "AQAAANCMnd8B…" } }
  ]
}
```

- `protocol` is `wsman`, `wsman-https` or `dcom`.
- `credential` is `"current"` or an object with `userName` and an optional DPAPI-encrypted `protectedPassword`. A plain-text `password` value is ignored (with a warning), and CryptoFixer asks for the password instead.
- Copying the file to another PC copies the machine list, but saved passwords won't decrypt there and are asked for again.

![Add remote machine dialog](docs/screenshot-machine-dialog.png)

## OS defaults

When a protocol is *not configured*, Windows applies a built-in default that depends on the version. CryptoFixer detects the build and labels the defaults accordingly:

| Protocol | Server 2008 R2 / Win 7 (7600) | Server 2012 / 2012 R2 (9200) | Server 2016 / 2019 (14393) | Server 2022 / 2025, Win 11 (20348+) |
|---|---|---|---|---|
| SSL 2.0 | Server: enabled, Client: disabled | Disabled | Not supported | Not supported |
| SSL 3.0 | Enabled | Enabled | Disabled | Disabled |
| TLS 1.0 | Enabled | Enabled | Enabled | Enabled |
| TLS 1.1 | Disabled | Enabled | Enabled | Enabled |
| TLS 1.2 | Disabled | Enabled | Enabled | Enabled |
| TLS 1.3 | Not supported | Not supported | Not supported | Enabled |
| DTLS 1.0 | Enabled | Enabled | Enabled | Enabled |
| DTLS 1.2 | Not supported | Not supported | Enabled | Enabled |
| PCT 1.0 | Not supported | Not supported | Not supported | Not supported |
| Multi-Protocol Unified Hello | Server: enabled, Client: disabled | same | same | same |

Sources:

- [Protocols in TLS/SSL (Schannel SSP)](https://learn.microsoft.com/windows/win32/secauthn/protocols-in-tls-ssl--schannel-ssp-)
- [TLS registry settings](https://learn.microsoft.com/windows-server/security/tls/tls-registry-settings)

The Unified Hello defaults are inferred from the documentation and not stated in it explicitly. TLS 1.0/1.1 are still enabled by default on in-market Windows releases.

### Ciphers, hashes and key exchange defaults

With no `Enabled` value, Windows doesn't block any of these algorithms. The OS default CryptoFixer shows is whether a cipher suite that uses the algorithm is **enabled by default** on that build: *Disabled* means no default suite uses it, so it is effectively off. `*` marks defaults that are inferred rather than stated by Microsoft.

| Algorithm | OS default |
|---|---|
| NULL | Disabled (NULL suites are used only when an application explicitly asks for them) |
| DES 56/56, RC4 40/128, RC4 56/128 | Disabled |
| RC2 40/128, 56/128, 128/128 | Disabled * (Windows 7 and later have no RC2 suites) |
| RC4 64/128 | Disabled * (no suite uses it) |
| RC4 128/128 | Enabled before Windows 10 1709 (build 16299); Disabled from 1709, Server 2019 and later |
| Triple DES 168 | Enabled up to Server 2022 (20348) and Windows 10 21H2; Disabled on Windows 10 22H2 (19045), Windows 11 22H2 and later (22621+), Server 2022 23H2 and Server 2025 |
| Triple DES 168/168 | Same as Triple DES 168 * (pre-Vista key name) |
| AES 128/128, AES 256/256 | Enabled * (key undocumented) |
| MD5 | Enabled (not blocked, although no `*_MD5` suite is enabled by default since Windows 10 1709) |
| SHA, PKCS, Diffie-Hellman | Enabled |
| SHA256, SHA384, SHA512, ECDH | Enabled * (key undocumented) |
| Diffie-Hellman `ServerMinKeyBitLength` | 2048 bits from Windows 10 / Server 2016, 1024 before (read-only) |

Sources:

- [Restrict the use of certain cryptographic algorithms and protocols in Schannel.dll (KB 245030)](https://learn.microsoft.com/troubleshoot/windows-server/windows-security/restrict-cryptographic-algorithms-protocols-schannel)
- [Cipher Suites in TLS/SSL (Schannel SSP)](https://learn.microsoft.com/windows/win32/secauthn/cipher-suites-in-schannel) and the per-version suite tables linked there

## Ciphers and cipher suites

TLS doesn't negotiate ciphers on their own but **cipher suites**, such as `TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384` (key exchange ECDHE, cipher AES 256, hash SHA384) or `TLS_RSA_WITH_3DES_EDE_CBC_SHA` (key exchange RSA/PKCS, cipher Triple DES, hash SHA-1). Windows has two independent controls:

1. **The cipher suite list and order** decides which suites Schannel offers and in which order. Without policy it is the `Functions` value (REG_MULTI_SZ) under `HKLM\SYSTEM\CurrentControlSet\Control\Cryptography\Configuration\Local\SSL\00010002`, managed with `Get-TlsCipherSuite`, `Enable-TlsCipherSuite` and `Disable-TlsCipherSuite`. The Group Policy setting *Computer Configuration → Administrative Templates → Network → SSL Configuration Settings → SSL Cipher Suite Order* writes a comma-separated list to `HKLM\SOFTWARE\Policies\Microsoft\Cryptography\Configuration\SSL\00010002\Functions`, which replaces the local list.
2. **The `SCHANNEL\Ciphers`, `Hashes` and `KeyExchangeAlgorithms` keys** that CryptoFixer manages block an algorithm wherever it occurs.

A suite is used only if it is **in the list and none of its algorithms is blocked**. For 3DES, either `Disable-TlsCipherSuite TLS_RSA_WITH_3DES_EDE_CBC_SHA` or setting *Triple DES 168* to *Disabled* here stops it being used. The suite list is the finer and Microsoft-recommended control; the algorithm keys are a blunt "never use this" switch that also covers suites added to the list later.

CryptoFixer reads the active list (the policy list if it is set, otherwise the local list) **read-only** and shows in the *Cipher suites* column how many listed suites use each algorithm. `listed · blocked` means the algorithm is disabled while suites that need it are still listed; those suites are simply unusable, which is harmless, but you may want to remove them from the list too. CryptoFixer doesn't edit the suite list or order.

TLS 1.3 suites (`TLS_AES_128_GCM_SHA256`, `TLS_AES_256_GCM_SHA384`, `TLS_CHACHA20_POLY1305_SHA256`) don't name a key exchange, and Microsoft doesn't document whether the algorithm keys affect TLS 1.3. The tooltip marks these suites.

## Scope and limitations

- Doesn't edit the cipher suite list or order (it is shown read-only; see [Ciphers and cipher suites](#ciphers-and-cipher-suites)), the elliptic curve order, the Diffie-Hellman/PKCS key sizes (shown read-only), or .NET Framework `SchUseStrongCrypto`/`SystemDefaultTlsVersions`. Microsoft recommends managing cipher suites through cipher suite order (Group Policy or `Enable-TlsCipherSuite`/`Disable-TlsCipherSuite`).
- Settings deployed by Group Policy or configuration management may overwrite changes made here.
- Changing protocol or cipher settings can break connectivity, including RDP, which uses TLS. Keep console or out-of-band access when hardening remote servers.
- Remote machines are handled one at a time; there is no bulk apply across several machines. WinRM itself doesn't depend on Schannel over HTTP (5985), so disabling TLS versions doesn't lock CryptoFixer out of a machine. Over HTTPS (5986) it does use TLS.
- The machine list sort order (Name or IP address) isn't saved; it starts sorted by name.

## Build, test and publish

```powershell
dotnet build CryptoFixer.sln
dotnet test CryptoFixer.sln
```

The tests use an in-memory registry fake, a temporary `HKCU\Software\CryptoFixerTests\<guid>` key and temporary folders for the presets/settings files, all deleted afterwards. They never write to HKLM.

### Building a release

`scripts\publish.ps1` builds the shippable package. It works in Windows PowerShell 5.1 and PowerShell 7:

```powershell
.\scripts\publish.ps1              # version from Directory.Build.props, win-x64
.\scripts\publish.ps1 -SmokeTest   # also starts the packaged exe read-only and closes it again
.\scripts\publish.ps1 -Version 1.2.0 -Runtime win-arm64 -SkipTests
.\scripts\publish.ps1 -OutputDir C:\Temp\cf-build   # package somewhere other than dist\
```

The script:

1. runs the tests (unless `-SkipTests`)
2. publishes a self-contained, compressed single-file exe (ReadyToRun off, since it added about 5 MB with no measurable startup gain; WPF doesn't support trimming)
3. checks that the output is a single exe with the expected version, the `requireAdministrator` manifest and the app icon
4. writes the package to `dist\`:

| Output | Contents |
|---|---|
| `dist\CryptoFixer-<version>-<runtime>\` | `CryptoFixer.exe` (about 59 MB), `README.md` and `docs\` screenshots |
| `dist\CryptoFixer-<version>-<runtime>.zip` | the folder above, zipped |
| `dist\CryptoFixer-<runtime>.exe` | the same exe under the unversioned name the in-app updater downloads |
| `dist\CryptoFixer-<version>-<runtime>.sha256` | SHA-256 hashes of both exes and the zip (`sha256sum` format); the updater verifies against it |

The `.pdb` symbol files stay in `artifacts\publish\<runtime>\`. The equivalent manual command is:

```powershell
dotnet publish src\CryptoFixer\CryptoFixer.csproj -c Release -r win-x64 --self-contained true `
  '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' -o publish
```

### Publishing a GitHub release

1. Bump `<Version>` in `Directory.Build.props`, commit and push.
2. Run `.\scripts\publish.ps1 -SmokeTest`.
3. Create the release with tag `v<version>` (not a draft or prerelease, or the updater ignores it) and attach the three files the updater and users need:

```powershell
gh release create v1.2.0 --title "CryptoFixer 1.2.0" --notes "…" `
  dist\CryptoFixer-win-x64.exe dist\CryptoFixer-1.2.0-win-x64.zip dist\CryptoFixer-1.2.0-win-x64.sha256
```

The updater looks for exactly `CryptoFixer-<runtime>.exe` and a `.sha256` asset that lists it, and refuses releases whose tag isn't a version number. The update dialog shows the release notes as plain text, so keep Markdown formatting in them light (plain lists read fine; `**bold**` shows its asterisks).

### Installing on a server

Copy `CryptoFixer.exe` (or unzip the package) to any folder, for example `C:\Tools\CryptoFixer`, and run it. There is no installer, and the server needs no .NET runtime. The exe prompts for elevation. To check the download, compare `Get-FileHash CryptoFixer.exe` with the `.sha256` file. Keep it in a folder that administrators can write to if you want it to update itself (see [Updates](#updates)).

On first start the exe unpacks a few native WPF DLLs to `%TEMP%\.net\CryptoFixer\`. If policy blocks DLLs in `%TEMP%` (for example AppLocker), set the `DOTNET_BUNDLE_EXTRACT_BASE_DIR` environment variable to an allowed folder.

To carry your presets, view setting and machine list along, copy `CryptoFixer.presets.json`, `CryptoFixer.settings.json` and `CryptoFixer.machines.json` into the same folder as the exe. Saved passwords in the machine list only decrypt for the Windows user who saved them on the PC where they were saved; elsewhere CryptoFixer asks for them again.

The app targets **.NET 10 (LTS)**. .NET 8 reached end of support on 10 November 2026.

## Project layout

```
src/CryptoFixer.Core    Registry abstraction, protocol and algorithm catalogs, OS defaults, state evaluation, apply logic,
                        cipher suite list and mapping (Schannel/), .reg export (Backup/),
                        search/filter/sort (Schannel/ProtocolQuery), presets (Presets/), portable settings (Settings/),
                        GitHub release check, verified download and exe swap (Updates/),
                        remote machines: machine list, IP sorting, DPAPI, CIM/StdRegProv registry, error mapping (Remote/)
src/CryptoFixer         WPF app (MVVM), app.manifest (requireAdministrator), Assets/CryptoFixer.ico
tests/CryptoFixer.Tests xUnit tests
scripts/publish.ps1     Release build and packaging
```
