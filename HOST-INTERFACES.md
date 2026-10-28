# Katana Air Win Host — Host Interfaces

| Item               | Value                                                       |
| ------------------ | ----------------------------------------------------------- |
| Product            | Katana Air Win Host                                         |
| Executable         | `KatanaAirWinHost.exe`                                      |
| Frontend           | BOSS TONE STUDIO for KATANA-AIR, version 2.1.0 (see README) |
| Frontend package   | `jp.co.roland.boss_katana_air_editor`                       |
| Frontend resources | Supplied locally; not part of this repository               |

## 1. Summary

| Interface                         | Size                                      | Defined in                                            |
| --------------------------------- | ----------------------------------------- | ----------------------------------------------------- |
| HTTP route classes                | 5                                         | `Http/HttpServer.cs`                                  |
| `$$` names in `SupportedCommands` | 55 (50 fixed names + 5 `$$btx_*`)         | `Bridge/NativeBridge.cs`                              |
| Prefix-forwarded command families | 2 (`$$ytplayer_*`, `$$btx_*`)             | `Bridge/YtPlayerBridge.cs`, `Bridge/BtxBridge.cs`     |
| Event names                       | 16                                        | §4                                                    |
| Command-line options              | 12                                        | `Cli/CliOptions.cs`                                   |
| Environment variables             | 3                                         | `Host/AppHost.cs`, `Bridge/CloudProxy.cs`             |
| Exit codes                        | 0, 1, 2                                   | `Program.cs`, `Host/AppHost.cs`, `Cli/Diagnostics.cs` |
| Storage keys written by the host  | 3 (`""`, `ble.address`, `dialog.lastdir`) | `Bridge/StorageStore.cs`                              |

## 2. HTTP interface

### 2.1 Server behaviour

| Property               | Value                                                                                                                            |
| ---------------------- | -------------------------------------------------------------------------------------------------------------------------------- |
| Listener               | `TcpListener` on `IPAddress.Loopback`                                                                                            |
| Port                   | `--port <n>`, otherwise a system-assigned port                                                                                   |
| Protocol               | HTTP/1.1 subset: request head, one response, `Connection: close`; the request method and body are not inspected, only the target |
| Request head limit     | 16 KiB initial buffer, doubled on demand, 8 MiB hard limit; over the limit the connection is closed without a response           |
| Timeouts               | 10 s receive, 10 s send                                                                                                          |
| Response headers       | `Content-Type`, `Content-Length`, `Cache-Control: no-store`, `Access-Control-Allow-Origin: *`, `Connection: close`               |
| `$$` handler exception | Response `v`; one `[cmd] <name>: <message>` line on stderr                                                                       |
| Process model          | One process: HTTP server, `$$` dispatch, BLE link                                                                                |

### 2.2 Route table

| Route                                | Response                                                        | Notes                                                                                                                                              |
| ------------------------------------ | --------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------- |
| `/$$<name>[?<arg1>[,<arg2>]]`        | `text/plain; charset=utf-8`, body = encoded return value (§3.2) | Any target starting with `/$$`; the leading `/` is stripped; the handler receives `$$<name>`                                                       |
| `/__debug`                           | `application/json; charset=utf-8` state snapshot                | §6.1                                                                                                                                               |
| `/__proxy?url=<percent-encoded URL>` | Upstream status, `Content-Type`, body                           | Whitelisted hosts only; §2.4                                                                                                                       |
| `/__host/<file>`                     | `text/javascript; charset=utf-8`, embedded script               | 404 `inject resource not found`; names restricted to ASCII letters, digits, `.`, `_`, `-`; no `..`; set fixed at build time from `Web/inject/*.js` |
| Any other target                     | Static file from the web root                                   | `/` maps to `/index.html`; §2.3                                                                                                                    |

### 2.3 Static files and injection

| Step                     | Behaviour                                                                                                                                                                                                   |
| ------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Decoding                 | Percent-decoded; on failure the raw target is used                                                                                                                                                          |
| Mapping                  | `/` maps to `/index.html`; path separators become `\`; resolved under the web root                                                                                                                          |
| Rejection                | A resolved path outside the web root, or a missing file, returns 404 `not found` (`text/plain; charset=utf-8`)                                                                                              |
| MIME types               | By extension: `.html`, `.js`, `.css`, `.json`, `.png`, `.jpg`/`.jpeg`, `.gif`, `.svg`, `.ico`, `.ttf`, `.otf`, `.woff`, `.woff2`, `.txt`/`.map`; otherwise `application/octet-stream`; text types are UTF-8 |
| Injection target         | A response whose file name is `index.html`, any directory, case-insensitive; files on disk are never modified                                                                                               |
| head.js                  | `<script src="/__host/head.js"></script>` immediately after the `<head ...>` start tag                                                                                                                      |
| tail.js                  | `<script src="/__host/tail.js"></script>` immediately before `</body`                                                                                                                                       |
| Missing marker or script | No `<head>`: head tag prepended. No `</body>`: tail tag appended. Missing embedded script: no tag inserted. One stderr warning per case                                                                     |

### 2.4 `/__proxy`

| Rule               | Value                                                                                                                                                                          |
| ------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Allowed hosts      | `api.roland.com`, `bosstonecentral.com`, `www.bosstonecentral.com`, `api.soundcloud.com`, `w.soundcloud.com`                                                                   |
| Allowed schemes    | `http`, `https`                                                                                                                                                                |
| Redirects          | Followed manually; each hop matched against the whitelist again; at most 3 hops                                                                                                |
| Timeout            | 10 s total, 5 s connect                                                                                                                                                        |
| `KAWH_PROXY_OFF=1` | Every request returns 403 `proxy disabled (KAWH_PROXY_OFF=1)`                                                                                                                  |
| Upstream status    | Body and `Content-Type` passed through                                                                                                                                         |
| Error replies      | 400 `url` missing or not absolute; 403 disabled proxy, disallowed scheme or host; 502 upstream error, redirect without `Location`, or too many redirects; 504 upstream timeout |

## 3. `$$` protocol

### 3.1 Request form

```
GET http://localhost:<port>/$$<name>?<arg1>,<arg2>
```

| Rule              | Value                                                                                                                                                                |
| ----------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Command placement | Path segment prefixed with `$$` (`$$app_ready`, `$$midi_send`, …); case-sensitive                                                                                    |
| Arguments         | Two, separated by `,`, each percent-encoded independently                                                                                                            |
| Argument values   | Positional, already percent-decoded; "JSON" denotes JSON text carried as the argument                                                                                |
| Decoding          | Split on `,`, then percent-decode each part; absent arguments are empty strings                                                                                      |
| No arguments      | No `?` in the URL; an empty query yields an empty argument list                                                                                                      |
| Response          | Raw body whose first character is a type tag; no JSON envelope                                                                                                       |
| Dispatch order    | Fixed `switch` cases in `NativeBridge.Handle`, then the `$$ytplayer_` prefix (`YtPlayerBridge`), then the `$$btx_` prefix (`BtxBridge`), then the default case (`v`) |

### 3.2 Response encoding

| Prefix          | Frontend value            | Used for                                                |
| --------------- | ------------------------- | ------------------------------------------------------- |
| `v`             | `undefined`               | No return value; every unknown or unimplemented command |
| `s`             | string                    | Every command that returns data                         |
| `b` / `d` / `f` | boolean / integer / float | Not used by any current command                         |

### 3.3 Command table — `app`

| Command                                                 | Arguments                             | Return                           | Behaviour                                                                                                                                              |
| ------------------------------------------------------- | ------------------------------------- | -------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `$$app_ready`                                           | —                                     | `v`                              | No-op; device-list seeding is not attached to it                                                                                                       |
| `$$app_startevent`                                      | optional, ignored                     | `v`                              | `v` is mandatory; any other value ends `$$app_getevent` polling in the frontend. The first call starts the `app.onScanned` burst                       |
| `$$app_getevent`                                        | —                                     | `s<event>` or `v`                | Dequeues one event; `v` when empty                                                                                                                     |
| `$$app_storage`, `$$app_storage2`                       | `[text]` / `key[, value]`             | `v` on write, `s<value>` on read | `$$app_storage` uses the empty key `""`; in `$$app_storage2` an empty `value` deletes the key                                                          |
| `$$app_control`, `$$app_rotation`, `$$app_setSleepable` | `request` / `0` or `1` / `bool`       | `v`                              | No-op                                                                                                                                                  |
| `$$app_exit`                                            | —                                     | `v`                              | Returns `v`, then after 250 ms closes the Edge window this process launched, releases the bridge, dialog thread and HTTP server, and exits with code 0 |
| `$$app_clipboard`                                       | `[text]`                              | `s` (empty string)               | The clipboard is neither read nor written                                                                                                              |
| `$$app_startScan`                                       | —                                     | `v`                              | Pushes `app.onScanned` immediately                                                                                                                     |
| `$$app_toggleConnectBLEDevice`                          | JSON endpoint                         | `v`                              | Pushes `midi.changed`; no connection attempt                                                                                                           |
| `$$app_importfile`, `$$app_importmultiplefile`          | filter JSON, e.g. `["kat"]`           | `v`                              | Open dialog; on selection pushes `app.command("import", <path>)` or `app.command("import", <JSON array string>)`; cancel pushes nothing                |
| `$$app_exportfile`                                      | `path` relative to the data directory | `v`                              | Save dialog, then copy to the chosen path                                                                                                              |
| `$$app_exportmultiplefile`                              | JSON array of paths                   | `v`                              | Folder picker, then copy each file into it                                                                                                             |

### 3.4 Command table — `midi`

| Command                                       | Arguments               | Return          | Behaviour                                                                                  |
| --------------------------------------------- | ----------------------- | --------------- | ------------------------------------------------------------------------------------------ |
| `$$midi_inendpoints`                          | —                       | `s<JSON array>` | One synthesized `IN` endpoint                                                              |
| `$$midi_outendpoints`                         | —                       | `s<JSON array>` | One synthesized `OUT` endpoint                                                             |
| `$$midi_inconnect`, `$$midi_outconnect`       | endpoint JSON (ignored) | `v`             | Opens the single BLE link if it is not open; pushes `midi.changed` or `midi.connectfailed` |
| `$$midi_indisconnect`, `$$midi_outdisconnect` | endpoint JSON (ignored) | `v`             | Closes the link; pushes `midi.changed` if it was open                                      |
| `$$midi_send`                                 | hex string              | `v`             | Hex → bytes → BLE-MIDI frames → GATT writes (§7); non-hex characters are discarded         |
| `$$midi_panel`                                | —                       | `v`             | No-op                                                                                      |

| Endpoint rule                                            | Value                                                                                                                                                                                                     |
| -------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Required keys                                            | `MIDIDeviceNameKey`, `MIDIEndpointUIDKey`, `MIDIEndpointIndexKey`, `MIDIEntityNameKey`                                                                                                                    |
| `MIDIEndpointIndexKey`                                   | Identical for the `IN` and `OUT` endpoints                                                                                                                                                                |
| Name prefix                                              | `KATANA-AIR`                                                                                                                                                                                              |
| Example, `OUT` variant (`IN` uses `(Bluetooth MIDI IN)`) | `[{"MIDIDeviceNameKey":"KATANA-AIR MIDI (Bluetooth MIDI OUT)","MIDIEndpointUIDKey":"KATANA-AIR-BLE","MIDIEndpointIndexKey":"KATANA-AIR-BLE","MIDIEntityNameKey":"KATANA-AIR MIDI (Bluetooth MIDI OUT)"}]` |

### 3.5 Command table — `fs`

| Command                                | Arguments           | Return          | Behaviour                                                                                                                                                                                                                           |
| -------------------------------------- | ------------------- | --------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `$$fs_separator`                       | —                   | `s\`            | One backslash                                                                                                                                                                                                                       |
| `$$fs_path`                            | `where`             | `s<directory>`  | `temporary` → `<data>\temporary\`; `documents` → the user's Documents directory with a trailing separator, or `<data>\documents\` when it does not exist; any other value or none → the data directory without a trailing separator |
| `$$fs_volumes`                         | —                   | `s<JSON array>` | Drive roots, e.g. `["C:\\","D:\\"]`                                                                                                                                                                                                 |
| `$$fs_stat`                            | `path`              | `s<JSON>`       | File: `{"size":<n>,"mtime":<unix ms>,"directory":false}`; directory: `{"size":0,"mtime":0,"directory":true}`; missing: `{}`                                                                                                         |
| `$$fs_contents`                        | `directory`         | `s<JSON array>` | `[{"name":…,"size":…,"directory":bool}]`; missing directory returns `[]`                                                                                                                                                            |
| `$$fs_mkdir`                           | `path`              | `v`             | Creates the directory, including parents                                                                                                                                                                                            |
| `$$fs_unlink`                          | `path`              | `v`             | Deletes a file; directories are ignored                                                                                                                                                                                             |
| `$$fs_copy`, `$$fs_move`               | `from, to`          | `v`             | File copy or move, overwrite                                                                                                                                                                                                        |
| `$$fs_unzip`                           | `zip, folder`       | `v`             | Extracts into `folder`, or `<data>\<zip name>` when `folder` is empty; overwrite                                                                                                                                                    |
| `$$fs_readString`                      | `path`              | `s<text>`       | UTF-8 text; missing file returns an empty string                                                                                                                                                                                    |
| `$$fs_readData`                        | `path`              | `s<HEX>`        | Uppercase hex of the file bytes; missing file returns an empty string                                                                                                                                                               |
| `$$fs_writeString`, `$$fs_writeData`   | `path, text`        | `v`             | UTF-8 without BOM, overwrite; `$$fs_writeData` writes the text as-is                                                                                                                                                                |
| `$$fs_appendString`, `$$fs_appendData` | `path, text`        | `v`             | UTF-8 without BOM, appended; `$$fs_appendData` appends the text as-is                                                                                                                                                               |
| `$$fs_savefilename`                    | `name[, extension]` | `v`             | Save dialog; pushes `fs.savefilename(<path>)`, or an empty string on cancel                                                                                                                                                         |
| `$$fs_openfilename`                    | filter JSON         | `v`             | Single-selection open dialog; pushes `fs.openfilename(<path>)`, or an empty string on cancel                                                                                                                                        |
| `$$fs_openfilenamev2`                  | filter JSON         | `v`             | Multi-selection; pushes `fs.openfilename(<JSON array string>)`, or an empty string on cancel                                                                                                                                        |

| Note                           | Value                                                                             |
| ------------------------------ | --------------------------------------------------------------------------------- |
| `$$fs_path` trailing separator | `temporary` and `documents` results end with `\`                                  |
| Path resolution                | Relative paths against the data directory; absolute paths as given                |
| `$$fs_readData` output         | Uppercase hex; input to `$$util_text`                                             |
| Filter argument forms          | JSON array of extensions without dots (`["kat"]`), JSON string, or bare extension |
| Dialog command return          | `v` before the dialog closes; result arrives as an event                          |

### 3.6 Command table — `security` / `util` / `host_event`

| Command                                        | Arguments                                                 | Return                              | Behaviour                                                                       |
| ---------------------------------------------- | --------------------------------------------------------- | ----------------------------------- | ------------------------------------------------------------------------------- |
| `$$security_kiv`                               | `salt`                                                    | `s0123456789ABCDEF0123456789ABCDEF` | Fixed constant                                                                  |
| `$$security_uuidgen`                           | —                                                         | `s<GUID>`                           | New GUID, uppercase, with hyphens                                               |
| `$$security_aesdecrypt`, `$$security_decipher` | `data[, key]`                                             | `s` (empty string)                  | No decryption performed                                                         |
| `$$util_text`                                  | hex                                                       | `s<text>`                           | Hex → bytes → UTF-8 text; invalid input returns an empty string                 |
| `$$util_bytes`                                 | text                                                      | `s<HEX>`                            | UTF-8 bytes → uppercase hex                                                     |
| `$$host_event`                                 | `prop\|type\|arg1\|arg2…` as one percent-encoded argument | `v`                                 | Replaces every `\|` with `0x0C` and enqueues the result as an event string (§4) |

### 3.7 Prefix families

| Family         | Names                                                                                                                                                                                                                                                                                                                            | Behaviour                                                                                                                                                                              |
| -------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `$$ytplayer_*` | `$$ytplayer_loadYT`, `$$ytplayer_openVideoId`, `$$ytplayer_playYT`, `$$ytplayer_pauseYT`, `$$ytplayer_seekToYT`, `$$ytplayer_seekRel`, `$$ytplayer_currentTime`, `$$ytplayer_setPlaybackRate`, `$$ytplayer_getPlaybackRate`, `$$ytplayer_getAvailablePlaybackRates`, `$$ytplayer_getDuration`, `$$ytplayer_displayScreenYt` (12) | Enqueues `app.command` with `param1 = "__yt"` and `param2 = {"cmd":"<cmd>","args":[…]}`; `tail.js` executes it (§9.3). Any suffix is forwarded; an unknown name is ignored by the page |
| `$$btx_*`      | `$$btx_viewPositionConfig`, `$$btx_show`, `$$btx_hide`, `$$btx_openPage`, `$$btx_sendMsgToBTX` (5, listed in `SupportedCommands`)                                                                                                                                                                                                | Enqueues `app.command` with `param1 = "__btx"` and `param2 = {"cmd":"<cmd>","args":[…]}`; `tail.js` executes it (§9.4)                                                                 |

### 3.8 Commands not implemented

| Scope               | Behaviour                                                                                                                                                                                                                                                                                                                       |
| ------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Any other `$$` name | Returns `v` (`undefined`); no action                                                                                                                                                                                                                                                                                            |
| Groups              | `midix_*`, `tgif_*`, `player_*`, `recorder_*`, `sampler_*`, `audio_*`, `rwc_*`, `http_*`, `tcpip_*`, `netservice_*`, `ble_*`, `store_*`, `notification_*`, `recognizer_*`, `barcode_*`, `sh_*`                                                                                                                                  |
| Individual names    | `$$app_locate`, `$$fs_exec`, `$$fs_unmount`, `$$fs_openmediafile`, `$$fs_importmediafile`, `$$fs_deletemediafile`, `$$fs_mediafileinfo`, `$$fs_getUrlScheme`, `$$fs_importmediafilemigratefromcachetofiles`, `$$security_md5`, `$$security_encipher`, `$$security_aesencrypt`, `$$security_desencrypt`, `$$security_desdecrypt` |

## 4. Events

### 4.1 Encoding and delivery

| Property       | Value                                                                   |
| -------------- | ----------------------------------------------------------------------- |
| Event string   | `<prop> 0x0C <type> 0x0C <arg1> 0x0C <arg2> …`                          |
| Separator      | `0x0C` (form feed); an argument never contains it                       |
| Arguments      | All strings; the frontend applies them positionally                     |
| Delivery       | The frontend polls `$$app_getevent`; the host does not push to the page |
| Empty queue    | `$$app_getevent` returns `v`                                            |
| Queue capacity | 2000 events; the oldest are dropped beyond that                         |
| Debug history  | The most recent 50 events, reported by `/__debug` only                  |
| Poll interval  | Set by `tail.js` to 10 ms (frontend default 50 ms)                      |

### 4.2 Event table

| Event                               | Arguments                                                                                              | Pushed when                                                                                                                          |
| ----------------------------------- | ------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------ |
| `app.onScanned`                     | JSON **string** with one synthesized `OUT` endpoint; parsed by `tail.js` before the frontend's handler | First `$$app_startevent`, again at +500/+1500/+3000/+5000 ms; every `$$app_startScan`                                                |
| `app.command`                       | `param1`, `param2`                                                                                     | Import finished (`"import"` + path or JSON array), YouTube forwarding (`"__yt"` + JSON), Tone Exchange forwarding (`"__btx"` + JSON) |
| `midi.changed`                      | —                                                                                                      | Link established, link released, `$$app_toggleConnectBLEDevice`                                                                      |
| `midi.connectfailed`                | device name (`KATANA-AIR MIDI`)                                                                        | No usable address, device open failure, GATT failure                                                                                 |
| `midi.message`                      | uppercase hex, decimal Unix time in milliseconds                                                       | One complete MIDI message: received data up to and including `F7`                                                                    |
| `fs.savefilename`                   | chosen path, or an empty string on cancel                                                              | Save dialog closed                                                                                                                   |
| `fs.openfilename`                   | chosen path; a JSON array string for `$$fs_openfilenamev2`; an empty string on cancel                  | Open dialog closed                                                                                                                   |
| `ytplayer.onReady`                  | `"1"`                                                                                                  | Player reports ready                                                                                                                 |
| `ytplayer.onStateChange`            | player state code as a decimal string                                                                  | Player state changes                                                                                                                 |
| `ytplayer.onCurrentTime`            | position in seconds                                                                                    | `$$ytplayer_seekRel`, `$$ytplayer_currentTime`                                                                                       |
| `ytplayer.onDuration`               | duration in seconds                                                                                    | `$$ytplayer_getDuration`                                                                                                             |
| `ytplayer.onPlaybackRate`           | rate                                                                                                   | `$$ytplayer_setPlaybackRate`, `$$ytplayer_getPlaybackRate`, player rate change                                                       |
| `ytplayer.onAvailablePlaybackRates` | JSON array string                                                                                      | `$$ytplayer_getAvailablePlaybackRates`                                                                                               |
| `ytplayer.error`                    | player error code, or `iframe_api` when the API script fails to load                                   | Player error or API load failure                                                                                                     |
| `btx.onSendMsgToBTS`                | percent-encoded JSON string                                                                            | A message arrives from the Tone Exchange iframe, or a synthesized handshake reply (§9.4)                                             |
| `btx.onBtxWebViewNotify`            | `"0"` on load, `"258"` on error                                                                        | Tone Exchange iframe load state                                                                                                      |

## 5. Command-line interface

### 5.1 Options

| Option               | Effect                                                                                                                                          |
| -------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------- |
| *(none)*             | Start the server and open the interface in an Edge application window                                                                           |
| `--dev`              | Open a normal Edge window (address bar, DevTools) instead of an application window                                                              |
| `--no-browser`       | Start the server only; no browser is launched and `$$app_exit` has no window to close                                                           |
| `--port <n>`         | Fixed listen port, 1–65535; default is a system-assigned free port                                                                              |
| `--web-root <dir>`   | Frontend directory; must contain `index.html` directly                                                                                          |
| `--model <id>`       | Model profile: `katana-air` (default) or `katana-air-ex`                                                                                        |
| `--addr <HEX>`       | Bluetooth address: diagnostic commands use it instead of scanning; in app mode it overrides `storage.json` for this run and is not written back |
| `--ble-scan`         | Scan, connect, read the characteristic and send a Device Inquiry (§6.2)                                                                         |
| `--ble-pair`         | Pair with the amplifier (§6.2)                                                                                                                  |
| `--ble-diag`         | List paired BLE devices and their GATT services (§6.3)                                                                                          |
| `--list-midi`        | List WinMM MIDI ports (§6.3)                                                                                                                    |
| `--list-winrt-midi`  | List WinRT MIDI ports (§6.3)                                                                                                                    |
| `--help`, `-h`, `/?` | Print the option list; exit code 0                                                                                                              |

| Parsing rule                                  | Behaviour                                                                                                                                                                       |
| --------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Diagnostic mode                               | At most one per run; a second is a parse error                                                                                                                                  |
| Value taking                                  | Any option order; each value is the next argument                                                                                                                               |
| `--addr` value                                | 1–16 hex digits, optional `0x` prefix, case-insensitive, stored uppercase                                                                                                       |
| `--port` value                                | Integer 1–65535                                                                                                                                                                 |
| `--web-root` value                            | Non-empty                                                                                                                                                                       |
| `--model` value                               | `katana-air`, `katana-air-ex`                                                                                                                                                   |
| Frontend directory resolution                 | `--web-root`, `KAWH_WEB_ROOT`, then the first directory within eight levels above the executable that contains `web/index.html`; the directory must contain `index.html` itself |
| Startup output                                | Interface URL, `/__debug` URL, frontend directory, data directory, WinMM input ports                                                                                            |
| Unknown option, extra argument, missing value | Parse error                                                                                                                                                                     |

### 5.2 Environment variables

| Variable         | Effect                                                          |
| ---------------- | --------------------------------------------------------------- |
| `KAWH_WEB_ROOT`  | Frontend directory; below `--web-root`, above the upward search |
| `KAWH_DATA_DIR`  | Data directory; default `<exe directory>\data`                  |
| `KAWH_PROXY_OFF` | `1` disables `/__proxy`; every request returns 403              |

### 5.3 Exit codes

| Code | Situations                                                                                                                                                                                                                                        |
| ---- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 0    | `--help`; normal shutdown (Ctrl+C or `$$app_exit`); a diagnostic command that completed successfully                                                                                                                                              |
| 1    | Frontend directory not found, or it has no `index.html`; listen port unavailable; `--ble-scan` when the device cannot be opened; `--ble-pair` when every attempt failed. The reason is printed to stderr                                          |
| 2    | Command-line parse error, including unknown model (message and help text go to stderr); `--ble-scan` when no device of the selected model was found; `--ble-pair` when no device was found, it could not be opened, or no pairing information is available; unknown diagnostic mode |

## 6. Diagnostics

### 6.1 `GET /__debug`

| Field                                           | Content                                                                                                     |
| ----------------------------------------------- | ----------------------------------------------------------------------------------------------------------- |
| `commands`                                      | Last 200 commands, `HH:mm:ss.fff $$<name> <arg1>\|<arg2>…`                                                  |
| `supportedCommands`                             | The 55 names from `NativeBridge.SupportedCommands`; excludes `$$ytplayer_*`                                 |
| `events`                                        | Last 50 enqueued event strings                                                                              |
| `ble.connected`, `ble.address`, `ble.name`      | Link object present; 12-digit uppercase hex; device name reported by the link                               |
| `ble.notify`, `ble.lastError`                   | Whether the CCCD subscription succeeded; last recorded transport error                                      |
| `midi.tx`, `midi.rx`                            | Frames sent; complete MIDI messages received                                                                |
| `midi.sysexTruncated`, `midi.pendingSysexBytes` | SysEx fragments discarded (malformed or over the buffer limit); bytes buffered for the open SysEx           |
| `midiTrace`                                     | Last 500 transport records, `HH:mm:ss.fff <TX\|RX\|RX*\|!!> <detail>`, including every notification (`RX*`) |
| `port`, `webRoot`                               | Listening port; resolved frontend directory                                                                 |

### 6.2 `--ble-scan` and `--ble-pair`

| Command                       | Steps                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                |
| ----------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `--ble-scan [--addr <HEX12>]` | Active advertisement scan for 8 s (unless `--addr`), printing every device with name, RSSI and address; picks the first name beginning with `KATANA-AIR` (no match: hint, exit code 2); opens and connects; prints `gatt ok, notify=<bool>` and one characteristic read; sends the Device Inquiry frame `F07E000601F7` with Write Without Response; after 3 s prints every received MIDI message in hex. On a connection error it prints the error, attempts pairing when the device reports `CanPair` and is not paired, then retries the link once |
| `--ble-pair [--addr <HEX12>]` | Passive scan for 4 s (unless `--addr`), with no scan requests to the amplifier while it is in pairing mode; prints `IsPaired`/`CanPair`; tries custom pairing with `ConfirmOnly \| ProvidePin \| DisplayPin` against `EncryptionAndAuthentication`, `Encryption`, `Default`, `None`, in that order; only `Paired` or `AlreadyPaired` counts as success                                                                                                                                                                                               |

### 6.3 `--ble-diag`, `--list-midi`, `--list-winrt-midi`

| Command             | Output                                                                                                                                                                                                                                                                      |
| ------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `--ble-diag`        | For each paired BLE device: id, open result and duration, connection status, name, address, GATT service list; for the BLE-MIDI service also every characteristic UUID and its properties, including whether GATT access returns `Success`, `AccessDenied` or `Unreachable` |
| `--list-midi`       | WinMM input and output ports with indices; Bluetooth LE devices are not listed                                                                                                                                                                                              |
| `--list-winrt-midi` | WinRT MIDI endpoints. Enumerating WinRT MIDI can hand the amplifier to the Windows Bluetooth MIDI stack; direct GATT access then reports `Unreachable`. The command prints this warning. The host never enumerates WinRT MIDI on startup                                    |

## 7. BLE-MIDI transport

### 7.1 Service and characteristic

| Item                                 | Value                                  |
| ------------------------------------ | -------------------------------------- |
| Device name                          | `KATANA-AIR MIDI`                      |
| Name prefix used for discovery       | `KATANA-AIR` (case-insensitive)        |
| MIDI service                         | `03B80E5A-EDE8-4B33-A751-6CE34EC4C700` |
| MIDI characteristic (write + notify) | `7772E5DB-3868-4112-A1A9-F2669D106BF3` |

Model profiles: `katana-air` (default) and `katana-air-ex`. The KATANA-AIR EX profile reuses the
KATANA-AIR values; it has not been verified on KATANA-AIR EX hardware.

### 7.2 Frame layout and timestamps

| Item                  | Value                                                                                                                            |
| --------------------- | -------------------------------------------------------------------------------------------------------------------------------- |
| Header byte           | `0x80 \| ((ts >> 7) & 0x3F)`                                                                                                     |
| Timestamp byte        | `0x80 \| (ts & 0x7F)`                                                                                                            |
| Timestamp source      | `counter++ & 0x1FFF`, 13-bit, monotonically increasing, not clock-based                                                          |
| Decoded timestamp     | `((header & 0x3F) << 7) \| (tsByte & 0x7F)`                                                                                      |
| Write layout          | `[header][tsByte][MIDI bytes…]`                                                                                                  |
| SysEx extra timestamp | A second `tsByte` immediately before the terminating `F7`                                                                        |
| SysEx write layout    | `[header] [ts] [F0] [7E] [00] [06] [01] [ts] [F7]`                                                                               |
| Amplifier requirement | A timestamp in front of every MIDI message and a separate timestamp before `F7`; a frame without the second timestamp is ignored |

### 7.3 Unframing and SysEx reassembly

| Unframing rule     | Value                                                                                                                                            |
| ------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------ |
| Instance           | One `BleMidiUnframer` per connection; SysEx state persists across notifications                                                                  |
| Header / timestamp | A first byte with bit 7 set is skipped; a following byte with bit 7 set is also skipped                                                          |
| Inside SysEx       | 7-bit bytes and `F7` kept; other bit-7 bytes dropped, including a timestamp whose low seven bits equal `0x70` (byte `0xF0`); `F7` ends the SysEx |
| Outside SysEx      | `F0` starts a SysEx; other bit-7 bytes dropped; 7-bit bytes kept                                                                                 |
| Header bit 6       | Part of the 13-bit timestamp; no "another timestamp follows" meaning                                                                             |

| Reassembly case                                          | Action                                                                                   |
| -------------------------------------------------------- | ---------------------------------------------------------------------------------------- |
| Accumulation                                             | Unframed bytes buffer until `F7`; each completed message is pushed as one `midi.message` |
| `F7` reached                                             | Emit the buffer as one message, clear it                                                 |
| Fragment shorter than 2 bytes, or not starting with `F0` | Discard, increment `sysexTruncated`                                                      |
| A new `F0` while a SysEx is open                         | Discard the open buffer, restart at the new `F0`, increment `sysexTruncated`             |
| Buffer exceeds 512 KiB                                   | Clear it, increment `sysexTruncated`                                                     |

### 7.4 Write rules

| Rule                   | Value                                                                                                                                                       |
| ---------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Write type             | Write Without Response for every MIDI write                                                                                                                 |
| Maximum per GATT write | 20 bytes                                                                                                                                                    |
| MIDI payload per write | At most 17 bytes, leaving room for the header and the two timestamps                                                                                        |
| Chunking               | A payload longer than 17 bytes is split into 17-byte pieces; each piece is framed independently with its own header and timestamp                           |
| Ordering               | Writes are serialized; a failed write is logged to stderr; while disconnected the payload is dropped and one stderr line is printed per disconnected period |

### 7.5 Connection sequence and constraints

| Step | Action                                                                                                                                             |
| ---- | -------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1    | Resolve an address (§7.6), open the device with `BluetoothLEDevice.FromBluetoothAddressAsync`                                                      |
| 2    | Create a `GattSession` from the device id; set `MaintainConnection = true`                                                                         |
| 3    | Enumerate services with `Uncached` caching: at most 2 attempts, 900 ms apart, with a fresh `BluetoothLEDevice.FromIdAsync` between attempts        |
| 4    | Select the BLE-MIDI service; call `RequestAccessAsync` on it before requesting characteristics                                                     |
| 5    | Request characteristics with `Uncached` caching: at most 2 attempts, 400 ms apart, with a device reopen in between                                 |
| 6    | Subscribe with a CCCD write of `Notify`; a failure is logged and `NotificationsEnabled` stays `false`, and the connection object is still returned |

| Constraint             | Value                                                                                    |
| ---------------------- | ---------------------------------------------------------------------------------------- |
| Connect call           | Blocking on the calling thread (`GetAwaiter().GetResult()`); not inside `Task.Run`       |
| Link encryption        | Required; a CCCD write on an unencrypted link returns `0x8065000F`                       |
| Concurrent connections | One                                                                                      |
| Connect window         | Total within the frontend's 30000 ms `WAIT_TIME_FOR_DEVICE_CONNECTION` (`bts_config.js`) |
| Notification thread    | WinRT callback thread; the handler unframes and enqueues                                 |

### 7.6 Address discovery

| Priority | Source                                                                              | Written back         |
| -------- | ----------------------------------------------------------------------------------- | -------------------- |
| 1        | `--addr`                                                                            | No                   |
| 2        | `storage.json` → `ble.address`                                                      | —                    |
| 3        | Advertisement scan (active, 5 s, first local name starting with `KATANA-AIR`)       | Yes, as 12-digit hex |
| 4        | Paired-device enumeration (first paired device whose name starts with `KATANA-AIR`) | Yes, as 12-digit hex |

| Condition                                 | Behaviour                                                                        |
| ----------------------------------------- | -------------------------------------------------------------------------------- |
| Amplifier already paired, not advertising | Priority 4 applies                                                               |
| No address found                          | Pushes `midi.connectfailed`; logs `[ble] no device address (scan found nothing)` |

## 8. Data directory and `storage.json`

| Item                       | Value                                                                                                  |
| -------------------------- | ------------------------------------------------------------------------------------------------------ |
| Data directory             | `KAWH_DATA_DIR`, otherwise `<exe directory>\data`; created on startup                                  |
| Storage file               | `<data>\storage.json`, a JSON object of string → string, rewritten on every mutation; no flush at exit |
| Missing or unreadable file | Treated as an empty store (read failures are logged)                                                   |
| Deleting a key             | A write with an empty value removes it                                                                 |
| Subdirectories             | `temporary\` and `documents\` are created on demand by `$$fs_path`                                     |
| `ble.address` source       | `--addr` if given, otherwise `storage.json`                                                            |
| `--addr` persistence       | Not written to `storage.json`                                                                          |

| Key              | Written by       | Content                                                              |
| ---------------- | ---------------- | -------------------------------------------------------------------- |
| `""` (empty key) | `$$app_storage`  | One opaque string chosen by the frontend (application state)         |
| `ble.address`    | Host             | 12-digit uppercase hex address, filled in after discovery            |
| `dialog.lastdir` | Host             | Last directory used by a file dialog                                 |
| any other key    | `$$app_storage2` | Values the frontend stores by key, e.g. `LAST_CONNECTED_MIDI_DEVICE` |

## 9. Injected page scripts

| Item           | Value                                          |
| -------------- | ---------------------------------------------- |
| Embedding      | Assembly resources, served at `/__host/<file>` |
| Frontend files | Served unchanged                               |

### 9.1 `head.js` — before the frontend's scripts

| Responsibility                           | Detail                                                                                                                                                                                                                                |
| ---------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| User agent                               | Overrides `navigator.userAgent` with a Windows Chrome user agent string containing `roland.quattro(KatanaAirWinHost)` and no Android marker                                                                                           |
| `window.$$xhrport`                       | `location.port`, or `443`/`80` when the port is empty; the frontend builds `http://localhost:<port>` from it                                                                                                                          |
| `window.__kawh`                          | Shared handle `{ call, emit, host }`: `call(name, args)` performs a synchronous XHR to `/$$…` and returns the raw response text; `emit(prop, type, args)` posts `prop\|type\|args…` to `$$host_event` as one percent-encoded argument |
| `window.webkit.messageHandlers.quattro1` | `postMessage(req)` splits `req` on `0x0C`, calls the host with the remaining parts, stores the raw result in `window[req]`                                                                                                            |
| `window.webkit.messageHandlers.quattro2` | Same split and host call; result discarded                                                                                                                                                                                            |

### 9.2 `tail.js` — after the frontend's scripts

| Hook                                           | Purpose                                                                                                                                                                  |
| ---------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `jQuery.ajax` rewrite                          | Requests to whitelisted hosts are rewritten to `/__proxy?url=<encoded>`; `dataType: 'script'` and `'jsonp'` are left alone. The patched function carries `__kawh = true` |
| `$native.app.event.onScanned` interceptor      | `JSON.parse` on a string argument before the frontend's handler                                                                                                          |
| `$native.app.event.command` hook               | `param1 === "__yt"` or `"__btx"` parsed and dispatched to the page-side tables (§9.3, §9.4); other values go to the previous handler                                     |
| `$native.btx.event.onSendMsgToBTS` interceptor | `decodeURIComponent` before the frontend's handler; `window.message` events from the Tone Exchange iframe are re-emitted here as a percent-encoded payload               |
| `$event.start` override                        | Forces a 10 ms poll interval (frontend default 50 ms)                                                                                                                    |
| YouTube API bootstrap                          | Loads `https://www.youtube.com/iframe_api` and wraps any existing `window.onYouTubeIframeAPIReady` handler                                                               |

### 9.3 Page-side `$$ytplayer_*` commands

| Command                               | Action                                                                                             |
| ------------------------------------- | -------------------------------------------------------------------------------------------------- |
| `loadYT`                              | `cueVideoById`: loads without playing                                                              |
| `openVideoId`                         | Loads the video; does not show the player                                                          |
| `playYT` / `pauseYT`                  | `playVideo` / `pauseVideo`                                                                         |
| `seekToYT`                            | `seekTo(seconds)`                                                                                  |
| `seekRel`, `currentTime`              | Relative or current position; commands 1–3 seek backwards, 4–6 forwards; both emit `onCurrentTime` |
| `setPlaybackRate` / `getPlaybackRate` | Set or query the rate; emits `onPlaybackRate`                                                      |
| `getAvailablePlaybackRates`           | Emits `onAvailablePlaybackRates` as a JSON array string                                            |
| `getDuration`                         | Emits `onDuration`                                                                                 |
| `displayScreenYt`                     | The only command that shows or hides the player                                                    |

| Player property  | Value                                                                                       |
| ---------------- | ------------------------------------------------------------------------------------------- |
| Container        | `#kawh-yt-box`; player in `#kawh-yt-player`                                                 |
| Default geometry | 400×225, 16 px from the right, 96 px from the bottom                                        |
| Visibility       | Hidden until `displayScreenYt(true)`                                                        |
| Drag handle      | `#kawh-yt-grip`; position stored in `localStorage` under `kawh.yt.pos`; double-click resets |

### 9.4 Page-side `$$btx_*` commands

| Command              | Action                                                                                                                                                                                                                                                                                  |
| -------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `viewPositionConfig` | Parses `{"x","y","width","height"}` and applies it to the iframe container; ignored while width or height is 0                                                                                                                                                                          |
| `openPage`           | Creates the iframe and sets `src`; loads only, never shows                                                                                                                                                                                                                              |
| `show` / `hide`      | Displays the container with the last rect, or hides it                                                                                                                                                                                                                                  |
| `sendMsgToBTX`       | Posts the message into the iframe and answers two environment handshakes itself: `requestCheckRegionIsChina` → `{"cmd":"responseCheckRegionIsChina","arg":{"result":"false"}}`, `requestEvaluateSignInSession` → `{"cmd":"responseEvaluateSignInSession","arg":{"result":"signedOut"}}` |

| Container property  | Value                                                           |
| ------------------- | --------------------------------------------------------------- |
| Elements            | `#kawh-btx-box`, iframe `#kawh-btx-frame`                       |
| Load / error events | `btx.onBtxWebViewNotify("0")` / `btx.onBtxWebViewNotify("258")` |
| Message exchange    | iframe `postMessage`                                            |
