/* ES5. One player surface for AVPlay and Shaka. Times are seconds. */
function createVodPlayer(kind, video, hooks) {
  var item = null;
  var state = "idle";
  var currentTime = 0;
  var duration = 0;
  var lastError = "";

  function emitState(next) {
    if (next === state) {
      return;
    }
    state = next;
    if (hooks.onState) {
      hooks.onState(state);
    }
  }

  var bridge = {
    onBuffering: function (active) {
      if (state === "idle" || state === "loading" || state === "error") {
        return;
      }
      if (active) {
        if (state === "playing") {
          emitState("buffering");
        }
        return;
      }
      if (state === "buffering") {
        emitState("playing");
      }
    },
    onTime: function (now, total) {
      if (state === "idle") {
        return;
      }
      currentTime = now || 0;
      if (total > 0) {
        duration = total;
      }
      if (hooks.onTime) {
        hooks.onTime(currentTime, duration);
      }
    },
    onReady: function (total) {
      if (total > 0) {
        duration = total;
      }
      if (hooks.onTime) {
        hooks.onTime(currentTime, duration);
      }
    },
    onState: function (next) {
      if (state === "idle") {
        return;
      }
      if (state === "loading" && (next === "paused" || next === "buffering")) {
        return;
      }
      emitState(next);
    },
    onEnded: function () {
      if (state === "idle" || state === "loading") {
        return;
      }
      emitState("ended");
    },
    onError: function (message) {
      lastError = message || "";
      if (!message) {
        if (hooks.onError) {
          hooks.onError("");
        }
        return;
      }
      state = "error";
      if (window.console && console.warn) {
        console.warn("vod " + message);
      }
      if (hooks.onError) {
        hooks.onError(message);
      }
      if (hooks.onState) {
        hooks.onState(state);
      }
    }
  };

  var backend = kind === "avplay"
    ? createAvplayBackend(bridge)
    : createShakaBackend(video, bridge);

  return {
    load: function (next) {
      item = next;
      state = "loading";
      lastError = "";
      currentTime = 0;
      duration = 0;
      if (hooks.onError) {
        hooks.onError("");
      }
      if (hooks.onState) {
        hooks.onState("loading");
      }
      if (hooks.onTime) {
        hooks.onTime(0, 0);
      }
      backend.load(next);
    },
    play: function () {
      backend.play();
    },
    pause: function () {
      backend.pause();
    },
    toggle: function () {
      if (state === "loading") {
        return;
      }
      if (state === "ended" || state === "error") {
        if (item) {
          this.load(item);
        }
        return;
      }
      if (state === "playing" || state === "buffering") {
        backend.pause();
        return;
      }
      backend.play();
    },
    stop: function () {
      backend.stop();
      state = "idle";
      lastError = "";
      currentTime = 0;
    },
    seekBy: function (delta) {
      if (state === "idle" || state === "loading" || state === "error") {
        return;
      }
      var target = currentTime + delta;
      if (target < 0) {
        target = 0;
      }
      if (duration > 0 && target > duration) {
        target = duration;
      }
      currentTime = target;
      backend.seekTo(target);
      if (hooks.onTime) {
        hooks.onTime(currentTime, duration);
      }
    },
    getCurrentTime: function () {
      return currentTime;
    },
    getDuration: function () {
      return duration;
    },
    getState: function () {
      return state;
    },
    getError: function () {
      return lastError;
    }
  };
}
