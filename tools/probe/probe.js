(function () {
  function supportsGrid() {
    if (window.CSS && CSS.supports) {
      try {
        return CSS.supports("display", "grid");
      } catch (err) {}
    }
    return false;
  }

  function flexGap() {
    var host = document.createElement("div");
    host.style.display = "flex";
    host.style.gap = "20px";
    host.style.width = "80px";
    var a = document.createElement("div");
    a.style.width = "10px";
    a.style.height = "10px";
    a.style.flex = "0 0 auto";
    var b = document.createElement("div");
    b.style.width = "10px";
    b.style.height = "10px";
    b.style.flex = "0 0 auto";
    host.appendChild(a);
    host.appendChild(b);
    document.body.appendChild(host);
    var delta = b.offsetLeft - a.offsetLeft;
    document.body.removeChild(host);
    return delta > 15;
  }

  function gamepadCount() {
    if (typeof navigator.getGamepads !== "function") {
      return 0;
    }
    var pads = navigator.getGamepads();
    var count = 0;
    var i;
    if (!pads) {
      return 0;
    }
    for (i = 0; i < pads.length; i++) {
      if (pads[i]) {
        count += 1;
      }
    }
    return count;
  }

  var workerUserAgent = "";

  function send() {
    var body = {
      letParsed: typeof tvEmulatorLetParsed !== "undefined",
      classParsed: window.tvEmulatorClassParsed === true,
      asyncParsed: window.tvEmulatorAsyncParsed === true,
      grid: supportsGrid(),
      flexGap: flexGap(),
      injected: window.__tvSpikeInjected === true,
      gamepadApi: typeof navigator.getGamepads === "function",
      gamepadCount: gamepadCount(),
      keyCode: window.__tvSpikeKey == null ? null : window.__tvSpikeKey,
      userAgent: navigator.userAgent,
      workerUa: workerUserAgent,
      hasRequire: typeof require === "function",
      hasProcess: typeof process !== "undefined",
      chromeObject: typeof window.chrome,
      pluginCount: navigator.plugins ? navigator.plugins.length : -1,
      innerWidth: window.innerWidth,
      innerHeight: window.innerHeight
    };
    var xhr = new XMLHttpRequest();
    xhr.open("POST", "/report", true);
    xhr.setRequestHeader("Content-Type", "application/json");
    xhr.send(JSON.stringify(body));
    var out = document.getElementById("out");
    if (out) {
      out.textContent = JSON.stringify(body);
    }
  }

  window.addEventListener("keydown", function (e) {
    window.__tvSpikeKey = e.keyCode;
    send();
  });

  window.addEventListener("load", function () {
    if (typeof Worker === "function") {
      try {
        var worker = new Worker("probe-worker.js");
        worker.onmessage = function (event) {
          workerUserAgent = String(event.data || "");
          send();
        };
      } catch (err) {}
    }
    setTimeout(send, 700);
  });
})();
