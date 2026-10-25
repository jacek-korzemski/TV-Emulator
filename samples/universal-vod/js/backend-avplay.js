/* ES5. Tizen picture path: webapis.avplay under the HTML layer. */
function createAvplayBackend(hooks) {
  var generation = 0;
  var duration = 0;

  function seconds(ms) {
    return (Number(ms) || 0) / 1000;
  }

  function errorText(err) {
    if (!err) {
      return "Błąd AVPlay.";
    }
    var name = err.name ? err.name + " " : "";
    return name + (err.message ? err.message : String(err));
  }

  function reset() {
    try {
      var state = webapis.avplay.getState();
      if (state !== "NONE" && state !== "IDLE") {
        webapis.avplay.stop();
      }
    } catch (ignore) {}
    try {
      webapis.avplay.close();
    } catch (ignore) {}
  }

  return {
    load: function (item) {
      generation += 1;
      var gen = generation;
      duration = 0;
      try {
        reset();
        webapis.avplay.open(item.url);
        webapis.avplay.setDisplayRect(0, 0, 1920, 1080);
        try {
          webapis.avplay.setDisplayMethod("PLAYER_DISPLAY_MODE_FULL_SCREEN");
        } catch (ignore) {}
        try {
          webapis.avplay.setStreamingProperty("ADAPTIVE_INFO", "STARTBITRATE=AVERAGE");
        } catch (ignore) {}
        webapis.avplay.setListener({
          onbufferingstart: function () {
            if (gen !== generation) {
              return;
            }
            hooks.onBuffering(true);
          },
          onbufferingcomplete: function () {
            if (gen !== generation) {
              return;
            }
            hooks.onBuffering(false);
          },
          oncurrentplaytime: function (ms) {
            if (gen !== generation) {
              return;
            }
            var total = 0;
            try {
              total = webapis.avplay.getDuration() || 0;
            } catch (ignore) {}
            if (total > 0) {
              duration = seconds(total);
            }
            hooks.onTime(seconds(ms), duration);
          },
          onstreamcompleted: function () {
            if (gen !== generation) {
              return;
            }
            hooks.onEnded();
          },
          onerror: function (type) {
            if (gen !== generation) {
              return;
            }
            hooks.onError("AVPlay " + type);
          }
        });
        if (item.drm === "playready" && item.license) {
          webapis.avplay.setDrm("PLAYREADY", "SetProperties", JSON.stringify({
            DeleteLicenseAfterUse: false,
            GetChallenge: false,
            LicenseServer: item.license
          }));
        }
        webapis.avplay.prepareAsync(function () {
          if (gen !== generation) {
            return;
          }
          try {
            duration = seconds(webapis.avplay.getDuration());
          } catch (ignore) {}
          hooks.onReady(duration);
          try {
            webapis.avplay.play();
            hooks.onState("playing");
          } catch (err) {
            hooks.onError(errorText(err));
          }
        }, function (err) {
          if (gen !== generation) {
            return;
          }
          hooks.onError(errorText(err));
        });
      } catch (err) {
        hooks.onError(errorText(err));
      }
    },
    play: function () {
      try {
        var state = webapis.avplay.getState();
        if (state === "PAUSED" || state === "READY") {
          webapis.avplay.play();
          hooks.onState("playing");
        }
      } catch (err) {
        hooks.onError(errorText(err));
      }
    },
    pause: function () {
      try {
        if (webapis.avplay.getState() === "PLAYING") {
          webapis.avplay.pause();
          hooks.onState("paused");
        }
      } catch (err) {
        hooks.onError(errorText(err));
      }
    },
    stop: function () {
      generation += 1;
      reset();
    },
    seekTo: function (secondsValue) {
      var ms = Math.round(secondsValue * 1000);
      try {
        webapis.avplay.seekTo(ms);
        hooks.onTime(secondsValue, duration);
      } catch (err) {
        try {
          var current = webapis.avplay.getCurrentTime() || 0;
          var delta = ms - current;
          if (delta >= 0) {
            webapis.avplay.jumpForward(delta);
          } else {
            webapis.avplay.jumpBackward(-delta);
          }
          hooks.onTime(secondsValue, duration);
        } catch (err2) {
          hooks.onError(errorText(err2));
        }
      }
    }
  };
}
