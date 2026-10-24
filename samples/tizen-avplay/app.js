/* ES5 only. Typical Tizen AVPlay open, prepare, then play order. */
(function () {
  var streams = {
    clear: {
      title: "Angel One (DASH, bez DRM)",
      url: "https://storage.googleapis.com/shaka-demo-assets/angel-one/dash.mpd"
    },
    playready: {
      title: "Big Buck Bunny (DASH, PlayReady)",
      url: "https://test.playready.microsoft.com/media/dash/APPLEENC_CBCS_BBB_1080p/1080p.mpd",
      license: "https://test.playready.microsoft.com/service/rightsmanager.asmx?cfg=(persist:false,contentkey:W31bfVt9W31bfVt9W31bfQ==,ckt:aes128bitcbc)"
    }
  };

  var barNode = document.querySelector(".bar");
  var statusNode = document.getElementById("status");

  function fadeBar() {
    if (!barNode) {
      return;
    }
    barNode.style.opacity = barNode.style.opacity === "0" ? "1" : "0";
  }
  var titleNode = document.getElementById("title");
  var lastTime = 0;

  function show(text) {
    statusNode.textContent = text;
    if (window.console && console.warn) {
      console.warn("sample " + text);
    }
  }

  function seconds(ms) {
    return (Math.floor(ms / 100) / 10).toFixed(1) + " s";
  }

  function listen() {
    webapis.avplay.setListener({
      onbufferingstart: function () { show("buforowanie..."); },
      onbufferingcomplete: function () { show("bufor gotowy"); },
      oncurrentplaytime: function (ms) {
        lastTime = ms;
        show(webapis.avplay.getState() + " " + seconds(ms) + " / " + seconds(webapis.avplay.getDuration()));
      },
      onstreamcompleted: function () { show("koniec"); webapis.avplay.stop(); },
      onevent: function (type, data) { show("event " + type + " " + data); },
      onerror: function (type) { show("onerror " + type); },
      ondrmevent: function (type, data) { show("drm " + type + " " + JSON.stringify(data)); }
    });
  }

  function start(stream) {
    titleNode.textContent = stream.title;
    try {
      webapis.avplay.close();
      webapis.avplay.open(stream.url);
      webapis.avplay.setDisplayRect(0, 0, 1920, 1080);
      webapis.avplay.setDisplayMethod("PLAYER_DISPLAY_MODE_FULL_SCREEN");
      webapis.avplay.setStreamingProperty("ADAPTIVE_INFO", "STARTBITRATE=AVERAGE");
      listen();
      if (stream.license) {
        webapis.avplay.setDrm("PLAYREADY", "SetProperties", JSON.stringify({
          DeleteLicenseAfterUse: false,
          GetChallenge: false,
          LicenseServer: stream.license
        }));
      }
      show("prepareAsync...");
      webapis.avplay.prepareAsync(function () {
        var tracks = webapis.avplay.getTotalTrackInfo();
        show("READY, ścieżek " + tracks.length + ", czas " + seconds(webapis.avplay.getDuration()));
        webapis.avplay.play();
      }, function (err) {
        show("prepareAsync błąd " + (err && err.name) + " " + (err && err.message));
      });
    } catch (err) {
      show("wyjątek " + (err && err.name) + " " + (err && err.message));
    }
  }

  function toggle() {
    var state = webapis.avplay.getState();
    if (state === "PLAYING") {
      webapis.avplay.pause();
      show("PAUSED " + seconds(lastTime));
    } else if (state === "PAUSED" || state === "READY") {
      webapis.avplay.play();
    }
  }

  document.addEventListener("keydown", function (e) {
    switch (e.keyCode) {
      case 13:
      case 49:
        start(streams.clear);
        break;
      case 404:
      case 50:
        start(streams.playready);
        break;
      case 405:
      case 51:
        fadeBar();
        break;
      case 38:
        toggle();
        break;
      case 39:
        webapis.avplay.jumpForward(10000);
        break;
      case 37:
        webapis.avplay.jumpBackward(10000);
        break;
      case 40:
        webapis.avplay.stop();
        show("IDLE (stop)");
        break;
    }
  });

  function reportViewport() {
    if (window.console && console.warn) {
      console.warn("sample viewport " + window.innerWidth + "x" + window.innerHeight +
        " outer " + window.outerWidth + "x" + window.outerHeight + " dpr " + window.devicePixelRatio);
    }
  }
  window.addEventListener("resize", reportViewport);
  reportViewport();

  try {
    tizen.tvinputdevice.registerKey("ColorF1Green");
  } catch (ignore) {}

  start(/playready/.test(location.search) ? streams.playready : streams.clear);
})();
