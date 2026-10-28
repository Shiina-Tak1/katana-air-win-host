namespace KatanaAirWinHost.Http;

/// <summary>Self-contained status page served at /__status; no external resources.</summary>
internal static class StatusPage
{
    /// <summary>Page HTML; state is loaded from /__status.json every 2 seconds.</summary>
    public const string Html = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Katana Air Win Host Status</title>
<style>
:root{color-scheme:dark light}
*{box-sizing:border-box}
body{margin:0;font-family:system-ui,"Segoe UI",sans-serif;background:#11151a;color:#e6edf3}
main{max-width:760px;margin:0 auto;padding:28px 20px 40px}
h1{font-size:20px;font-weight:600;margin:0 0 4px}
.sub{color:#8b98a5;font-size:13px;margin:0 0 18px}
.pill{display:inline-block;padding:3px 10px;border-radius:999px;font-size:12px;font-weight:600;background:#2d333b;color:#adbac7}
.pill.ok{background:#1b4721;color:#7ee787}
.pill.bad{background:#4d1d1d;color:#ff9b9b}
dl{display:grid;grid-template-columns:220px 1fr;gap:1px;margin:0 0 22px;border:1px solid #2d333b;border-radius:8px;overflow:hidden;background:#2d333b}
dt,dd{margin:0;padding:9px 14px;background:#161b22;font-size:13px}
dt{color:#8b98a5}
dd{font-family:ui-monospace,Consolas,monospace;word-break:break-all}
dd.err{color:#ff9b9b}
.actions{display:flex;align-items:center;gap:10px;flex-wrap:wrap}
button{font:inherit;font-size:13px;font-weight:600;padding:8px 16px;border-radius:6px;border:1px solid #444c56;background:#22272e;color:#e6edf3;cursor:pointer}
button:hover:not(:disabled){background:#2d333b}
button:disabled{opacity:.55;cursor:default}
#note{font-size:12px;color:#8b98a5}
textarea{width:100%;height:240px;margin-top:16px;font-family:ui-monospace,Consolas,monospace;font-size:12px;background:#0d1117;color:#e6edf3;border:1px solid #444c56;border-radius:6px;padding:10px}
</style>
</head>
<body>
<main>
<h1>Katana Air Win Host</h1>
<p class="sub">Local status of the running host. Refreshes every 2 seconds.</p>
<p><span id="state" class="pill">Checking&#8230;</span></p>
<dl id="fields"></dl>
<div class="actions">
<button id="reconnect" type="button">Reconnect</button>
<button id="copy" type="button">Copy diagnostics</button>
<span id="note" role="status"></span>
</div>
<textarea id="raw" hidden readonly spellcheck="false" aria-label="Diagnostics JSON"></textarea>
</main>
<script>
(function () {
  "use strict";

  var FIELDS = [
    ["webRoot", "Web root"],
    ["port", "Port"],
    ["model", "Model"],
    ["frontend.version", "Frontend version"],
    ["frontend.build", "Frontend build"],
    ["ble.connected", "BLE connected"],
    ["ble.name", "BLE name"],
    ["ble.address", "BLE address"],
    ["ble.notify", "Notifications"],
    ["ble.paired", "Paired in Windows"],
    ["ble.lastError", "Last error"],
    ["midi.tx", "MIDI sent"],
    ["midi.rx", "MIDI received"]
  ];

  var list = document.getElementById("fields");
  var cells = {};
  FIELDS.forEach(function (field) {
    var term = document.createElement("dt");
    term.textContent = field[1];
    var value = document.createElement("dd");
    value.textContent = "\u2014";
    list.appendChild(term);
    list.appendChild(value);
    cells[field[0]] = value;
  });

  var state = document.getElementById("state");
  var note = document.getElementById("note");
  var raw = document.getElementById("raw");
  var reconnectButton = document.getElementById("reconnect");
  var copyButton = document.getElementById("copy");

  function read(data, key) {
    return key.split(".").reduce(function (value, part) {
      return value && typeof value === "object" ? value[part] : undefined;
    }, data);
  }

  function text(value) {
    if (value === null || value === undefined) return "\u2014";
    if (typeof value === "boolean") return value ? "yes" : "no";
    if (value === "") return "\u2014";
    return String(value);
  }

  function render(data) {
    FIELDS.forEach(function (field) {
      var cell = cells[field[0]];
      var value = read(data, field[0]);
      cell.textContent = text(value);
      cell.classList.toggle("err", field[0] === "ble.lastError" && !!value);
    });

    var connected = read(data, "ble.connected") === true;
    state.textContent = connected ? "Connected" : "Disconnected";
    state.className = "pill " + (connected ? "ok" : "bad");
  }

  function unavailable(message) {
    state.textContent = "Host unreachable";
    state.className = "pill bad";
    note.textContent = message;
  }

  function refresh() {
    fetch("/__status.json", { cache: "no-store" })
      .then(function (response) { return response.json(); })
      .then(render)
      .catch(function (error) { unavailable(error.message); });
  }

  function copyFallback(text) {
    raw.hidden = false;
    raw.value = text;
    raw.focus();
    raw.select();
    var copied = false;
    try { copied = document.execCommand("copy"); } catch (e) { copied = false; }
    note.textContent = copied ? "Diagnostics copied." : "Press Ctrl+C to copy the selected text.";
  }

  reconnectButton.addEventListener("click", function () {
    reconnectButton.disabled = true;
    note.textContent = "Reconnecting\u2026";
    fetch("/__status/reconnect", { method: "POST" })
      .then(function () {
        note.textContent = "Reconnect requested.";
        setTimeout(refresh, 600);
        setTimeout(refresh, 1800);
        setTimeout(refresh, 4000);
      })
      .catch(function (error) { note.textContent = "Reconnect failed: " + error.message; })
      .then(function () { reconnectButton.disabled = false; });
  });

  copyButton.addEventListener("click", function () {
    fetch("/__status.json", { cache: "no-store" })
      .then(function (response) { return response.text(); })
      .then(function (text) {
        if (!navigator.clipboard || !navigator.clipboard.writeText) {
          copyFallback(text);
          return;
        }
        navigator.clipboard.writeText(text)
          .then(function () { note.textContent = "Diagnostics copied."; })
          .catch(function () { copyFallback(text); });
      })
      .catch(function (error) { note.textContent = "Copy failed: " + error.message; });
  });

  refresh();
  setInterval(refresh, 2000);
})();
</script>
</body>
</html>
""";
}
