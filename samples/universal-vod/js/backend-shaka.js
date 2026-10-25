/* ES5. webOS and other HTML profiles: Shaka drives a video element. */
function createShakaBackend(video, hooks) {
  var player = null;
  var generation = 0;
  var alive = false;
  var scriptState = "idle";
  var waiters = [];

  function finite(value) {
    return typeof value === "number" && isFinite(value) && value > 0 ? value : 0;
  }

  function errorText(err) {
    if (!err) {
      return "Nie udało się odtworzyć.";
    }
    var code = err.code;
    if (code === 6001) {
      return "Odbiornik odrzucił konfigurację Widevine (6001).";
    }
    if (code === 6006 || code === 6007) {
      return "Serwer licencji nie wydał klucza (" + code + ").";
    }
    if (code >= 6000 && code < 7000) {
      return "Błąd DRM (" + code + ").";
    }
    var message = err.message ? String(err.message) : "";
    if (!message || message.indexOf("\n") !== -1 || message.indexOf("shaka.util.Error") !== -1) {
      return code ? "Błąd odtwarzacza (" + code + ")." : "Nie udało się odtworzyć.";
    }
    return code ? message + " (" + code + ")" : message;
  }

  function ensureEmeMarkers() {
    var nav = window.navigator;
    if (!nav || typeof nav.requestMediaKeySystemAccess !== "function") {
      return;
    }
    if (typeof window.MediaKeySystemAccess !== "function") {
      window.MediaKeySystemAccess = function () {};
    }
    if (!window.MediaKeySystemAccess.prototype.getConfiguration) {
      window.MediaKeySystemAccess.prototype.getConfiguration = function () {
        return {};
      };
    }
    if (typeof window.MediaKeys !== "function") {
      window.MediaKeys = function () {};
    }
  }

  video.addEventListener("timeupdate", function () {
    if (!alive) {
      return;
    }
    hooks.onTime(video.currentTime || 0, finite(video.duration));
  });
  video.addEventListener("ended", function () {
    if (!alive) {
      return;
    }
    hooks.onEnded();
  });
  video.addEventListener("pause", function () {
    if (!alive || video.ended) {
      return;
    }
    hooks.onState("paused");
  });
  video.addEventListener("playing", function () {
    if (!alive) {
      return;
    }
    hooks.onBuffering(false);
    hooks.onState("playing");
  });
  video.addEventListener("waiting", function () {
    if (!alive) {
      return;
    }
    hooks.onBuffering(true);
  });

  function releasePlayer(done) {
    var dying = player;
    player = null;
    function finish() {
      try {
        video.removeAttribute("src");
        video.load();
      } catch (ignore) {}
      if (done) {
        done();
      }
    }
    if (!dying) {
      finish();
      return;
    }
    try {
      var result = dying.destroy();
      if (result && result.then) {
        result.then(finish, finish);
        return;
      }
    } catch (ignore) {}
    finish();
  }

  function withLibrary(done) {
    if (window.shaka && shaka.Player) {
      done();
      return;
    }
    waiters.push(done);
    if (scriptState === "loading") {
      return;
    }
    scriptState = "loading";
    var script = document.createElement("script");
    script.src = "vendor/shaka-player.compiled.js";
    script.onload = function () {
      scriptState = "ready";
      var pending = waiters;
      waiters = [];
      var i;
      for (i = 0; i < pending.length; i++) {
        pending[i]();
      }
    };
    script.onerror = function () {
      scriptState = "idle";
      waiters = [];
      hooks.onError("Nie udało się wczytać Shaka Player.");
    };
    document.getElementsByTagName("head")[0].appendChild(script);
  }

  return {
    load: function (item) {
      alive = false;
      generation += 1;
      var gen = generation;
      try {
        video.pause();
      } catch (ignore) {}
      releasePlayer(function () {
        if (gen !== generation) {
          return;
        }
        withLibrary(function () {
        if (gen !== generation) {
          return;
        }
        try {
          ensureEmeMarkers();
          shaka.polyfill.installAll();
          if (shaka.Player.isBrowserSupported && !shaka.Player.isBrowserSupported()) {
            hooks.onError("Ten odbiornik nie obsługuje odtwarzacza Shaka.");
            return;
          }
          alive = true;
          player = new shaka.Player(video);
          player.addEventListener("error", function (event) {
            if (gen !== generation) {
              return;
            }
            var detail = event && event.detail ? event.detail : event;
            hooks.onError(errorText(detail));
          });
          player.addEventListener("buffering", function (event) {
            if (gen !== generation) {
              return;
            }
            hooks.onBuffering(!!(event && event.buffering));
          });
          if (item.drm === "widevine" && item.license) {
            player.configure({
              drm: {
                servers: {
                  "com.widevine.alpha": item.license
                }
              }
            });
          }
          player.load(item.url).then(function () {
            if (gen !== generation) {
              return;
            }
            hooks.onReady(finite(video.duration));
            var started = video.play();
            if (started && started.then) {
              started.then(function () {}, function () {
                if (gen !== generation) {
                  return;
                }
                hooks.onError("Nie można rozpocząć odtwarzania.");
              });
            }
          }, function (err) {
            if (gen !== generation) {
              return;
            }
            hooks.onError(errorText(err));
          });
        } catch (err) {
          alive = false;
          if (gen !== generation) {
            return;
          }
          hooks.onError(errorText(err));
        }
      });
      });
    },
    play: function () {
      alive = true;
      try {
        var started = video.play();
        if (started && started.then) {
          started.then(function () {}, function () {});
        }
      } catch (err) {
        hooks.onError(errorText(err));
      }
    },
    pause: function () {
      try {
        video.pause();
      } catch (err) {
        hooks.onError(errorText(err));
      }
    },
    stop: function () {
      alive = false;
      generation += 1;
      try {
        video.pause();
      } catch (ignore) {}
      releasePlayer();
    },
    seekTo: function (secondsValue) {
      try {
        video.currentTime = secondsValue;
      } catch (err) {
        hooks.onError(errorText(err));
      }
    }
  };
}
