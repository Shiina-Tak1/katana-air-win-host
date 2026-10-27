//
//  tail.js - injected before </body> of index.html, after all stock scripts.
//  /__proxy rewrite for allowlisted $.ajax; YouTube player via the YouTube IFrame API;
//  stock extension point adapters. No stock file is modified.
//
(function () {
    'use strict';

    var K = window.__kawh;
    if (!K) {
        // head.js absent: exit without touching the page.
        return;
    }

    // ==================================================================================
    // A. Cloud data proxy
    // ==================================================================================

    // Hosts rewritten to /__proxy; the same hosts are in the host allowlist (CloudProxy).
    var PROXY_HOSTS = [
        'api.roland.com',
        'bosstonecentral.com',
        'www.bosstonecentral.com',
        'api.soundcloud.com',
        'w.soundcloud.com'
    ];

    function hostOf(url) {
        try {
            return new URL(url, location.href).hostname.toLowerCase();
        } catch (e) {
            return '';
        }
    }

    function installAjaxProxy() {
        if (!window.jQuery || !window.jQuery.ajax) { return false; }
        var origAjax = window.jQuery.ajax;

        var patched = function (url, options) {
            var settings;
            if (typeof url === 'object' && url !== null) {
                settings = window.jQuery.extend({}, url);
            } else {
                settings = window.jQuery.extend({}, options || {});
                settings.url = url;
            }

            // dataType 'script'/'jsonp' use <script> injection and are not proxied.
            var dt = settings.dataType;
            var bypass = (dt === 'script' || dt === 'jsonp');

            if (!bypass && settings.url && PROXY_HOSTS.indexOf(hostOf(settings.url)) !== -1) {
                settings.url = '/__proxy?url=' + encodeURIComponent(settings.url);
            }

            return origAjax.call(window.jQuery, settings);
        };

        // Debug marker: jQuery.ajax.__kawh is true after installation.
        patched.__kawh = true;
        window.jQuery.ajax = patched;

        return true;
    }

    // ==================================================================================
    // B. YouTube player
    // ==================================================================================

    var YT = {
        apiState: 'idle',     // idle | loading | ready | failed
        player: null,
        playerReady: false,
        box: null,
        inner: null,
        queue: []             // commands received before the player is ready
    };

    function emit(type, args) {
        try {
            K.emit('ytplayer', type, args || []);
        } catch (e) { /* stay silent when the host is absent */ }
    }

    var YT_POS_KEY = 'kawh.yt.pos';
    var YT_DEFAULT = { right: 16, bottom: 96, width: 400, height: 225 };

    function ensureBox() {
        if (YT.box && document.body.contains(YT.box)) { return; }

        var box = document.createElement('div');
        box.id = 'kawh-yt-box';
        box.style.cssText = [
            'position:fixed',
            'right:' + YT_DEFAULT.right + 'px',
            'bottom:' + YT_DEFAULT.bottom + 'px',
            'width:' + YT_DEFAULT.width + 'px',
            'height:' + YT_DEFAULT.height + 'px',
            'z-index:2147483000',
            'background:#000', 'border-radius:8px', 'overflow:hidden',
            'box-shadow:0 10px 30px rgba(0,0,0,.55)',
            'display:none'
        ].join(';');

        var inner = document.createElement('div');
        inner.id = 'kawh-yt-player';
        inner.style.cssText = 'width:100%;height:100%';
        box.appendChild(inner);

        // Drag handle: top-left corner; double-click resets position and size.
        var grip = document.createElement('div');
        grip.id = 'kawh-yt-grip';
        grip.title = 'Drag to move / double-click to reset';
        grip.textContent = '⠿';
        grip.style.cssText = [
            'position:absolute', 'left:4px', 'top:4px', 'width:24px', 'height:24px',
            'line-height:24px', 'text-align:center', 'font-size:16px',
            'color:#fff', 'background:rgba(0,0,0,.45)', 'border-radius:4px',
            'cursor:move', 'user-select:none', 'z-index:2147483001',
            'opacity:.55'
        ].join(';');
        box.appendChild(grip);

        document.body.appendChild(box);
        YT.box = box;
        YT.inner = inner;

        ytInstallDrag(grip);
        ytRestorePos();

        window.addEventListener('resize', ytClamp);
    }

    function ytSavePos() {
        if (!YT.box) { return; }
        try {
            localStorage.setItem(YT_POS_KEY, JSON.stringify({
                left: parseInt(YT.box.style.left, 10),
                top: parseInt(YT.box.style.top, 10),
                width: parseInt(YT.box.style.width, 10),
                height: parseInt(YT.box.style.height, 10)
            }));
        } catch (e) { /* private mode may disable localStorage */ }
    }

    // Clamps the box into the viewport and switches to left/top positioning.
    function ytClamp() {
        if (!YT.box) { return; }
        var w = parseInt(YT.box.style.width, 10) || YT_DEFAULT.width;
        var h = parseInt(YT.box.style.height, 10) || YT_DEFAULT.height;
        var r = YT.box.getBoundingClientRect();
        var maxL = Math.max(0, window.innerWidth - w);
        var maxT = Math.max(0, window.innerHeight - h);
        var l = Math.min(Math.max(0, r.left), maxL);
        var t = Math.min(Math.max(0, r.top), maxT);
        YT.box.style.left = l + 'px';
        YT.box.style.top = t + 'px';
        YT.box.style.right = 'auto';
        YT.box.style.bottom = 'auto';
    }

    function ytRestorePos() {
        var raw = null;
        try { raw = localStorage.getItem(YT_POS_KEY); } catch (e) { return; }
        if (!raw) { return; }
        try {
            var p = JSON.parse(raw);
            if (p && isFinite(p.width) && isFinite(p.height) && p.width >= 160 && p.height >= 90) {
                YT.box.style.width = p.width + 'px';
                YT.box.style.height = p.height + 'px';
                YT.box.style.left = (isFinite(p.left) ? p.left : 0) + 'px';
                YT.box.style.top = (isFinite(p.top) ? p.top : 0) + 'px';
                YT.box.style.right = 'auto';
                YT.box.style.bottom = 'auto';
                ytClamp();
            }
        } catch (e) { /* treat a corrupt entry as absent */ }
    }

    function ytInstallDrag(grip) {
        var dragging = false;
        var dx = 0, dy = 0;

        grip.addEventListener('pointerdown', function (e) {
            if (!YT.box) { return; }
            var r = YT.box.getBoundingClientRect();
            dx = e.clientX - r.left;
            dy = e.clientY - r.top;
            dragging = true;
            try { grip.setPointerCapture(e.pointerId); } catch (x) { }
            e.preventDefault();
        });

        grip.addEventListener('pointermove', function (e) {
            if (!dragging || !YT.box) { return; }
            YT.box.style.left = (e.clientX - dx) + 'px';
            YT.box.style.top = (e.clientY - dy) + 'px';
            YT.box.style.right = 'auto';
            YT.box.style.bottom = 'auto';
            ytClamp();
        });

        function stop(e) {
            if (!dragging) { return; }
            dragging = false;
            try { grip.releasePointerCapture(e.pointerId); } catch (x) { }
            ytSavePos();
        }
        grip.addEventListener('pointerup', stop);
        grip.addEventListener('pointercancel', stop);

        grip.addEventListener('dblclick', function () {
            if (!YT.box) { return; }
            try { localStorage.removeItem(YT_POS_KEY); } catch (e) { }
            YT.box.style.width = YT_DEFAULT.width + 'px';
            YT.box.style.height = YT_DEFAULT.height + 'px';
            YT.box.style.left = 'auto';
            YT.box.style.top = 'auto';
            YT.box.style.right = YT_DEFAULT.right + 'px';
            YT.box.style.bottom = YT_DEFAULT.bottom + 'px';
        });
    }

    function showBox(visible) {
        if (visible) { ensureBox(); }
        if (YT.box) { YT.box.style.display = visible ? 'block' : 'none'; }
    }

    function loadApi() {
        if (YT.apiState !== 'idle') { return; }
        YT.apiState = 'loading';

        // yt_controller.js defines onYouTubeIframeAPIReady; it is wrapped, not replaced.
        var prev = window.onYouTubeIframeAPIReady;
        window.onYouTubeIframeAPIReady = function () {
            YT.apiState = 'ready';
            try {
                if (typeof prev === 'function') { prev(); }
            } catch (e) { /* stock player failure is isolated */ }
            createPlayer();
        };

        var s = document.createElement('script');
        s.src = 'https://www.youtube.com/iframe_api';
        s.async = true;
        s.onerror = function () {
            YT.apiState = 'failed';
            emit('error', ['iframe_api']);
        };
        document.head.appendChild(s);
    }

    function createPlayer() {
        if (YT.player) { return; }
        ensureBox();

        YT.player = new window.YT.Player('kawh-yt-player', {
            width: '100%',
            height: '100%',
            playerVars: {
                controls: 1,
                disablekb: 1,
                modestbranding: 1,
                rel: 0,
                playsinline: 1,
                origin: location.origin
            },
            events: {
                onReady: function () {
                    YT.playerReady = true;
                    // onReady: the frontend calls setAvailable(true) and may call openVideoId.
                    emit('onReady', ['1']);
                    var q = YT.queue;
                    YT.queue = [];
                    for (var i = 0; i < q.length; i++) {
                        try { q[i](); } catch (e) { /* ignore */ }
                    }
                },
                onStateChange: function (e) {
                    emit('onStateChange', [String(e.data)]);
                },
                onPlaybackRateChange: function (e) {
                    emit('onPlaybackRate', [String(e.data)]);
                },
                onError: function (e) {
                    emit('error', [String(e.data)]);
                }
            }
        });
    }

    // Runs fn on the player, or queues it until the player is ready.
    function withPlayer(fn) {
        loadApi();
        if (YT.playerReady && YT.player) {
            try { fn(YT.player); } catch (e) { /* ignore */ }
            return;
        }
        YT.queue.push(function () {
            if (YT.playerReady && YT.player) {
                try { fn(YT.player); } catch (e) { /* ignore */ }
            }
        });
    }

    function num(v, dflt) {
        var n = parseFloat(v);
        return isFinite(n) ? n : dflt;
    }

    function truthy(v) {
        return v === true || v === 'true' || v === 1 || v === '1';
    }

    // ------------------------------------------------------------------ command implementations
    // Mirrors the ytplayer methods of stock native.js; see HOST-INTERFACES.md.
    var CMD = {

        // Load only. cueVideoById does not autoplay; loadVideoById starts playback immediately.
        // Playback is started by the frontend through playYT().
        loadYT: function (videoId) {
            if (!videoId) { return; }
            withPlayer(function (p) {
                if (typeof p.cueVideoById === 'function') {
                    p.cueVideoById(String(videoId));
                } else {
                    p.loadVideoById(String(videoId));
                }
            });
        },

        // On Android this shows the video surface. Visibility is set only by displayScreenYt.
        openVideoId: function (videoId) {
            if (videoId) { CMD.loadYT(videoId); }
        },

        playYT: function () {
            withPlayer(function (p) { p.playVideo(); });
        },

        pauseYT: function () {
            withPlayer(function (p) { p.pauseVideo(); });
        },

        seekToYT: function (time) {
            var t = num(time, 0);
            withPlayer(function (p) { p.seekTo(t, true); });
        },

        // cmd 1/2/3: rewind; 4/5/6: forward. ffTime is a positive number of seconds.
        seekRel: function (cmd, ffTime) {
            var c = num(cmd, 4);
            var d = Math.abs(num(ffTime, 5));
            var delta = (c >= 1 && c <= 3) ? -d : d;
            withPlayer(function (p) {
                var t = Math.max(0, num(p.getCurrentTime(), 0) + delta);
                p.seekTo(t, true);
                emit('onCurrentTime', [String(t)]);
            });
        },

        currentTime: function () {
            withPlayer(function (p) {
                emit('onCurrentTime', [String(num(p.getCurrentTime(), 0))]);
            });
        },

        setPlaybackRate: function (rate) {
            var r = num(rate, 1);
            withPlayer(function (p) {
                if (typeof p.setPlaybackRate === 'function') { p.setPlaybackRate(r); }
                emit('onPlaybackRate', [String(r)]);
            });
        },

        getPlaybackRate: function () {
            withPlayer(function (p) {
                var r = (typeof p.getPlaybackRate === 'function') ? p.getPlaybackRate() : 1;
                emit('onPlaybackRate', [String(num(r, 1))]);
            });
        },

        getAvailablePlaybackRates: function () {
            withPlayer(function (p) {
                var rates = (typeof p.getAvailablePlaybackRates === 'function')
                    ? p.getAvailablePlaybackRates() : null;
                if (!rates || !rates.length) { rates = [1]; }
                // The frontend JSON.parses this value; it is a JSON array string.
                emit('onAvailablePlaybackRates', [JSON.stringify(rates)]);
            });
        },

        getDuration: function () {
            withPlayer(function (p) {
                emit('onDuration', [String(num(p.getDuration(), 0))]);
            });
        },

        // Only entry point for player visibility; the frontend passes true/false per screen.
        displayScreenYt: function (isShow) {
            showBox(truthy(isShow));
        }
    };

    // ==================================================================================
    // B-2. Tone Exchange ($$btx_*): iframe driven by $native.btx.* (openPage/show/hide/sendMsgToBTX;
    // page events onSendMsgToBTS / onBtxWebViewNotify). Site sends no X-Frame-Options; reply envelope undocumented.
    // ==================================================================================

    var BTX = {
        box: null,
        frame: null,
        url: null,
        rect: { x: 0, y: 0, width: 0, height: 0 },
        visible: false
    };

    function ensureBtxBox() {
        if (BTX.box && document.body.contains(BTX.box)) { return; }

        var box = document.createElement('div');
        box.id = 'kawh-btx-box';
        box.style.cssText = [
            'position:fixed', 'left:0', 'top:0', 'width:100%', 'height:100%',
            'z-index:2147482000', 'background:#fff', 'display:none'
        ].join(';');

        var frame = document.createElement('iframe');
        frame.id = 'kawh-btx-frame';
        frame.style.cssText = 'width:100%;height:100%;border:0;display:block';
        frame.setAttribute('allow', 'autoplay; encrypted-media; fullscreen');
        frame.setAttribute('referrerpolicy', 'no-referrer-when-downgrade');

        frame.addEventListener('load', function () {
            // NotifyResult.PageLoaded = 0x000
            K.emit('btx', 'onBtxWebViewNotify', ['0']);
        });
        frame.addEventListener('error', function () {
            // NotifyResult.ConnectionError = 0x102
            K.emit('btx', 'onBtxWebViewNotify', ['258']);
        });

        box.appendChild(frame);
        document.body.appendChild(box);
        BTX.box = box;
        BTX.frame = frame;
    }

    function btxApplyRect() {
        if (!BTX.box) { return; }
        var r = BTX.rect;
        if (!r.width || !r.height) { return; }
        BTX.box.style.left = (r.x || 0) + 'px';
        BTX.box.style.top = (r.y || 0) + 'px';
        BTX.box.style.width = r.width + 'px';
        BTX.box.style.height = r.height + 'px';
    }

    function btxShow(visible) {
        if (visible) { ensureBtxBox(); }
        BTX.visible = !!visible;
        if (BTX.box) {
            btxApplyRect();
            BTX.box.style.display = visible ? 'block' : 'none';
        }
    }

    // Replies for two environment probes: requestCheckRegionIsChina and requestEvaluateSignInSession.
    // requestMoveToUploadPage / requestGetDownloadedTslFileData are not answered.
    function btxSynthesizeReply(msg) {
        if (typeof msg !== 'string') { return null; }

        var m;
        try { m = JSON.parse(msg); } catch (e) { return null; }
        if (!m || !m.cmd) { return null; }

        if (m.cmd === 'requestCheckRegionIsChina') {
            // BTX_CHINA_REGION_CHECK.NOT_CHINA
            return JSON.stringify({ cmd: 'responseCheckRegionIsChina', arg: { result: 'false' } });
        }
        if (m.cmd === 'requestEvaluateSignInSession') {
            // No BOSS Tone Exchange session in a browser.
            return JSON.stringify({ cmd: 'responseEvaluateSignInSession', arg: { result: 'signedOut' } });
        }
        return null;
    }

    var BTX_CMD = {

        // JSON argument: {"x":0,"y":0,"width":1086,"height":795}
        viewPositionConfig: function (json) {
            try {
                var r = JSON.parse(json);
                BTX.rect = {
                    x: num(r.x, 0), y: num(r.y, 0),
                    width: num(r.width, 0), height: num(r.height, 0)
                };
            } catch (e) { return; }
            btxApplyRect();
        },

        // Load only. Visibility is driven by $native.btx.show() / hide() after page LOADED.
        openPage: function (url) {
            if (!url) { return; }
            ensureBtxBox();
            if (BTX.url !== url) {
                BTX.url = url;
                BTX.frame.src = url;
            }
        },

        show: function () { btxShow(true); },

        hide: function () { btxShow(false); },

        // JSON text; posted to the iframe as-is.
        sendMsgToBTX: function (msg) {
            // The frontend repeats these probes every second until answered; the replies
            // report non-China region and signed out.
            var reply = btxSynthesizeReply(msg);
            if (reply) {
                K.emit('btx', 'onSendMsgToBTS', [encodeURIComponent(reply)]);
            }

            if (!BTX.frame || !BTX.frame.contentWindow) { return; }
            try {
                BTX.frame.contentWindow.postMessage(msg, '*');
            } catch (e) { /* ignore */ }
        }
    };

    // Relays iframe messages as onSendMsgToBTS; encodeURIComponent keeps '|' and newlines
    // out of the event separator.
    function installBtxMessageRelay() {
        window.addEventListener('message', function (e) {
            if (!BTX.frame || e.source !== BTX.frame.contentWindow) { return; }
            var d = e.data;
            if (d === null || d === undefined) { return; }
            if (typeof d !== 'string') {
                try { d = JSON.stringify(d); } catch (x) { return; }
            }
            K.emit('btx', 'onSendMsgToBTS', [encodeURIComponent(d)]);
        });
    }

    // btx_controller.js assigns its own onSendMsgToBTS handler; the encodeURIComponent'd
    // argument is decoded before that handler runs.
    function installBtxEventShim() {
        var ev = window.$native && window.$native.btx && window.$native.btx.event;
        if (!ev) { return false; }

        var raw = ev.onSendMsgToBTS;

        function wrapped(msg) {
            if (typeof msg === 'string') {
                try { msg = decodeURIComponent(msg); } catch (e) { /* deliver as-is */ }
            }
            if (typeof raw === 'function') { return raw.call(this, msg); }
        }

        try {
            Object.defineProperty(ev, 'onSendMsgToBTS', {
                configurable: true,
                enumerable: true,
                get: function () { return wrapped; },
                set: function (fn) { raw = fn; }
            });
        } catch (e) {
            return false;
        }

        return true;
    }

    // ==================================================================================
    // C. Stock extension point adapters
    // ==================================================================================

    // C-1. onScanned shim: midi_device_manager.js expects an array, but events arrive as
    // strings over synchronous XHR; the interceptor JSON.parses before the handler runs.
    function installOnScannedShim() {
        var ev = window.$native && window.$native.app && window.$native.app.event;
        if (!ev) { return false; }

        var raw = ev.onScanned;

        function wrapped(devices) {
            if (typeof devices === 'string') {
                try {
                    devices = JSON.parse(devices);
                } catch (e) {
                    return; // unparsable data is dropped
                }
            }
            if (typeof raw === 'function') {
                return raw.call(this, devices);
            }
        }

        try {
            Object.defineProperty(ev, 'onScanned', {
                configurable: true,
                enumerable: true,
                get: function () { return wrapped; },
                set: function (fn) { raw = fn; }
            });
        } catch (e) {
            return false;
        }

        return true;
    }

    function installCommandHook() {
        var app = window.$native && window.$native.app;
        if (!app || !app.event) { return false; }

        var prev = app.event.command;

        app.event.command = function (param1, param2) {
            if (param1 === '__yt' || param1 === '__btx') {
                try {
                    var msg = JSON.parse(param2);
                    var table = (param1 === '__yt') ? CMD : BTX_CMD;
                    var fn = table[msg.cmd];
                    if (typeof fn === 'function') { fn.apply(null, msg.args || []); }
                } catch (e) { /* malformed packets are dropped */ }
                return;
            }
            if (typeof prev === 'function') {
                return prev.apply(this, arguments);
            }
        };

        return true;
    }

    // ==================================================================================
    // D. Faster event pump
    // $event.start(delay) polls $$app_getevent every 50 ms by default; each poll is one
    // synchronous XHR. The wrapper uses a fixed 10 ms.
    // ==================================================================================
    var EVENT_PUMP_MS = 10;

    function installFasterEventPump() {
        var ev = window.$event;
        if (!ev || typeof ev.start !== 'function') { return false; }

        var orig = ev.start;
        ev.start = function () { return orig.call(ev, EVENT_PUMP_MS); };
        return true;
    }

    function boot() {
        installAjaxProxy();
        installOnScannedShim();
        installBtxEventShim();
        installBtxMessageRelay();
        installCommandHook();
        installFasterEventPump();

        // Load the iframe API at startup; a network failure is reported to the frontend only.
        loadApi();

        // Debug access: __kawh.yt / __kawh.btx.
        window.__kawh.yt = YT;
        window.__kawh.btx = BTX;
    }

    // Runs immediately, not on DOMContentLoaded, before the stock index.js first request.
    if (document.body) {
        boot();
    } else {
        document.addEventListener('DOMContentLoaded', boot);
    }
})();
