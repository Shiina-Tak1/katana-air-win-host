//
//  head.js - injected into <head> of index.html before the stock js/lib/native.js.
//  Provides the host userAgent, window.$$xhrport and the quattro1/quattro2 message handlers.
//  Constraint: no dependency on stock code; the DOM may not be ready.
//
(function () {
    'use strict';

    var HOST = 'KatanaAirWinHost';

    // ---------------------------------------------------------------- 1. UA
    // native.js checks 'roland.quattro'; midi_device_manager.js uses it to detect Android (3 s vs 5 s scan wait).
    // "roland.quattro(Android)" selects Android-specific paths and is not used.
    var UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) ' +
             'Chrome/124.0.0.0 Safari/537.36 roland.quattro(' + HOST + ')';
    try {
        Object.defineProperty(navigator, 'userAgent', {
            get: function () { return UA; },
            configurable: true
        });
    } catch (e) { /* no-op on engines without defineProperty support */ }

    // ---------------------------------------------------------------- 2. $$xhrport
    // native.js concatenates 'http://localhost:' + window.$$xhrport; the value is the port only.
    var port = location.port || (location.protocol === 'https:' ? '443' : '80');
    window.$$xhrport = port;

    // ---------------------------------------------------------------- 3. Host calls
    // URL: /$$<name>?<arg1>,<arg2>, each argument encodeURIComponent'd (native.js `_` layout).
    // Response: raw body with type prefix ('v' / 'b1' / 'd42' / 'f1.5' / 's...'), decoded by native.js decode().
    function hostCall(name, args) {
        var url = 'http://localhost:' + window.$$xhrport + '/' + name;
        if (args && typeof args[0] !== 'undefined') url += '?' + encodeURIComponent(args[0]);
        if (args && typeof args[1] !== 'undefined') url += ',' + encodeURIComponent(args[1]);
        var xhr = new XMLHttpRequest();
        xhr.open('GET', url, false);
        xhr.send(null);
        return xhr.responseText;
    }

    // Pushes "prop|type|arg..." to $$host_event; the host converts '|' to '\f' and takes the payload as one argument.
    function hostEmit(prop, type, args) {
        var parts = [type];
        if (args) {
            for (var i = 0; i < args.length; i++) parts.push(String(args[i]));
        }
        hostCall('$$host_event', [prop + '|' + parts.join('|')]);
    }

    window.__kawh = {
        call: hostCall,
        emit: hostEmit,
        host: HOST
    };

    // ---------------------------------------------------------------- 4. WKWebView shim
    // native.js `_1`: quattro1 writes the result to window[req]; quattro2 is fire-and-forget.
    var handlers = {
        quattro1: {
            postMessage: function (req) {
                var parts = String(req).split('\f');
                var res = hostCall(parts[0], parts.slice(1));
                // req contains 0x0C, a valid JS property name.
                window[req] = res;
            }
        },
        quattro2: {
            postMessage: function (req) {
                var parts = String(req).split('\f');
                hostCall(parts[0], parts.slice(1));
            }
        }
    };

    try {
        if (!window.webkit) window.webkit = {};
        window.webkit.messageHandlers = handlers;
    } catch (e) { /* ignore */ }
})();
