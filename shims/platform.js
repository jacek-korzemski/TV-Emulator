/*
 * One ES5 shim for every TV profile. Chromium 47 parses this file.
 * It does not use let, const, class, arrows, template strings, or destructuring.
 * The host injects window.__TV_EMULATOR_PROFILE__ before this script.
 */
(function (global) {
  if (global.__TV_EMULATOR_BOOTED) {
    return;
  }
  global.__TV_EMULATOR_BOOTED = true;

  var profile = global.__TV_EMULATOR_PROFILE__ || {};
  var keys = profile.keys || {};
  var product = profile.product || {};
  var quirks = profile.quirks || {};
  var shadeRecords = [];
  var shadeSequence = 1;
  var shadeTrackerStarted = false;
  var shadeObject = null;
  var shadeLastPayload = "";
  var shadeLastPost = 0;
  var shadeLastDebug = "";

  function keyCode(name, fallback) {
    return typeof keys[name] === "number" ? keys[name] : fallback;
  }

  var backCode = keyCode("back", 10009);
  var enterCode = keyCode("enter", 13);
  var leftCode = keyCode("left", 37);
  var upCode = keyCode("up", 38);
  var rightCode = keyCode("right", 39);
  var downCode = keyCode("down", 40);
  var redCode = keyCode("red", 403);
  var greenCode = keyCode("green", 404);
  var yellowCode = keyCode("yellow", 405);
  var blueCode = keyCode("blue", 406);

  function post(path, body) {
    try {
      var xhr = new XMLHttpRequest();
      xhr.open("POST", path, true);
      xhr.send(body || "");
    } catch (ignore) {}
  }

  try {
    boot();
  } catch (err) {
    post("/__emulator/log", String(err && err.message ? err.message : err));
  }

  function boot() {
    installCookieFix();
    installRpcCapture();
    applyUserAgent();
    hideDeletedGlobals();
    if (!profile.pointerEnabled) {
      hidePointer();
    }
    installRemote();
    installDiagnostics();
    if (profile.platform === "tizen") {
      installTizen();
    }
    if (profile.platform === "webos") {
      installWebOs();
    }
  }

  /*
   * Apps write cookies with domain="." + location.hostname. On the TV that is a real
   * host name. Here it is 127.0.0.1, and Chromium drops domain cookies on IP and
   * single-label hosts, so session and device cookies never persist.
   */
  function installCookieFix() {
    var desc = null;
    var protos = [global.Document && global.Document.prototype, global.HTMLDocument && global.HTMLDocument.prototype];
    var i;
    for (i = 0; i < protos.length && !desc; i++) {
      if (protos[i]) {
        desc = Object.getOwnPropertyDescriptor(protos[i], "cookie");
      }
    }
    if (!desc || !desc.get || !desc.set) {
      return;
    }

    var reported = {};

    function bareHost(host) {
      return !host || host.indexOf(".") === -1 || host.indexOf(":") !== -1 || /^[0-9.]+$/.test(host);
    }

    function rewrite(text) {
      var parts = text.split(";");
      var host = String(global.location.hostname || "").toLowerCase();
      var insecure = global.location.protocol !== "https:";
      var kept = [parts[0]];
      var dropped = false;
      var j;
      for (j = 1; j < parts.length; j++) {
        var part = parts[j].replace(/^\s+|\s+$/g, "");
        if (!part) {
          continue;
        }
        var eq = part.indexOf("=");
        var name = (eq === -1 ? part : part.substring(0, eq)).replace(/\s+$/, "").toLowerCase();
        var value = eq === -1 ? "" : part.substring(eq + 1).replace(/^\s+|\s+$/g, "");
        if (name === "domain") {
          var domain = value.replace(/^\./, "").toLowerCase();
          if (bareHost(host) || domain === host) {
            dropped = true;
            continue;
          }
        }
        if (insecure && name === "secure") {
          continue;
        }
        if (insecure && name === "samesite" && value.toLowerCase() === "none") {
          kept.push("SameSite=Lax");
          continue;
        }
        kept.push(part);
      }
      if (dropped && profile.debug) {
        var cookieName = parts[0].split("=")[0].replace(/^\s+|\s+$/g, "");
        if (!reported[cookieName]) {
          reported[cookieName] = true;
          post("/__emulator/log", "cookie-domain dropped for " + cookieName);
        }
      }
      return kept.join("; ");
    }

    try {
      Object.defineProperty(document, "cookie", {
        configurable: true,
        get: function () { return desc.get.call(document); },
        set: function (value) { desc.set.call(document, rewrite(String(value))); }
      });
    } catch (ignoreCookie) {}
  }

  function installRpcCapture() {
    if (!profile.debug || global.__TV_EMULATOR_RPC) {
      return;
    }
    global.__TV_EMULATOR_RPC = true;

    function bodyText(body) {
      if (body == null) {
        return "";
      }
      if (typeof body === "string") {
        return body;
      }
      try {
        return String(body);
      } catch (ignore) {
        return "";
      }
    }

    function reportRpc(method, url, headers, requestBody, status, responseBody) {
      var urlText = url == null ? "" : String(url);
      var responseText = responseBody == null ? "" : String(responseBody);
      if (urlText.indexOf("/__emulator/") !== -1) {
        return;
      }
      if (urlText.indexOf("redefine.pl") === -1 && responseText.indexOf("13430") === -1) {
        return;
      }
      var headerText = headers && headers.join ? headers.join("\n") : "";
      var payload = "";
      try {
        payload = JSON.stringify({
          method: method == null ? "" : String(method),
          url: urlText,
          status: status,
          headers: headerText,
          request: bodyText(requestBody),
          response: responseText
        });
      } catch (ignore) {
        return;
      }
      post("/__emulator/log", "rpc " + payload);
    }

    var proto = global.XMLHttpRequest && global.XMLHttpRequest.prototype;
    if (proto && proto.open && proto.send && proto.setRequestHeader) {
      var origOpen = proto.open;
      var origSend = proto.send;
      var origHeader = proto.setRequestHeader;
      proto.open = function (method, url) {
        this.__emuMethod = method;
        this.__emuUrl = url;
        return origOpen.apply(this, arguments);
      };
      proto.setRequestHeader = function (name, value) {
        if (!this.__emuHeaders) {
          this.__emuHeaders = [];
        }
        this.__emuHeaders.push(String(name) + ": " + String(value));
        return origHeader.apply(this, arguments);
      };
      proto.send = function (data) {
        var xhr = this;
        xhr.__emuBody = bodyText(data);
        if (!xhr.__emuHooked) {
          xhr.__emuHooked = true;
          xhr.addEventListener("loadend", function () {
            var text = "";
            try {
              text = xhr.responseText;
            } catch (ignoreText) {
              try {
                text = JSON.stringify(xhr.response);
              } catch (ignoreJson) {
                text = "";
              }
            }
            reportRpc(xhr.__emuMethod, xhr.__emuUrl, xhr.__emuHeaders, xhr.__emuBody, xhr.status, text);
          });
        }
        return origSend.apply(xhr, arguments);
      };
    }

    if (typeof global.fetch === "function") {
      var origFetch = global.fetch;
      global.fetch = function (input, init) {
        var url = "";
        if (typeof input === "string") {
          url = input;
        } else if (input && input.url) {
          url = input.url;
        }
        var method = init && init.method ? init.method : "GET";
        var body = init && init.body != null ? bodyText(init.body) : "";
        return origFetch.apply(global, arguments).then(function (response) {
          try {
            response.clone().text().then(function (text) {
              reportRpc(method, url, [], body, response.status, text);
            }, function () {});
          } catch (ignoreClone) {}
          return response;
        }, function (err) {
          reportRpc(method, url, [], body, 0, String(err && err.message ? err.message : err));
          throw err;
        });
      };
    }
  }

  function applyUserAgent() {
    if (!profile.userAgent) {
      return;
    }
    try {
      Object.defineProperty(navigator, "userAgent", {
        configurable: true,
        get: function () { return profile.userAgent; }
      });
    } catch (ignoreUa) {}
  }

  function installDiagnostics() {
    if (!profile.debug) {
      return;
    }
    global.onerror = function (message, source, line) {
      post("/__emulator/log", "js-error " + message + " " + source + ":" + line);
    };
    global.addEventListener("error", function (e) {
      var target = e.target || e.srcElement;
      if (!target || target === global) {
        return;
      }
      var url = target.src || target.href || "";
      post("/__emulator/log", "resource-error " + (target.tagName || "") + " " + url);
    }, true);
    try {
      global.addEventListener("unhandledrejection", function (e) {
        var reason = e && e.reason ? (e.reason.message || String(e.reason)) : "";
        post("/__emulator/log", "unhandledrejection " + reason);
      });
    } catch (ignoreRejection) {}
    wrapConsole("error");
    wrapConsole("warn");
  }

  function wrapConsole(level) {
    if (!global.console || !global.console[level]) {
      return;
    }
    var original = global.console[level];
    global.console[level] = function () {
      var text = "";
      var i;
      for (i = 0; i < arguments.length; i++) {
        if (i) {
          text += " ";
        }
        text += arguments[i];
      }
      post("/__emulator/log", "console." + level + " " + text);
      try {
        return original.apply(global.console, arguments);
      } catch (ignoreApply) {
        return original(text);
      }
    };
  }

  function hideDeletedGlobals() {
    var names = quirks.deleteGlobals || [];
    var i;
    for (i = 0; i < names.length; i++) {
      try {
        delete global[names[i]];
      } catch (ignoreDelete) {}
      try {
        Object.defineProperty(global, names[i], {
          value: undefined,
          configurable: true,
          writable: true
        });
      } catch (ignoreDefine) {}
    }
  }

  function hidePointer() {
    var node = document.createElement("style");
    node.setAttribute("data-tv-emulator", "pointer");
    node.appendChild(document.createTextNode("html,body,*{cursor:none !important;}"));
    var parent = document.head || document.documentElement;
    if (parent) {
      parent.appendChild(node);
    }
  }

  function dispatchKey(code, type) {
    var evt = document.createEvent("Event");
    evt.initEvent(type, true, true);
    evt.__tvEmulator = true;
    Object.defineProperty(evt, "keyCode", { get: function () { return code; } });
    Object.defineProperty(evt, "which", { get: function () { return code; } });
    var target = document.body || document.documentElement || document;
    target.dispatchEvent(evt);
  }

  function rewriteKey(e) {
    if (!e || e.__tvEmulator) {
      return;
    }
    var mapped = null;
    if (e.keyCode === 27) {
      mapped = backCode;
    } else if (e.keyCode === 112) {
      mapped = redCode;
    } else if (e.keyCode === 113) {
      mapped = greenCode;
    } else if (e.keyCode === 114) {
      mapped = yellowCode;
    } else if (e.keyCode === 115) {
      mapped = blueCode;
    }
    if (mapped === null || mapped === e.keyCode) {
      return;
    }
    e.preventDefault();
    e.stopImmediatePropagation();
    dispatchKey(mapped, e.type);
  }

  function installRemote() {
    global.addEventListener("keydown", rewriteKey, true);
    global.addEventListener("keyup", rewriteKey, true);
    if (typeof navigator.getGamepads === "function") {
      global.setInterval(pollGamepad, 80);
    }
  }

  var padWas = [];

  function padDown(pad, index) {
    if (!pad.buttons || index >= pad.buttons.length || !pad.buttons[index]) {
      return false;
    }
    var button = pad.buttons[index];
    return button.pressed === true || button.value > 0.5;
  }

  function pollGamepad() {
    var pads;
    var pad = null;
    var i;
    try {
      pads = navigator.getGamepads();
    } catch (ignore) {
      return;
    }
    if (!pads) {
      return;
    }
    for (i = 0; i < pads.length; i++) {
      if (pads[i]) {
        pad = pads[i];
        break;
      }
    }
    if (!pad) {
      return;
    }
    edge(0, padDown(pad, 0), enterCode);
    edge(1, padDown(pad, 1), backCode);
    edge(2, padDown(pad, 2), greenCode);
    edge(3, padDown(pad, 3), yellowCode);
    edge(4, padDown(pad, 4), redCode);
    edge(5, padDown(pad, 5), blueCode);
    edge(12, padDown(pad, 12), upCode);
    edge(13, padDown(pad, 13), downCode);
    edge(14, padDown(pad, 14), leftCode);
    edge(15, padDown(pad, 15), rightCode);
  }

  function edge(index, pressed, code) {
    var was = padWas[index] === true;
    if (pressed && !was) {
      dispatchKey(code, "keydown");
    }
    if (!pressed && was) {
      dispatchKey(code, "keyup");
    }
    padWas[index] = pressed;
  }

  function WebAPIException(code, message, name) {
    this.code = code;
    this.message = message;
    this.name = name || "WebAPIException";
  }
  WebAPIException.prototype = Object.create(Error.prototype);

  function appInfo() {
    return profile.appInfo || { id: "emulator.app", title: "App", version: "1.0.0" };
  }

  function requestExit() {
    try {
      var xhr = new XMLHttpRequest();
      xhr.open("POST", "/__emulator/exit", false);
      xhr.send("");
    } catch (ignore) {}
  }

  function installTizen() {
    var registered = {};
    var supported = [
      { name: "ColorF0Red", code: redCode },
      { name: "ColorF1Green", code: greenCode },
      { name: "ColorF2Yellow", code: yellowCode },
      { name: "ColorF3Blue", code: blueCode },
      { name: "MediaPlay", code: 415 },
      { name: "MediaPause", code: 19 },
      { name: "MediaStop", code: 413 },
      { name: "MediaFastForward", code: 417 },
      { name: "MediaRewind", code: 412 },
      { name: "MediaPlayPause", code: 10252 },
      { name: "ChannelUp", code: 427 },
      { name: "ChannelDown", code: 428 },
      { name: "Exit", code: 10182 },
      { name: "Info", code: 457 },
      { name: "Return", code: backCode }
    ];

    function findKey(name) {
      var i;
      for (i = 0; i < supported.length; i++) {
        if (supported[i].name === name) {
          return supported[i];
        }
      }
      return null;
    }

    function ApplicationControlData(key, value) {
      this.key = key;
      this.value = value || [];
    }

    function ApplicationControl(operation, uri, mime, category, data, launchMode) {
      this.operation = operation || null;
      this.uri = uri || null;
      this.mime = mime || null;
      this.category = category || null;
      this.data = data || [];
      this.launchMode = launchMode || "SINGLE";
    }

    function packageIdOf(id) {
      var text = id || "emulator.app";
      var dot = text.indexOf(".");
      if (dot <= 0) {
        return text;
      }
      return text.substring(0, dot);
    }

    function trace(message) {
      if (profile.debug) {
        post("/__emulator/log", message);
      }
    }

    var info = appInfo();
    var appId = info.id || "emulator.app";
    var currentApp = {
      exit: requestExit,
      hide: requestExit,
      contextId: "emulator-context",
      appInfo: {
        id: appId,
        name: info.title || info.name || "App",
        version: info.version || "1.0.0",
        show: function () {},
        packageId: packageIdOf(appId),
        iconPath: "",
        categories: [],
        installDate: new Date(0),
        size: 0
      },
      getRequestedAppControl: function () {
        return {
          appControl: new ApplicationControl("http://tizen.org/appcontrol/operation/default", null, null, null, []),
          callerAppId: null,
          replyResult: function () {},
          replyFailure: function () {}
        };
      },
      addEventListener: function () {},
      removeEventListener: function () {}
    };

    global.tizen = {
      ApplicationControlData: ApplicationControlData,
      ApplicationControl: ApplicationControl,
      application: {
        getCurrentApplication: function () { return currentApp; },
        getAppInfo: function (id) {
          if (!id || id === appId) {
            return currentApp.appInfo;
          }
          return null;
        },
        getAppContext: function (contextId) {
          if (contextId && contextId !== currentApp.contextId) {
            return null;
          }
          return { id: currentApp.contextId, appId: appId };
        },
        getAppsInfo: function (onSuccess) {
          global.setTimeout(function () {
            if (onSuccess) {
              onSuccess([currentApp.appInfo]);
            }
          }, 0);
        },
        launch: function (id, onSuccess) {
          trace("launch " + (id || ""));
          global.setTimeout(function () {
            if (onSuccess) {
              onSuccess();
            }
          }, 0);
        },
        launchAppControl: function (appControl, id, onSuccess) {
          var operation = appControl && appControl.operation ? appControl.operation : "";
          trace("launchAppControl " + (id || "") + " " + operation);
          global.setTimeout(function () {
            if (onSuccess) {
              onSuccess();
            }
          }, 0);
        }
      },
      systeminfo: {
        getCapability: function (feature) {
          if (feature === "http://tizen.org/feature/platform.version") {
            return profile.platformVersion || "";
          }
          if (feature === "http://tizen.org/feature/screen.width") {
            return 1920;
          }
          if (feature === "http://tizen.org/feature/screen.height") {
            return 1080;
          }
          return null;
        },
        getPropertyValue: function (property, onSuccess, onError) {
          var value = null;
          if (property === "LOCALE") {
            value = { language: "pl", country: "PL" };
          } else if (property === "DISPLAY") {
            value = {
              resolutionWidth: 1920,
              resolutionHeight: 1080,
              dotsPerInchWidth: 72,
              dotsPerInchHeight: 72,
              physicalWidth: 0,
              physicalHeight: 0,
              brightness: 1
            };
          } else if (property === "BUILD") {
            value = {
              model: product.model || "",
              manufacturer: product.brand || "Samsung",
              buildVersion: product.firmware || ""
            };
          } else if (property === "WIFI_NETWORK") {
            value = { status: "ON", ssid: "emulator", ipAddress: "192.168.0.20", signalStrength: 4 };
          }
          if (value === null) {
            if (onError) {
              onError(new WebAPIException(0, "Not supported", "NotSupportedError"));
            }
            return;
          }
          if (onSuccess) {
            onSuccess(value);
          }
        }
      },
      tvinputdevice: {
        registerKey: function (name) {
          if (!findKey(name)) {
            throw new WebAPIException(11, "Invalid key name", "InvalidValuesError");
          }
          registered[name] = true;
        },
        unregisterKey: function (name) {
          delete registered[name];
        },
        getSupportedKeys: function () {
          var copy = [];
          var i;
          for (i = 0; i < supported.length; i++) {
            copy.push({ name: supported[i].name, code: supported[i].code });
          }
          return copy;
        },
        getKey: function (name) {
          var found = findKey(name);
          if (!found) {
            return null;
          }
          return { name: found.name, code: found.code };
        },
        isRegistered: function (name) {
          return registered[name] === true;
        }
      }
    };

    var listenerSeq = 1;
    var listeners = {};
    global.webapis = {
      productinfo: {
        getVersion: function () { return profile.platformVersion || "2.3"; },
        getFirmware: function () { return product.firmware || ""; },
        getDuid: function () { return product.duid || "EMULATOR"; },
        getModelCode: function () { return product.modelCode || ""; },
        getModel: function () { return product.model || ""; },
        getRealModel: function () { return product.model || ""; },
        getLicensedBrand: function () { return product.brand || "Samsung"; },
        getLocalSet: function () { return "PL"; },
        isUdPanelSupported: function () { return product.uhd === true; },
        is8KPanelSupported: function () { return false; },
        isOledPanelSupported: function () { return false; },
        isUHDAModel: function () { return product.uhd === true; },
        isWallModel: function () { return false; },
        getSystemConfig: function () { return null; },
        getSmartTVServerType: function () { return "DEVELOPMENT"; }
      },
      network: {
        NetworkActiveConnectionType: {
          DISCONNECTED: 0,
          WIFI: 1,
          CELLULAR: 2,
          ETHERNET: 3
        },
        getVersion: function () { return "2.3"; },
        isConnectedToGateway: function () { return true; },
        getIp: function () { return "192.168.0.20"; },
        getMac: function () { return "02:00:00:00:00:47"; },
        getGateway: function () { return "192.168.0.1"; },
        getDns: function () { return "1.1.1.1"; },
        getActiveConnectionType: function () { return 3; },
        addNetworkStateChangeListener: function (listener) {
          var id = listenerSeq;
          listenerSeq += 1;
          listeners[id] = listener;
          return id;
        },
        removeNetworkStateChangeListener: function (id) {
          delete listeners[id];
        }
      },
      avplay: createAvplay()
    };
  }

  /*
   * webapis.avplay is the native Tizen player. The pinned Chromium has no H.264, AAC or PlayReady,
   * so the calls go to the emulator, which plays the stream on a video plane (WebView2) right below
   * this window. The plane shows through wherever the page paints the key color (VideoPlane.KeyColor
   * on the host), the same way the TV video layer shows through under <object type="application/avplayer">.
   */
  function createAvplay() {
    var state = "NONE";
    var gen = 0;
    var head = -1;
    var polling = false;
    var listener = {};
    var durationMs = 0;
    var timeMs = 0;
    var live = false;
    var liveStartMs = 0;
    var liveEndMs = 0;
    var tracks = [];
    var current = [];
    var properties = {};
    var pendingPrepare = null;
    var jumps = {};
    var jumpSeq = 1;

    installVideoSurface();
    post("/__emulator/avplay", JSON.stringify({ cmd: "reset", gen: 0 }));

    function trace(message) {
      if (profile.debug) {
        post("/__emulator/log", "avplay-shim " + message);
      }
    }

    function fail(name, message) {
      trace(name + " " + message);
      throw new WebAPIException(name === "TypeMismatchError" ? 17 : 11, message, name);
    }

    function requireState(method, allowed) {
      if (allowed.indexOf(state) === -1) {
        fail("InvalidStateError", method + " is not allowed in state " + state);
      }
    }

    function send(command) {
      command.gen = gen;
      var reply = null;
      try {
        var xhr = new XMLHttpRequest();
        xhr.open("POST", "/__emulator/avplay", false);
        xhr.setRequestHeader("Content-Type", "application/json");
        xhr.send(JSON.stringify(command));
        reply = JSON.parse(xhr.responseText);
      } catch (ignore) {
        reply = null;
      }
      if (!reply || reply.ok !== true) {
        fail("UnknownError", "video plane did not accept " + command.cmd);
      }
    }

    function call(name) {
      var fn = listener ? listener[name] : null;
      if (typeof fn !== "function") {
        return;
      }
      try {
        fn.apply(listener, Array.prototype.slice.call(arguments, 1));
      } catch (err) {
        trace("listener " + name + " threw " + (err && err.message ? err.message : err));
      }
    }

    function copyTracks(list) {
      var copy = [];
      var i;
      for (i = 0; i < list.length; i++) {
        copy.push({ index: list[i].index, type: list[i].type, extra_info: list[i].extra_info });
      }
      return copy;
    }

    function deliver(evt) {
      if (evt.gen != null && evt.gen !== gen) {
        return;
      }
      switch (evt.type) {
        case "prepared":
          durationMs = evt.durationMs || 0;
          live = evt.live === true;
          liveStartMs = evt.liveStartMs || 0;
          liveEndMs = evt.liveEndMs || 0;
          tracks = evt.tracks || [];
          current = evt.current || [];
          if (state === "IDLE") {
            state = "READY";
          }
          if (pendingPrepare) {
            var done = pendingPrepare;
            pendingPrepare = null;
            if (typeof done.ok === "function") {
              done.ok();
            }
          }
          break;
        case "prepareError":
          if (pendingPrepare) {
            var failed = pendingPrepare;
            pendingPrepare = null;
            if (typeof failed.err === "function") {
              failed.err(new WebAPIException(0, evt.message || evt.code, evt.code || "PLAYER_ERROR_GENEREIC"));
              break;
            }
          }
          call("onerror", evt.code || "PLAYER_ERROR_GENEREIC");
          break;
        case "time":
          timeMs = evt.ms || 0;
          if (live && timeMs > liveEndMs) {
            liveEndMs = timeMs;
          }
          if (state === "PLAYING") {
            call("oncurrentplaytime", timeMs);
          }
          break;
        case "buffering":
          if (evt.state === "start") {
            call("onbufferingstart");
          } else {
            call("onbufferingprogress", 100);
            call("onbufferingcomplete");
          }
          break;
        case "completed":
          call("onstreamcompleted");
          break;
        case "error":
          call("onerror", evt.code || "PLAYER_ERROR_GENEREIC");
          break;
        case "drm":
          call("ondrmevent", evt.drmType, evt.data);
          break;
        case "event":
          call("onevent", evt.name, evt.data);
          break;
        case "subtitle":
          call("onsubtitlechange", evt.durationMs, evt.text, 0, "");
          break;
        case "tracks":
          tracks = evt.tracks || tracks;
          current = evt.current || current;
          break;
        case "jumped":
          var pending = jumps[evt.callback];
          delete jumps[evt.callback];
          if (pending && evt.ok && typeof pending.ok === "function") {
            pending.ok();
          } else if (pending && !evt.ok && typeof pending.err === "function") {
            pending.err(new WebAPIException(11, "seek failed", "InvalidStateError"));
          }
          break;
      }
    }

    function poll() {
      var xhr = new XMLHttpRequest();
      xhr.open("GET", "/__emulator/avplay/events?since=" + head, true);
      xhr.onload = function () {
        var data = null;
        try {
          data = JSON.parse(xhr.responseText);
        } catch (ignore) {
          data = null;
        }
        if (data) {
          head = data.head;
          var i;
          for (i = 0; i < data.events.length; i++) {
            try {
              deliver(data.events[i]);
            } catch (err) {
              trace("deliver " + data.events[i].type + " threw " + (err && err.message ? err.message : err));
            }
          }
        }
        global.setTimeout(poll, 0);
      };
      xhr.onerror = function () {
        global.setTimeout(poll, 1000);
      };
      xhr.send();
    }

    function startPolling() {
      if (polling) {
        return;
      }
      polling = true;
      try {
        var xhr = new XMLHttpRequest();
        xhr.open("GET", "/__emulator/avplay/events?since=-1", false);
        xhr.send();
        head = JSON.parse(xhr.responseText).head;
      } catch (ignore) {
        head = 0;
      }
      poll();
    }

    function jump(method, command, ok, err) {
      requireState(method, ["READY", "PLAYING", "PAUSED"]);
      var id = jumpSeq;
      jumpSeq += 1;
      jumps[id] = { ok: ok, err: err };
      command.callback = id;
      send(command);
    }

    return {
      open: function (url) {
        requireState("open", ["NONE"]);
        if (typeof url !== "string" || !url) {
          fail("TypeMismatchError", "open needs a url");
        }
        startPolling();
        gen += 1;
        ensurePlayerObject(gen);
        durationMs = 0;
        timeMs = 0;
        live = false;
        liveStartMs = 0;
        liveEndMs = 0;
        tracks = [];
        current = [];
        properties = {};
        pendingPrepare = null;
        jumps = {};
        send({ cmd: "open", url: url });
        state = "IDLE";
      },
      close: function () {
        if (state === "NONE") {
          return;
        }
        pendingPrepare = null;
        state = "NONE";
        send({ cmd: "close" });
      },
      prepare: function () {
        requireState("prepare", ["IDLE"]);
        send({ cmd: "prepare" });
      },
      prepareAsync: function (onSuccess, onError) {
        if (state === "READY") {
          if (typeof onSuccess === "function") {
            global.setTimeout(onSuccess, 0);
          }
          return;
        }
        requireState("prepareAsync", ["IDLE"]);
        pendingPrepare = { ok: onSuccess, err: onError };
        send({ cmd: "prepare" });
      },
      play: function () {
        if (state === "PLAYING") {
          return;
        }
        requireState("play", ["READY", "PAUSED"]);
        send({ cmd: "play" });
        state = "PLAYING";
      },
      pause: function () {
        if (state === "PAUSED") {
          return;
        }
        requireState("pause", ["PLAYING"]);
        send({ cmd: "pause" });
        state = "PAUSED";
      },
      stop: function () {
        if (state === "NONE") {
          return;
        }
        pendingPrepare = null;
        send({ cmd: "stop" });
        state = "IDLE";
        timeMs = 0;
      },
      seekTo: function (ms, onSuccess, onError) {
        jump("seekTo", { cmd: "seek", ms: Number(ms) || 0 }, onSuccess, onError);
        timeMs = Number(ms) || 0;
      },
      jumpForward: function (ms, onSuccess, onError) {
        jump("jumpForward", { cmd: "jump", ms: Number(ms) || 0 }, onSuccess, onError);
        timeMs += Number(ms) || 0;
      },
      jumpBackward: function (ms, onSuccess, onError) {
        jump("jumpBackward", { cmd: "jump", ms: -(Number(ms) || 0) }, onSuccess, onError);
        timeMs = Math.max(0, timeMs - (Number(ms) || 0));
      },
      getState: function () {
        return state;
      },
      getDuration: function () {
        return durationMs;
      },
      getCurrentTime: function () {
        return timeMs;
      },
      setListener: function (value) {
        listener = value || {};
      },
      unsetListener: function () {
        listener = {};
      },
      setDisplayRect: function (x, y, width, height) {
        requireState("setDisplayRect", ["IDLE", "READY", "PLAYING", "PAUSED"]);
        send({ cmd: "display", x: Number(x) || 0, y: Number(y) || 0, w: Number(width) || 0, h: Number(height) || 0 });
      },
      setDisplayMethod: function (method) {
        requireState("setDisplayMethod", ["IDLE", "READY", "PLAYING", "PAUSED"]);
        send({ cmd: "displayMethod", method: String(method) });
      },
      setStreamingProperty: function (name, value) {
        requireState("setStreamingProperty", ["IDLE"]);
        properties[name] = value;
        send({ cmd: "property", name: String(name), value: value == null ? "" : String(value) });
      },
      getStreamingProperty: function (name) {
        requireState("getStreamingProperty", ["IDLE", "READY", "PLAYING", "PAUSED"]);
        if (name === "IS_LIVE") {
          return live ? "1" : "0";
        }
        if (name === "GET_LIVE_DURATION") {
          return liveStartMs + "|" + liveEndMs;
        }
        return properties[name] == null ? "" : String(properties[name]);
      },
      setDrm: function (drmType, operation, data) {
        requireState("setDrm", ["IDLE", "READY", "PLAYING", "PAUSED"]);
        send({ cmd: "drm", drmType: String(drmType), operation: String(operation), data: data == null ? "" : String(data) });
        return "";
      },
      getTotalTrackInfo: function () {
        requireState("getTotalTrackInfo", ["READY", "PLAYING", "PAUSED"]);
        return copyTracks(tracks);
      },
      getCurrentStreamInfo: function () {
        requireState("getCurrentStreamInfo", ["READY", "PLAYING", "PAUSED"]);
        return copyTracks(current);
      },
      setSelectTrack: function (trackType, index) {
        requireState("setSelectTrack", ["READY", "PLAYING", "PAUSED"]);
        send({ cmd: "track", trackType: String(trackType), index: Number(index) });
      },
      setExternalSubtitlePath: function (path) {
        properties.EXTERNAL_SUBTITLE_PATH = path;
        trace("setExternalSubtitlePath is not played on the video plane: " + path);
        return true;
      },
      setSilentSubtitle: function () {},
      setSubtitlePosition: function () {},
      setSpeed: function (rate) {
        requireState("setSpeed", ["READY", "PLAYING", "PAUSED"]);
        send({ cmd: "speed", rate: Number(rate) || 1 });
      },
      setTimeoutForBuffering: function () {},
      setBufferingParam: function () {},
      suspend: function () {
        if (state === "NONE") {
          return;
        }
        send({ cmd: "suspend" });
      },
      restore: function () {
        if (state === "NONE") {
          return;
        }
        send({ cmd: "restore" });
      },
      getVersion: function () {
        return profile.platformVersion || "";
      }
    };
  }

  function installVideoSurface() {
    if (profile.videoComposition === "alpha") {
      installAlphaSurface();
      return;
    }
    installKeyColor();
  }

  // Real per-pixel alpha: the TV page paints over the video the way a TV graphic
  // plane does. Do not paint a color key, and do not restyle the player UI.
  function installAlphaSurface() {
    var node = document.createElement("style");
    node.setAttribute("data-tv-emulator", "avplay");
    node.appendChild(document.createTextNode(
      "object[type=\"application/avplayer\"]{display:block;background:transparent !important;}"
    ));
    var parent = document.head || document.documentElement;
    if (parent) {
      parent.appendChild(node);
    }
    installPlayerFallback();
  }

  function installKeyColor() {
    var keyColor = "#010001";
    var node = document.createElement("style");
    node.setAttribute("data-tv-emulator", "avplay");
    node.appendChild(document.createTextNode(
      "object[type=\"application/avplayer\"]{display:block;background-color:" + keyColor + " !important;}"
    ));
    var parent = document.head || document.documentElement;
    if (parent) {
      parent.appendChild(node);
    }
    installPlayerFallback();
    installShadeTracker();
    // The player hides its <video> and may paint a poster over the AVPlay object.
    // Stretch the object across its parent so the color key shows the video plane.
    // Only set position:relative when the parent is actually static. fixed, absolute
    // and relative come from the app stylesheet; an inline relative drops that overlay
    // into normal flow, a viewport width below the picture.
    global.setInterval(openVideoHole, 400);
  }

  /*
   * A color key is binary. A large translucent black plate over the AVPlay object
   * changes #010001 into an ordinary dark pixel and closes the hole. Move only those
   * large background plates to the video plane. Text, icons, focus rings and their
   * native opacity animation remain in this Chromium window.
   *
   * Records survive opacity:0 and display:none. That is important: removing a shade
   * at the first fading frame made the old implementation reveal the full movie at
   * once. Here the plane receives every intermediate computed opacity from rAF.
   */
  function installShadeTracker() {
    if (shadeTrackerStarted) {
      return;
    }
    shadeTrackerStarted = true;
    var style = document.createElement("style");
    style.setAttribute("data-tv-emulator", "shade");
    style.appendChild(document.createTextNode(
      "[data-tv-shade]{background-color:transparent !important;background-image:none !important;transition-property:opacity,transform !important;}"
    ));
    var parent = document.head || document.documentElement;
    if (parent) {
      parent.appendChild(style);
    }
    if (profile.debug) {
      global.__TV_EMULATOR_SHADE_TICK = function () {
        discoverShades();
        shadeFrameNow();
        return global.__TV_EMULATOR_SHADES || null;
      };
    }
    global.setInterval(discoverShades, 250);
    shadeFrame();
  }

  function findPlayerObject() {
    var holes = document.getElementsByTagName("div");
    var hole;
    for (hole = 0; hole < holes.length; hole++) {
      if (holes[hole].getAttribute("data-tv-hole") === "widevine") {
        return holes[hole];
      }
    }
    var objects = document.getElementsByTagName("object");
    var i;
    for (i = 0; i < objects.length; i++) {
      if (String(objects[i].getAttribute("type") || "").toLowerCase() === "application/avplayer") {
        return objects[i];
      }
    }
    return null;
  }

  // Some players keep an "AVPlay object already created" flag in module scope.
  // A hash-route unmount removes the first player container and its AVPlay object,
  // but that flag stays true, so the next instance hides its <video> without
  // creating a new color-key surface. Recreate the object beside that hidden video.
  function ensurePlayerObject(generation) {
    var videos = document.getElementsByTagName("video");
    var video = null;
    var host = null;
    var best = -1;
    var i;
    for (i = 0; i < videos.length; i++) {
      var candidate = videos[i];
      var parent = candidate.parentNode;
      if (!parent || !parent.appendChild || !parent.getBoundingClientRect) {
        continue;
      }
      var rect = parent.getBoundingClientRect();
      var score = Math.max(0, rect.width) * Math.max(0, rect.height);
      if (candidate.style && candidate.style.display === "none") {
        score += 1000000000;
      }
      score += i;
      if (score > best) {
        best = score;
        video = candidate;
        host = parent;
      }
    }
    if (!host) {
      host = document.getElementById("player-container");
    }
    if (host) {
      var hostObjects = host.getElementsByTagName("object");
      for (i = 0; i < hostObjects.length; i++) {
        if (String(hostObjects[i].getAttribute("type") || "").toLowerCase() === "application/avplayer") {
          return hostObjects[i];
        }
      }
    }
    if (!host) {
      var existing = findPlayerObject();
      if (existing) {
        return existing;
      }
      if (profile.debug) {
        post("/__emulator/log", "avplay-hole recreate failed no-player-container");
      }
      return null;
    }

    var object = document.createElement("object");
    object.setAttribute("type", "application/avplayer");
    object.setAttribute("id", "");
    object.setAttribute("url", "");
    object.setAttribute("style", "width:100%;height:100%");
    object.setAttribute("data-tv-emulator", "avplay-recreated");
    host.appendChild(object);
    if (video && video.style) {
      video.style.display = "none";
    }
    openVideoHole();
    if (profile.debug) {
      post("/__emulator/log", "avplay-hole recreated gen=" + generation + " videos=" + videos.length);
    }
    return object;
  }

  function shadeRound(value, digits) {
    var power = Math.pow(10, digits);
    return Math.round(value * power) / power;
  }

  function shadeColor(text) {
    var match = /rgba?\(\s*([0-9.]+)\s*,\s*([0-9.]+)\s*,\s*([0-9.]+)(?:\s*,\s*([0-9.]+))?\s*\)/i.exec(String(text || ""));
    if (!match) {
      return null;
    }
    return {
      r: Number(match[1]) || 0,
      g: Number(match[2]) || 0,
      b: Number(match[3]) || 0,
      a: match[4] == null ? 1 : Number(match[4]) || 0
    };
  }

  function darkGradient(image) {
    return image && image !== "none"
      && /gradient/i.test(image)
      && /rgba?\(\s*(?:0|[1-3]?[0-9])\s*,\s*(?:0|[1-3]?[0-9])\s*,\s*(?:0|[1-3]?[0-9])/i.test(image);
  }

  function readShadePaint(element) {
    var tagged = element.hasAttribute("data-tv-shade");
    var id = tagged ? element.getAttribute("data-tv-shade") : "";
    if (tagged) {
      element.removeAttribute("data-tv-shade");
    }
    var style = global.getComputedStyle(element);
    var color = style.backgroundColor || "rgba(0, 0, 0, 0)";
    var image = style.backgroundImage || "none";
    var radius = style.borderRadius || "0px";
    if (tagged) {
      element.setAttribute("data-tv-shade", id);
    }
    var parsed = shadeColor(color);
    return {
      color: color,
      image: image,
      radius: radius,
      dark: !!((parsed && parsed.a > 0 && parsed.r <= 48 && parsed.g <= 48 && parsed.b <= 48) || darkGradient(image))
    };
  }

  function shadeSignature(element) {
    return String(element.className || "") + "|" + String(element.getAttribute("style") || "");
  }

  function registeredShade(element) {
    var i;
    for (i = 0; i < shadeRecords.length; i++) {
      if (shadeRecords[i].element === element) {
        return shadeRecords[i];
      }
    }
    return null;
  }

  function discoverShades() {
    var player = findPlayerObject();
    if (!player || !global.getComputedStyle) {
      return;
    }
    shadeObject = player;
    var videoRect = player.getBoundingClientRect();
    if (videoRect.width < 2 || videoRect.height < 2) {
      return;
    }

    var minimumArea = videoRect.width * videoRect.height * 0.1;
    var all = document.getElementsByTagName("*");
    var i;
    for (i = 0; i < all.length; i++) {
      var element = all[i];
      if (element === player || element === document.documentElement || element === document.body
          || element.hasAttribute("data-tv-emulator")
          || (element.contains && element.contains(player))
          || (player.contains && player.contains(element))) {
        continue;
      }
      var rect = element.getBoundingClientRect();
      var left = Math.max(rect.left, videoRect.left);
      var top = Math.max(rect.top, videoRect.top);
      var right = Math.min(rect.right, videoRect.right);
      var bottom = Math.min(rect.bottom, videoRect.bottom);
      var width = Math.max(0, right - left);
      var height = Math.max(0, bottom - top);
      if (width * height < minimumArea
          || (width < videoRect.width * 0.4 && height < videoRect.height * 0.4)) {
        continue;
      }

      var record = registeredShade(element);
      var signature = shadeSignature(element);
      if (record && record.signature === signature) {
        continue;
      }
      var paint = readShadePaint(element);
      if (record) {
        record.color = paint.color;
        record.image = paint.image;
        record.radius = paint.radius;
        record.enabled = paint.dark;
        record.signature = signature;
        continue;
      }
      if (!paint.dark) {
        continue;
      }

      var shadeId = "shade-" + shadeSequence++;
      element.setAttribute("data-tv-shade", shadeId);
      shadeRecords.push({
        id: shadeId,
        element: element,
        color: paint.color,
        image: paint.image,
        radius: paint.radius,
        enabled: true,
        signature: shadeSignature(element),
        rect: { x: left - videoRect.left, y: top - videoRect.top, w: width, h: height }
      });
      if (profile.debug) {
        post("/__emulator/log", "avplay-shade-track add " + shadeId + " " + Math.round(width) + "x" + Math.round(height));
      }
    }
  }

  function effectiveShadeOpacity(element) {
    var opacity = 1;
    var current = element;
    while (current && current.nodeType === 1) {
      var style = global.getComputedStyle(current);
      if (style.display === "none" || style.visibility === "hidden") {
        return 0;
      }
      var value = parseFloat(style.opacity);
      if (!isNaN(value)) {
        opacity *= value;
      }
      current = current.parentNode;
    }
    return Math.max(0, Math.min(1, opacity));
  }

  function scheduleShadeFrame() {
    var fired = false;
    var timeout = global.setTimeout(function () {
      if (!fired) {
        fired = true;
        shadeFrame();
      }
    }, 50);
    if (global.requestAnimationFrame) {
      global.requestAnimationFrame(function () {
        if (!fired) {
          fired = true;
          global.clearTimeout(timeout);
          shadeFrame();
        }
      });
    }
  }

  function shadeFrameNow() {
    var player = shadeObject && document.documentElement.contains(shadeObject) ? shadeObject : findPlayerObject();
    if (player) {
      shadeObject = player;
      var videoRect = player.getBoundingClientRect();
      if (videoRect.width >= 2 && videoRect.height >= 2) {
        var alive = [];
        var items = [];
        var debug = [];
        var i;
        for (i = 0; i < shadeRecords.length; i++) {
          var record = shadeRecords[i];
          if (!document.documentElement.contains(record.element)) {
            continue;
          }
          alive.push(record);
          var rect = record.element.getBoundingClientRect();
          var left = Math.max(rect.left, videoRect.left);
          var top = Math.max(rect.top, videoRect.top);
          var right = Math.min(rect.right, videoRect.right);
          var bottom = Math.min(rect.bottom, videoRect.bottom);
          if (right > left && bottom > top) {
            record.rect = {
              x: left - videoRect.left,
              y: top - videoRect.top,
              w: right - left,
              h: bottom - top
            };
          }
          var opacity = record.enabled ? effectiveShadeOpacity(record.element) : 0;
          items.push({
            id: record.id,
            x: shadeRound(record.rect.x, 2),
            y: shadeRound(record.rect.y, 2),
            w: shadeRound(record.rect.w, 2),
            h: shadeRound(record.rect.h, 2),
            color: record.color,
            image: record.image,
            radius: record.radius,
            opacity: shadeRound(opacity, 3)
          });
          debug.push(record.id + ":" + shadeRound(opacity, 1));
        }
        shadeRecords = alive;
        var command = {
          cmd: "shade",
          viewport: { w: shadeRound(videoRect.width, 2), h: shadeRound(videoRect.height, 2) },
          items: items
        };
        var payload = JSON.stringify(command);
        global.__TV_EMULATOR_SHADES = command;
        if (payload !== shadeLastPayload || Date.now() - shadeLastPost >= 1000) {
          shadeLastPayload = payload;
          shadeLastPost = Date.now();
          post("/__emulator/avplay", payload);
        }
        var debugText = debug.join(",");
        if (profile.debug && debugText !== shadeLastDebug) {
          shadeLastDebug = debugText;
          post("/__emulator/log", "avplay-shade-frame " + (debugText || "empty"));
        }
      }
    }
  }

  function shadeFrame() {
    try {
      shadeFrameNow();
      global.__TV_EMULATOR_SHADE_ERROR = "";
    } catch (err) {
      global.__TV_EMULATOR_SHADE_ERROR = String(err && err.message ? err.message : err);
    }
    scheduleShadeFrame();
  }

  function openVideoHole() {
    var keyColor = "#010001";
    var objects = document.getElementsByTagName("object");
    var i;
    for (i = 0; i < objects.length; i++) {
      var node = objects[i];
      if (String(node.getAttribute("type") || "").toLowerCase() !== "application/avplayer") {
        continue;
      }
      var parent = node.parentNode;
      if (!parent || !parent.style) {
        continue;
      }
      var parentPosition = "static";
      if (global.getComputedStyle) {
        parentPosition = global.getComputedStyle(parent).position || "static";
      }
      if (parentPosition === "static") {
        parent.style.position = "relative";
      }
      node.style.position = "absolute";
      node.style.left = "0";
      node.style.top = "0";
      node.style.width = "100%";
      node.style.height = "100%";
      node.style.backgroundColor = keyColor;
      if (profile.debug && !node.getAttribute("data-tv-hole")) {
        node.setAttribute("data-tv-hole", "1");
        var scrolling = document.scrollingElement || document.documentElement;
        post("/__emulator/log", "avplay-hole parent=" + parentPosition + " scroll=" + scrolling.scrollHeight + " viewport=" + global.innerWidth + "x" + global.innerHeight);
      }
      var kids = parent.children;
      if (!kids) {
        continue;
      }
      var j;
      for (j = 0; j < kids.length; j++) {
        var child = kids[j];
        if (child === node || !child.style) {
          continue;
        }
        var tag = String(child.tagName || "").toUpperCase();
        if (tag === "IMG" || tag === "VIDEO" || tag === "CANVAS") {
          child.style.display = "none";
        }
      }
    }
  }

  // Chromium has no plugin for application/avplayer and would draw a "plugin not supported"
  // placeholder over the key colour (Chromium 47 also opens an infobar). An <object> that has
  // fallback content never asks for a plugin, so every AVPlay object gets an empty child before
  // the engine looks at it. Chromium 47 looks synchronously, hence the DOM method wrappers.
  function installPlayerFallback() {
    var nodeProto = global.Node && global.Node.prototype;
    var elementProto = global.Element && global.Element.prototype;
    if (!nodeProto || !elementProto || !nodeProto.appendChild) {
      return;
    }
    var origAppend = nodeProto.appendChild;
    var origInsert = nodeProto.insertBefore;
    var origReplace = nodeProto.replaceChild;
    var origSetAttribute = elementProto.setAttribute;

    function isPlayer(node, type) {
      if (!node || node.nodeType !== 1 || String(node.nodeName).toUpperCase() !== "OBJECT") {
        return false;
      }
      var value = arguments.length > 1 ? type : node.getAttribute("type");
      return String(value).toLowerCase() === "application/avplayer";
    }

    function fill(node) {
      for (var child = node.firstChild; child; child = child.nextSibling) {
        if (child.nodeType === 1 && String(child.nodeName).toUpperCase() !== "PARAM") {
          return;
        }
      }
      var filler = document.createElement("span");
      origSetAttribute.call(filler, "data-tv-emulator", "avplay");
      origAppend.call(node, filler);
    }

    function prepare(node) {
      if (!node || (node.nodeType !== 1 && node.nodeType !== 11)) {
        return;
      }
      if (isPlayer(node)) {
        fill(node);
      }
      var list = node.querySelectorAll ? node.querySelectorAll("object") : [];
      for (var i = 0; i < list.length; i++) {
        if (isPlayer(list[i])) {
          fill(list[i]);
        }
      }
    }

    nodeProto.appendChild = function (child) {
      prepare(child);
      return origAppend.apply(this, arguments);
    };
    nodeProto.insertBefore = function (child) {
      prepare(child);
      return origInsert.apply(this, arguments);
    };
    nodeProto.replaceChild = function (child) {
      prepare(child);
      return origReplace.apply(this, arguments);
    };
    elementProto.setAttribute = function (name, value) {
      if (String(name).toLowerCase() === "type" && isPlayer(this, value)) {
        fill(this);
      }
      return origSetAttribute.apply(this, arguments);
    };

    var objectProto = global.HTMLObjectElement && global.HTMLObjectElement.prototype;
    var typeDescriptor = objectProto && Object.getOwnPropertyDescriptor(objectProto, "type");
    if (typeDescriptor && typeDescriptor.set && typeDescriptor.configurable) {
      Object.defineProperty(objectProto, "type", {
        configurable: true,
        enumerable: typeDescriptor.enumerable,
        get: typeDescriptor.get,
        set: function (value) {
          if (isPlayer(this, value)) {
            fill(this);
          }
          typeDescriptor.set.call(this, value);
        }
      });
    }

    if (global.MutationObserver && document.documentElement) {
      new global.MutationObserver(function (records) {
        for (var i = 0; i < records.length; i++) {
          var record = records[i];
          if (record.type === "attributes") {
            if (isPlayer(record.target)) {
              fill(record.target);
            }
            continue;
          }
          for (var j = 0; j < record.addedNodes.length; j++) {
            prepare(record.addedNodes[j]);
          }
        }
      }).observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ["type"] });
    }
  }

  function installWebOs() {
    var info = appInfo();
    function device() {
      var major = parseInt(profile.platformVersion, 10);
      if (isNaN(major)) {
        major = 4;
      }
      return {
        modelName: product.model || "webOS Emulator",
        version: profile.platformVersion || "4.0",
        versionMajor: major,
        versionMinor: 0,
        sdkVersion: (profile.platformVersion || "4.0") + ".0",
        screenWidth: 1920,
        screenHeight: 1080,
        uhd: product.uhd === true,
        uhd8K: false,
        hdr10: false,
        dolbyVision: false,
        brandName: product.brand || "LG",
        manufacturer: product.brand || "LG",
        tuner: true
      };
    }

    global.webOS = {
      libVersion: "1.2.4-emulator",
      platform: { tv: true },
      appInfo: info,
      deviceInfo: function (callback) {
        if (callback) {
          callback(device());
        }
      },
      systemInfo: function (callback) {
        if (callback) {
          callback({ country: "PL", smartServiceCountry: "PL", timezone: "Europe/Warsaw" });
        }
      },
      fetchAppId: function () {
        return info.id || "com.emulator.app";
      },
      fetchAppInfo: function (callback) {
        global.webOS.appInfo = info;
        if (callback) {
          callback(info);
        }
      },
      fetchAppRootPath: function () {
        return "/";
      },
      platformBack: function () {
        if (global.history && global.history.length > 1) {
          global.history.back();
          return;
        }
        requestExit();
      },
      keyboard: {
        isShowing: function () { return false; }
      },
      service: {
        request: function (uri, params) {
          params = params || {};
          var method = params.method || "";
          var full = uri || "";
          if (method && full.indexOf(method) === -1) {
            full = full.replace(/\/$/, "") + "/" + method;
          }
          var req = {
            uri: full,
            params: params.parameters || {},
            subscribe: params.subscribe === true,
            cancel: function () {},
            send: function () {}
          };
          global.setTimeout(function () {
            var ok = false;
            var response = { returnValue: false, errorCode: -1, errorText: "Unknown method " + full };
            if (full.indexOf("getSystemInfo") !== -1 || method === "getSystemInfo") {
              ok = true;
              response = {
                returnValue: true,
                modelName: product.model || "",
                firmwareVersion: product.firmware || "",
                sdkVersion: (profile.platformVersion || "4.0") + ".0",
                UHD: product.uhd === true ? "true" : "false"
              };
            }
            if (ok && params.onSuccess) {
              params.onSuccess(response);
            } else if (!ok && params.onFailure) {
              params.onFailure(response);
            }
            if (params.onComplete) {
              params.onComplete(response);
            }
          }, 0);
          return req;
        }
      }
    };

    global.PalmSystem = {
      identifier: info.id || "com.emulator.app",
      stageReady: function () {},
      platformBack: function () { global.webOS.platformBack(); }
    };
    installWebOsPlayback();
  }

  /*
   * webOS 4 plays through the HTML engine and Widevine, not AVPlay.
   * Chromium 53 has neither a CDM nor H.264, so the page keeps choosing
   * variants and downloading segments. The Edge plane owns the real Widevine
   * session and the decoder. The page video is only a clock.
   */
  function installWebOsPlayback() {
    var keyColor = "#010001";
    var mediaSeq = 1;
    var head = 0;
    var polling = false;
    var planeVideo = null;
    var mseByUrl = {};
    var sessions = {};
    var pendingMedia = {};
    var nativeMediaSource = global.MediaSource;
    var nativeCreateUrl = global.URL && global.URL.createObjectURL;
    var nativeRevokeUrl = global.URL && global.URL.revokeObjectURL;

    var style = document.createElement("style");
    style.setAttribute("data-tv-emulator", "widevine");
    style.appendChild(document.createTextNode(
      "video[data-tv-plane=\"1\"]{opacity:0 !important;background:transparent !important;}" +
      "div[data-tv-hole=\"widevine\"]{position:absolute;left:0;top:0;width:100%;height:100%;background-color:" + keyColor + " !important;pointer-events:none;}"
    ));
    var parent = document.head || document.documentElement;
    if (parent) {
      parent.appendChild(style);
    }
    installShadeTracker();
    startPolling();
    installMediaElementBridge();
    installMseBridge();
    installEmeBridge();
    installSourceBridge();

    function nextId(prefix) {
      mediaSeq += 1;
      return prefix + mediaSeq;
    }

    function makeRanges(pairs) {
      return {
        length: pairs.length,
        start: function (index) { return pairs[index][0]; },
        end: function (index) { return pairs[index][1]; }
      };
    }

    function makeStatuses(list) {
      var items = [];
      // One zero byte is hex "00". Shaka treats that single id as a global
      // status. Sixteen zero bytes do not match the manifest key ids, so half
      // a second after "usable" every variant is disabled and playback reloads.
      var key = new Uint8Array(1);
      var i;
      var statuses = list && list.length ? list : ["usable"];
      for (i = 0; i < statuses.length; i++) {
        items.push({ status: statuses[i], keyId: key });
      }
      return {
        size: items.length,
        get: function () { return items[0].status; },
        forEach: function (callback) {
          var j;
          for (j = 0; j < items.length; j++) {
            callback(items[j].status, items[j].keyId);
          }
        }
      };
    }

    function emitter(target) {
      target._on = {};
      target.addEventListener = function (type, listener) {
        if (!this._on[type]) {
          this._on[type] = [];
        }
        this._on[type].push(listener);
      };
      target.removeEventListener = function (type, listener) {
        var list = this._on[type] || [];
        var next = [];
        var i;
        for (i = 0; i < list.length; i++) {
          if (list[i] !== listener) {
            next.push(list[i]);
          }
        }
        this._on[type] = next;
      };
      target.dispatchEvent = function (event) {
        var list = (this._on[event.type] || []).slice();
        var i;
        for (i = 0; i < list.length; i++) {
          list[i].call(this, event);
        }
        return true;
      };
    }

    function dispatch(target, event) {
      if (!event.type && typeof event === "string") {
        event = { type: event };
      }
      if (target.dispatchEvent && target._on) {
        target.dispatchEvent(event);
        return;
      }
      var nativeEvent;
      try {
        nativeEvent = new Event(event.type || event);
      } catch (ignore) {
        nativeEvent = document.createEvent("Event");
        nativeEvent.initEvent(event.type || event, false, false);
      }
      target.dispatchEvent(nativeEvent);
    }

    function fire(target, type) {
      dispatch(target, { type: type });
    }

    function toArrayBuffer(data) {
      if (!data) {
        return new ArrayBuffer(0);
      }
      if (data instanceof ArrayBuffer) {
        return data;
      }
      if (data.buffer && data.byteLength != null) {
        return data.buffer.slice(data.byteOffset || 0, (data.byteOffset || 0) + data.byteLength);
      }
      return new ArrayBuffer(0);
    }

    function bytesFromBase64(text) {
      var binary = global.atob(text || "");
      var out = new Uint8Array(binary.length);
      var i;
      for (i = 0; i < binary.length; i++) {
        out[i] = binary.charCodeAt(i);
      }
      return out.buffer;
    }

    function isTextMime(mime) {
      return /text\/|vtt|ttml|subtitle/i.test(String(mime || ""));
    }

    function isPlaneUrl(url) {
      return typeof url === "string" && url.indexOf("blob:tv-emulator/") === 0;
    }

    function postControl(command) {
      command.cmd = "media";
      try {
        var xhr = new XMLHttpRequest();
        xhr.open("POST", "/__emulator/avplay", false);
        xhr.setRequestHeader("Content-Type", "application/json");
        xhr.send(JSON.stringify(command));
      } catch (ignore) {}
    }

    function postBinary(op, id, buffer, extra) {
      return new Promise(function (resolve, reject) {
        var xhr = new XMLHttpRequest();
        var query = "/__emulator/media?op=" + encodeURIComponent(op) + "&id=" + encodeURIComponent(id || "");
        if (extra && extra.initDataType) {
          query += "&initDataType=" + encodeURIComponent(extra.initDataType);
        }
        xhr.open("POST", query, true);
        xhr.onload = function () {
          if (xhr.status >= 200 && xhr.status < 300) {
            resolve();
          } else {
            reject(new Error("media " + xhr.status));
          }
        };
        xhr.onerror = function () { reject(new Error("media network")); };
        xhr.send(buffer);
      });
    }

    function arm(key) {
      return new Promise(function (resolve, reject) {
        pendingMedia[key] = { resolve: resolve, reject: reject };
        global.setTimeout(function () {
          if (!pendingMedia[key]) {
            return;
          }
          delete pendingMedia[key];
          reject(new Error(key + " timeout"));
        }, 30000);
      });
    }

    function settle(key, ok, value) {
      var waiter = pendingMedia[key];
      if (!waiter) {
        return false;
      }
      delete pendingMedia[key];
      if (ok) {
        waiter.resolve(value);
      } else {
        waiter.reject(value || new Error(key));
      }
      return true;
    }

    function ensureHole(video) {
      var parentNode = video.parentNode;
      if (!parentNode) {
        return;
      }
      var position = "static";
      if (global.getComputedStyle) {
        position = global.getComputedStyle(parentNode).position || "static";
      }
      if (position === "static" && parentNode.style) {
        parentNode.style.position = "relative";
      }
      var hole = video.__tvHole;
      if (!hole) {
        hole = document.createElement("div");
        hole.setAttribute("data-tv-hole", "widevine");
        video.__tvHole = hole;
      }
      if (hole.parentNode !== parentNode) {
        parentNode.insertBefore(hole, video);
      }
      video.setAttribute("data-tv-plane", "1");
    }

    function releaseCanPlay(video) {
      if (!video || video.__tvCanPlay || video.__tvReady < 3) {
        return;
      }
      video.__tvCanPlay = true;
      global.setTimeout(function () {
        if (!video.__tvPlane) {
          return;
        }
        fire(video, "canplay");
        fire(video, "canplaythrough");
      }, 0);
    }

    function applyClock(state) {
      var video = planeVideo;
      if (!video || !state) {
        return;
      }
      var previous = video.__tvTime || 0;
      var reported = Number(state.time) || 0;
      var held = video.__tvHoldTime;
      if (held == null || Math.abs(reported - held) < 1.5 || Date.now() - (video.__tvHoldTimeAt || 0) > 3000) {
        video.__tvHoldTime = null;
        video.__tvTime = reported;
      }
      if (state.duration) {
        video.__tvDuration = Number(state.duration) || 0;
        video.__tvSeekable = makeRanges([[0, video.__tvDuration]]);
      }
      if (video.__tvHoldPause) {
        if (state.paused !== false) {
          video.__tvHoldPause = false;
        }
      } else if (video.__tvHoldPlay) {
        video.__tvPaused = false;
        if (state.paused === false) {
          video.__tvHoldPlay = false;
        }
      } else {
        video.__tvPaused = state.paused !== false;
      }
      video.__tvReady = state.ready || video.__tvReady || 0;
      video.__tvWidth = state.width || video.__tvWidth || 0;
      video.__tvHeight = state.height || video.__tvHeight || 0;
      if (state.videoRanges) {
        video.__tvBuffered = makeRanges(state.videoRanges);
      }
      if (!video.__tvMetadata && (video.__tvWidth || video.__tvDuration)) {
        video.__tvMetadata = true;
        fire(video, "loadedmetadata");
        fire(video, "loadeddata");
      }
      releaseCanPlay(video);
      if (video.__tvTime !== previous) {
        fire(video, "timeupdate");
      }
    }

    function finishBuffer(buffer, ranges) {
      buffer.updating = false;
      buffer.buffered = makeRanges(ranges || []);
      dispatch(buffer, { type: "updateend", target: buffer });
    }

    function failBuffer(buffer, error) {
      buffer.updating = false;
      dispatch(buffer, { type: "error", target: buffer, error: error });
      dispatch(buffer, { type: "updateend", target: buffer });
    }

    function createBuffer(mime) {
      var buffer = {
        updating: false,
        buffered: makeRanges([]),
        _id: nextId("b"),
        _local: isTextMime(mime),
        _mode: "segments",
        _offset: 0,
        _windowStart: 0,
        _windowEnd: Infinity
      };
      emitter(buffer);
      Object.defineProperty(buffer, "mode", {
        configurable: true,
        get: function () { return buffer._mode; },
        set: function (value) {
          buffer._mode = value === "sequence" ? "sequence" : "segments";
          if (!buffer._local) {
            postControl({ op: "mode", id: buffer._id, mode: buffer._mode });
          }
        }
      });
      Object.defineProperty(buffer, "timestampOffset", {
        configurable: true,
        get: function () { return buffer._offset; },
        set: function (value) {
          buffer._offset = Number(value) || 0;
          if (!buffer._local) {
            postControl({ op: "offset", id: buffer._id, seconds: buffer._offset });
          }
        }
      });
      Object.defineProperty(buffer, "appendWindowStart", {
        configurable: true,
        get: function () { return buffer._windowStart; },
        set: function (value) {
          buffer._windowStart = Number(value) || 0;
          postWindow(buffer);
        }
      });
      Object.defineProperty(buffer, "appendWindowEnd", {
        configurable: true,
        get: function () { return buffer._windowEnd; },
        set: function (value) {
          buffer._windowEnd = Number(value);
          postWindow(buffer);
        }
      });
      buffer.appendBuffer = function (data) {
        if (buffer.updating) {
          var busy = new Error("SourceBuffer is updating");
          busy.name = "InvalidStateError";
          throw busy;
        }
        buffer.updating = true;
        if (buffer._local) {
          global.setTimeout(function () { finishBuffer(buffer, [[0, 0.001]]); }, 0);
          return;
        }
        var wait = arm("mse-update:" + buffer._id);
        postBinary("append", buffer._id, toArrayBuffer(data)).catch(function (error) {
          failBuffer(buffer, error);
          settle("mse-update:" + buffer._id, false, error);
        });
        wait.then(function (evt) {
          finishBuffer(buffer, evt.ranges || []);
        }).catch(function (error) {
          if (buffer.updating) {
            failBuffer(buffer, error);
          }
        });
      };
      buffer.remove = function (start, end) {
        if (buffer.updating) {
          var busyRemove = new Error("SourceBuffer is updating");
          busyRemove.name = "InvalidStateError";
          throw busyRemove;
        }
        buffer.updating = true;
        if (buffer._local || !(end > start)) {
          global.setTimeout(function () { finishBuffer(buffer, []); }, 0);
          return;
        }
        var waitRemove = arm("mse-update:" + buffer._id);
        postControl({ op: "remove", id: buffer._id, start: Number(start) || 0, end: Number(end) || 0 });
        waitRemove.then(function (evt) {
          finishBuffer(buffer, evt.ranges || []);
        }).catch(function (error) {
          failBuffer(buffer, error);
        });
      };
      buffer.abort = function () {
        buffer.updating = false;
        if (!buffer._local) {
          postControl({ op: "abort", id: buffer._id });
        }
      };
      buffer.changeType = function (next) {
        if (!buffer._local) {
          postControl({ op: "type", id: buffer._id, mime: String(next || "") });
        }
      };
      return buffer;
    }

    function postWindow(buffer) {
      if (buffer._local) {
        return;
      }
      postControl({
        op: "window",
        id: buffer._id,
        start: buffer._windowStart,
        end: buffer._windowEnd === Infinity ? null : buffer._windowEnd
      });
    }

    function detachPlane(video) {
      if (!video || !video.__tvPlane) {
        return;
      }
      video.__tvPlane = false;
      video.__tvSrc = "";
      video.removeAttribute("data-tv-plane");
      if (video.__tvHole && video.__tvHole.parentNode) {
        video.__tvHole.parentNode.removeChild(video.__tvHole);
      }
      video.__tvHole = null;
      if (planeVideo === video) {
        planeVideo = null;
        postControl({ op: "close" });
      }
    }

    function attachPlane(video, url) {
      var source = mseByUrl[url];
      if (!source) {
        return;
      }
      if (planeVideo && planeVideo !== video) {
        detachPlane(planeVideo);
      }
      planeVideo = video;
      video.__tvPlane = true;
      video.__tvSrc = url;
      video.__tvSource = source;
      video.__tvPaused = true;
      video.__tvHoldPause = false;
      video.__tvHoldPlay = false;
      video.__tvHoldTime = null;
      video.__tvTime = 0;
      video.__tvDuration = NaN;
      video.__tvReady = 0;
      video.__tvSeeking = false;
      video.__tvEnded = false;
      video.__tvMetadata = false;
      video.__tvCanPlay = false;
      video.__tvBuffered = makeRanges([]);
      video.__tvSeekable = makeRanges([]);
      video.__tvWidth = 0;
      video.__tvHeight = 0;
      video.__tvRate = 1;
      source._video = video;
      ensureHole(video);
      var ready = arm("mse-ready");
      postControl({ op: "open" });
      ready.then(function () {
        if (video.__tvSource !== source) {
          return;
        }
        source.readyState = "open";
        dispatch(source, { type: "sourceopen", target: source });
      }).catch(function (error) {
        source.readyState = "closed";
        dispatch(source, { type: "error", target: source, error: error });
        fire(video, "error");
      });
    }

    function installMediaElementBridge() {
      var media = global.HTMLMediaElement && global.HTMLMediaElement.prototype;
      if (!media) {
        return;
      }
      wrapMedia("src", function (element) {
        return element.__tvSrc || "";
      }, function (element, value) {
        var url = value == null ? "" : String(value);
        if (isPlaneUrl(url)) {
          attachPlane(element, url);
          return;
        }
        detachPlane(element);
      });
      wrapMedia("currentTime", function (element) {
        return element.__tvTime || 0;
      }, function (element, value) {
        element.__tvTime = Number(value) || 0;
        element.__tvHoldTime = element.__tvTime;
        element.__tvHoldTimeAt = Date.now();
        element.__tvSeeking = true;
        element.__tvEnded = false;
        fire(element, "seeking");
        postControl({ op: "seek", seconds: element.__tvTime });
      });
      wrapMedia("duration", function (element) {
        return element.__tvDuration;
      });
      wrapMedia("paused", function (element) {
        return element.__tvPaused !== false;
      });
      wrapMedia("ended", function (element) {
        return element.__tvEnded === true;
      });
      wrapMedia("seeking", function (element) {
        return element.__tvSeeking === true;
      });
      wrapMedia("readyState", function (element) {
        return element.__tvReady || 0;
      });
      wrapMedia("networkState", function () {
        return 1;
      });
      wrapMedia("buffered", function (element) {
        return element.__tvBuffered || makeRanges([]);
      });
      wrapMedia("seekable", function (element) {
        return element.__tvSeekable || makeRanges([]);
      });
      wrapMedia("error", function () {
        return null;
      });
      wrapMedia("videoWidth", function (element) {
        return element.__tvWidth || 0;
      });
      wrapMedia("videoHeight", function (element) {
        return element.__tvHeight || 0;
      });
      wrapMedia("playbackRate", function (element) {
        return element.__tvRate || 1;
      }, function (element, value) {
        element.__tvRate = Number(value) || 1;
        postControl({ op: "rate", rate: element.__tvRate });
        fire(element, "ratechange");
      });

      var originalPlay = media.play;
      var originalPause = media.pause;
      var originalLoad = media.load;
      media.play = function () {
        if (!this.__tvPlane) {
          return originalPlay.apply(this, arguments);
        }
        this.__tvHoldPause = false;
        this.__tvHoldPlay = true;
        this.__tvPaused = false;
        this.__tvEnded = false;
        postControl({ op: "play" });
        fire(this, "play");
        fire(this, "playing");
        return Promise.resolve();
      };
      media.pause = function () {
        if (!this.__tvPlane) {
          return originalPause.apply(this, arguments);
        }
        this.__tvHoldPlay = false;
        this.__tvHoldPause = true;
        this.__tvPaused = true;
        postControl({ op: "pause" });
        fire(this, "pause");
      };
      media.load = function () {
        if (this.__tvPlane || absorbVideoSources(this)) {
          return;
        }
        return originalLoad.apply(this, arguments);
      };

      function wrapMedia(name, getter, setter) {
        var desc = Object.getOwnPropertyDescriptor(media, name);
        if (!desc || !desc.configurable || !desc.get) {
          return;
        }
        Object.defineProperty(media, name, {
          configurable: true,
          enumerable: desc.enumerable,
          get: function () {
            if (this.__tvPlane) {
              return getter(this);
            }
            return desc.get.call(this);
          },
          set: function (value) {
            var planeUrl = name === "src" && isPlaneUrl(value == null ? "" : String(value));
            if (setter && (this.__tvPlane || planeUrl)) {
              setter(this, value);
              return;
            }
            if (desc.set) {
              desc.set.call(this, value);
            }
          }
        });
      }
    }

    function planeSourceUrl(child) {
      if (!child || child.nodeType !== 1 || String(child.tagName || "").toUpperCase() !== "SOURCE") {
        return "";
      }
      var url = child.__tvPlaneUrl || child.getAttribute("src") || child.src || "";
      return isPlaneUrl(String(url)) ? String(url) : "";
    }

    function stripPlaneSource(child) {
      var url = planeSourceUrl(child);
      if (!url) {
        return "";
      }
      child.__tvPlaneUrl = url;
      child.removeAttribute("src");
      return url;
    }

    function absorbVideoSources(video) {
      var sources = video.getElementsByTagName ? video.getElementsByTagName("source") : [];
      var i;
      for (i = 0; i < sources.length; i++) {
        var url = stripPlaneSource(sources[i]);
        if (url) {
          attachPlane(video, url);
          return true;
        }
      }
      return false;
    }

    // Shaka 5 appends <source src="blob:tv-emulator/..."> and then calls
    // video.load(). Inserting that element, even after clearing src, makes the
    // media element fail resource selection. Shaka treats the error as a fatal
    // engine failure and reloads, which is the 1200 loop. Keep the source out
    // of the document and open the plane from the URL it carried.
    function holdPlaneSource(parentNode, child) {
      if (!parentNode || String(parentNode.tagName || "").toUpperCase() !== "VIDEO") {
        return "";
      }
      var url = planeSourceUrl(child);
      if (!url) {
        return "";
      }
      child.__tvPlaneUrl = url;
      child.removeAttribute("src");
      attachPlane(parentNode, url);
      return url;
    }

    function installSourceBridge() {
      var node = global.Node && global.Node.prototype;
      if (!node || !node.appendChild || !node.insertBefore) {
        return;
      }
      var originalAppend = node.appendChild;
      var originalInsert = node.insertBefore;

      global.addEventListener("error", function (event) {
        var target = event.target || event.srcElement;
        if (!target || target === global) {
          return;
        }
        var tag = String(target.tagName || "").toUpperCase();
        var planeError = (tag === "VIDEO" && target.__tvPlane) || (tag === "SOURCE" && target.__tvPlaneUrl);
        if (!planeError) {
          return;
        }
        if (event.stopImmediatePropagation) {
          event.stopImmediatePropagation();
        }
        if (event.stopPropagation) {
          event.stopPropagation();
        }
      }, true);

      node.appendChild = function (child) {
        if (holdPlaneSource(this, child)) {
          return child;
        }
        return originalAppend.apply(this, arguments);
      };
      node.insertBefore = function (child) {
        if (holdPlaneSource(this, child)) {
          return child;
        }
        return originalInsert.apply(this, arguments);
      };
    }

    function installMseBridge() {
      function TvMediaSource() {
        this.readyState = "closed";
        this._duration = NaN;
        this._buffers = [];
        this.sourceBuffers = this._buffers;
        this.activeSourceBuffers = this._buffers;
        this._id = nextId("ms");
        this.__tvMse = true;
        emitter(this);
      }
      Object.defineProperty(TvMediaSource.prototype, "duration", {
        configurable: true,
        get: function () { return this._duration; },
        set: function (value) {
          this._duration = Number(value);
          if (this._video) {
            this._video.__tvDuration = this._duration;
            this._video.__tvSeekable = makeRanges([[0, this._duration]]);
            fire(this._video, "durationchange");
          }
          postControl({ op: "duration", seconds: this._duration === Infinity ? "Infinity" : this._duration });
        }
      });
      TvMediaSource.prototype.addSourceBuffer = function (mime) {
        var buffer = createBuffer(mime);
        this._buffers.push(buffer);
        this._buffers.item = function (index) { return this[index] || null; };
        if (!buffer._local) {
          postControl({ op: "add", id: buffer._id, mime: String(mime || "") });
        }
        return buffer;
      };
      TvMediaSource.prototype.removeSourceBuffer = function (buffer) {
        var next = [];
        var i;
        for (i = 0; i < this._buffers.length; i++) {
          if (this._buffers[i] !== buffer) {
            next.push(this._buffers[i]);
          }
        }
        this._buffers = next;
        this.sourceBuffers = next;
        this.activeSourceBuffers = next;
        if (buffer && !buffer._local) {
          postControl({ op: "abort", id: buffer._id });
        }
      };
      TvMediaSource.prototype.endOfStream = function () {
        this.readyState = "ended";
        postControl({ op: "end" });
        dispatch(this, { type: "sourceended", target: this });
      };
      TvMediaSource.isTypeSupported = function () { return true; };
      if (nativeMediaSource) {
        TvMediaSource.MEDIA_SOURCE_TYPE = "tv-emulator";
      }
      global.MediaSource = TvMediaSource;
      if (global.URL && nativeCreateUrl) {
        global.URL.createObjectURL = function (object) {
          if (object && object.__tvMse) {
            var url = "blob:tv-emulator/" + object._id;
            mseByUrl[url] = object;
            return url;
          }
          return nativeCreateUrl.apply(global.URL, arguments);
        };
        global.URL.revokeObjectURL = function (url) {
          if (isPlaneUrl(url)) {
            delete mseByUrl[url];
            return;
          }
          if (nativeRevokeUrl) {
            nativeRevokeUrl.apply(global.URL, arguments);
          }
        };
      }
    }

    function installEmeBridge() {
      var navigatorObject = global.navigator;
      if (!navigatorObject) {
        return;
      }
      var nativeRequest = navigatorObject.requestMediaKeySystemAccess;
      var nativeSetMediaKeys = global.HTMLMediaElement && global.HTMLMediaElement.prototype.setMediaKeys;
      if (global.HTMLMediaElement && nativeSetMediaKeys) {
        global.HTMLMediaElement.prototype.setMediaKeys = function (keys) {
          if (keys && keys.__tvEme) {
            this.__tvMediaKeys = keys;
            return Promise.resolve();
          }
          if (!keys && this.__tvMediaKeys) {
            this.__tvMediaKeys = null;
            return Promise.resolve();
          }
          return nativeSetMediaKeys.apply(this, arguments);
        };
      }
      navigatorObject.requestMediaKeySystemAccess = function (keySystem, configs) {
        if (keySystem === "com.widevine.alpha") {
          var requested = configs && configs.length ? configs[0] : {};
          return Promise.resolve(fakeAccess(requested));
        }
        if (nativeRequest) {
          return nativeRequest.apply(navigatorObject, arguments);
        }
        return Promise.reject(new Error("unsupported key system"));
      };

      function fakeAccess(requested) {
        var config = {};
        var key;
        for (key in requested) {
          if (Object.prototype.hasOwnProperty.call(requested, key)) {
            config[key] = requested[key];
          }
        }
        if (!config.sessionTypes) {
          config.sessionTypes = ["temporary"];
        }
        return {
          keySystem: "com.widevine.alpha",
          __tvEme: true,
          getConfiguration: function () { return config; },
          createMediaKeys: function () { return Promise.resolve(fakeKeys()); }
        };
      }

      function fakeKeys() {
        return {
          __tvEme: true,
          createSession: function (type) { return fakeSession(type || "temporary"); },
          setServerCertificate: function (certificate) {
            var bytes = toArrayBuffer(certificate);
            if (!bytes.byteLength) {
              return Promise.resolve(true);
            }
            var wait = arm("cert:cdm");
            postBinary("cert", "cdm", bytes).catch(function (error) {
              settle("cert:cdm", false, error);
            });
            return wait.then(function (evt) { return !evt || evt.ok !== false; });
          },
          getStatusForPolicy: function () { return Promise.resolve("usable"); }
        };
      }

      function fakeSession(type) {
        var session = {
          sessionId: "",
          sessionType: type,
          expiration: NaN,
          keyStatuses: makeStatuses(["status-pending"]),
          _id: nextId("s")
        };
        emitter(session);
        var closeResolve;
        session.closed = new Promise(function (resolve) { closeResolve = resolve; });
        sessions[session._id] = session;
        session.generateRequest = function (initDataType, initData) {
          var wait = arm("generate:" + session._id);
          postBinary("generate", session._id, toArrayBuffer(initData), { initDataType: initDataType || "cenc" }).catch(function (error) {
            settle("generate:" + session._id, false, error);
          });
          return wait.then(function (evt) {
            if (evt && evt.sessionId) {
              session.sessionId = evt.sessionId;
            }
          });
        };
        session.update = function (response) {
          var wait = arm("update:" + session._id);
          postBinary("update", session._id, toArrayBuffer(response)).catch(function (error) {
            settle("update:" + session._id, false, error);
          });
          return wait.then(function () {});
        };
        session.close = function () {
          delete sessions[session._id];
          closeResolve();
          return Promise.resolve();
        };
        session.remove = function () { return session.close(); };
        session.load = function () { return Promise.resolve(false); };
        return session;
      }
    }

    function onPlaneEvent(evt) {
      if (!evt || !evt.type) {
        return;
      }
      if (evt.type === "mse-ready") {
        settle("mse-ready", true, evt);
      } else if (evt.type === "mse-error") {
        var failure = new Error(evt.message || "media plane failed");
        settle("mse-ready", false, failure);
        if (evt.op === "cert") {
          settle("cert:cdm", false, failure);
        }
        if (evt.id) {
          settle("mse-update:" + evt.id, false, failure);
          settle("generate:" + evt.id, false, failure);
          settle("update:" + evt.id, false, failure);
        }
      } else if (evt.type === "mse-update") {
        settle("mse-update:" + evt.id, true, evt);
      } else if (evt.type === "mse-state") {
        applyClock(evt.state);
      } else if (evt.type === "mse-seeked") {
        if (planeVideo) {
          planeVideo.__tvSeeking = false;
        }
        applyClock(evt.state);
        if (planeVideo) {
          fire(planeVideo, "seeked");
        }
      } else if (evt.type === "mse-ended") {
        if (planeVideo) {
          planeVideo.__tvEnded = true;
          planeVideo.__tvPaused = true;
          fire(planeVideo, "ended");
        }
      } else if (evt.type === "eme-message") {
        var session = sessions[evt.id];
        if (session) {
          dispatch(session, {
            type: "message",
            messageType: evt.messageType || "license-request",
            message: bytesFromBase64(evt.bytes || ""),
            target: session
          });
        }
      } else if (evt.type === "eme-generated") {
        settle("generate:" + evt.id, true, evt);
      } else if (evt.type === "eme-updated") {
        settle("update:" + evt.id, true, evt);
      } else if (evt.type === "eme-cert") {
        settle("cert:" + evt.id, true, evt);
      } else if (evt.type === "eme-keys") {
        var keyed = sessions[evt.id];
        if (keyed) {
          if (evt.sessionId) {
            keyed.sessionId = evt.sessionId;
          }
          keyed.keyStatuses = makeStatuses(evt.statuses);
          dispatch(keyed, { type: "keystatuseschange", target: keyed });
        }
      }
    }

    function startPolling() {
      if (polling) {
        return;
      }
      polling = true;
      try {
        var xhr = new XMLHttpRequest();
        xhr.open("GET", "/__emulator/avplay/events?since=-1", false);
        xhr.send();
        head = JSON.parse(xhr.responseText).head;
      } catch (ignore) {
        head = 0;
      }
      poll();
    }

    function poll() {
      var xhr = new XMLHttpRequest();
      xhr.open("GET", "/__emulator/avplay/events?since=" + head, true);
      xhr.onload = function () {
        var data = null;
        try {
          data = JSON.parse(xhr.responseText);
        } catch (ignore) {
          data = null;
        }
        if (data) {
          head = data.head;
          var i;
          for (i = 0; i < data.events.length; i++) {
            try {
              onPlaneEvent(data.events[i]);
            } catch (err) {
              post("/__emulator/log", "widevine event " + (err && err.message ? err.message : err));
            }
          }
        }
        global.setTimeout(poll, 0);
      };
      xhr.onerror = function () {
        global.setTimeout(poll, 1000);
      };
      xhr.send();
    }
  }
})(window);
