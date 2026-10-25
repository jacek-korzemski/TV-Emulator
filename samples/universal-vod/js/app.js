/* ES5. Remote screens for the universal VOD sample. */
(function () {
  var items = VodCatalog.items;
  var kind = detectBackend();
  var keys = readKeys();
  var screen = "home";
  var homeIndex = 0;
  var selected = items[0];
  var player = null;
  var hideTimer = 0;
  var lastKeyAt = 0;

  var MEDIA_PLAY = 415;
  var MEDIA_PAUSE = 19;
  var MEDIA_STOP = 413;
  var MEDIA_FF = 417;
  var MEDIA_RW = 412;
  var MEDIA_PLAY_PAUSE = 10252;

  function detectBackend() {
    var profile = window.__TV_EMULATOR_PROFILE__ || {};
    if (profile.platform === "tizen") {
      return "avplay";
    }
    if (window.webapis && webapis.avplay && typeof webapis.avplay.open === "function") {
      return "avplay";
    }
    return "shaka";
  }

  function readKeys() {
    var source = (window.__TV_EMULATOR_PROFILE__ && window.__TV_EMULATOR_PROFILE__.keys) || {};
    function pick(name, fallback) {
      return typeof source[name] === "number" ? source[name] : fallback;
    }
    return {
      back: pick("back", 461),
      enter: pick("enter", 13),
      left: pick("left", 37),
      right: pick("right", 39),
      up: pick("up", 38),
      down: pick("down", 40),
      red: pick("red", 403)
    };
  }

  function deviceLabel() {
    var profile = window.__TV_EMULATOR_PROFILE__ || {};
    var product = profile.product || {};
    var brand = product.brand || "";
    var model = product.model || "";
    var who = brand && model ? brand + " " + model : (model || brand || profile.platform || "Przeglądarka");
    return who + " · " + (kind === "avplay" ? "AVPlay" : "Shaka");
  }

  function mountSurface() {
    if (kind === "avplay") {
      return null;
    }
    var object = document.getElementById("av-player");
    if (object && object.parentNode) {
      object.parentNode.removeChild(object);
    }
    var video = document.createElement("video");
    video.id = "html-player";
    video.setAttribute("playsinline", "");
    var stage = document.getElementById("stage");
    stage.insertBefore(video, stage.firstChild);
    return video;
  }

  function showScreen(name) {
    screen = name;
    document.getElementById("home").classList.toggle("is-open", name === "home");
    document.getElementById("detail").classList.toggle("is-open", name === "detail");
    var osd = document.getElementById("osd");
    osd.classList.toggle("is-open", name === "player");
    if (name !== "player") {
      osd.classList.remove("is-hidden");
    }
  }

  function renderCards() {
    var row = document.getElementById("row");
    var i;
    for (i = 0; i < items.length; i++) {
      row.appendChild(buildCard(items[i], i));
    }
  }

  function buildCard(item, index) {
    var playable = VodCatalog.canPlay(item, kind);
    var card = document.createElement("div");
    card.className = "card";
    var poster = document.createElement("div");
    poster.className = "poster poster-" + item.id;
    var watermark = document.createElement("div");
    watermark.className = "watermark";
    watermark.textContent = item.mark;
    var badge = document.createElement("div");
    badge.className = "badge badge-" + item.drm;
    badge.textContent = item.badge;
    var title = document.createElement("div");
    title.className = "poster-title";
    title.textContent = item.title;
    poster.appendChild(watermark);
    poster.appendChild(badge);
    if (!playable) {
      var ribbon = document.createElement("div");
      ribbon.className = "unavailable is-on";
      ribbon.textContent = "Niedostępne";
      poster.appendChild(ribbon);
    }
    poster.appendChild(title);
    var body = document.createElement("div");
    body.className = "card-body";
    var meta = document.createElement("div");
    meta.className = "card-meta";
    meta.textContent = item.year + " · " + item.badge;
    body.appendChild(meta);
    card.appendChild(poster);
    card.appendChild(body);
    card.onclick = function () {
      homeIndex = index;
      paintFocus();
      openDetail(items[index]);
    };
    return card;
  }

  function paintFocus() {
    var cards = document.getElementById("row").children;
    var i;
    for (i = 0; i < cards.length; i++) {
      if (i === homeIndex) {
        cards[i].classList.add("is-focused");
      } else {
        cards[i].classList.remove("is-focused");
      }
    }
  }

  function openDetail(item) {
    selected = item;
    var playable = VodCatalog.canPlay(item, kind);
    document.getElementById("detail-poster").className = "detail-poster poster-" + item.id;
    document.getElementById("detail-watermark").textContent = item.mark;
    var badge = document.getElementById("detail-badge");
    badge.className = "badge badge-" + item.drm;
    badge.textContent = item.badge;
    var ribbon = document.getElementById("detail-ribbon");
    ribbon.textContent = playable ? "" : "Niedostępne";
    ribbon.className = playable ? "unavailable" : "unavailable is-on";
    document.getElementById("detail-kicker").textContent = item.badge;
    document.getElementById("detail-title").textContent = item.title;
    document.getElementById("detail-meta").textContent = item.year + " · DASH · " + (kind === "avplay" ? "AVPlay" : "Shaka");
    document.getElementById("detail-synopsis").textContent = item.synopsis;
    var play = document.getElementById("play");
    play.textContent = playable ? "Odtwórz" : "Niedostępne";
    play.className = playable ? "play is-focused" : "play is-focused is-blocked";
    document.getElementById("detail-note").textContent = VodCatalog.reason(item, kind);
    showScreen("detail");
  }

  function formatTime(seconds) {
    var value = Math.floor(Number(seconds) || 0);
    if (value < 0) {
      value = 0;
    }
    var hours = Math.floor(value / 3600);
    var minutes = Math.floor((value % 3600) / 60);
    var secs = value % 60;
    function pad(n) {
      return (n < 10 ? "0" : "") + n;
    }
    if (hours > 0) {
      return hours + ":" + pad(minutes) + ":" + pad(secs);
    }
    return minutes + ":" + pad(secs);
  }

  function renderTime(current, duration) {
    if (screen !== "player") {
      return;
    }
    document.getElementById("osd-time").textContent = formatTime(current) + " / " + formatTime(duration);
    var pct = duration > 0 ? (current / duration) * 100 : 0;
    if (pct < 0) {
      pct = 0;
    }
    if (pct > 100) {
      pct = 100;
    }
    document.getElementById("seek-fill").style.width = pct + "%";
  }

  function stateLabel(state) {
    if (state === "playing") {
      return "Odtwarzanie";
    }
    if (state === "paused") {
      return "Pauza";
    }
    if (state === "ended") {
      return "Koniec";
    }
    if (state === "error") {
      return "Błąd";
    }
    if (state === "idle") {
      return "Stop";
    }
    return "Buforowanie";
  }

  function showOsd() {
    var osd = document.getElementById("osd");
    osd.classList.remove("is-hidden");
    if (hideTimer) {
      clearTimeout(hideTimer);
      hideTimer = 0;
    }
    if (player && player.getState() === "playing") {
      hideTimer = setTimeout(function () {
        osd.classList.add("is-hidden");
        hideTimer = 0;
      }, 5000);
    }
  }

  function renderState() {
    if (screen !== "player") {
      return;
    }
    var state = player.getState();
    document.getElementById("osd-state").textContent = stateLabel(state);
    document.getElementById("osd-error").textContent = player.getError() || "";
    if (state !== "playing") {
      showOsd();
    } else if (!hideTimer) {
      showOsd();
    }
  }

  function startPlayer(item) {
    document.getElementById("osd-title").textContent = item.title;
    document.getElementById("osd-error").textContent = "";
    document.getElementById("osd-state").textContent = "Buforowanie";
    document.getElementById("osd-time").textContent = "0:00 / 0:00";
    document.getElementById("seek-fill").style.width = "0%";
    document.getElementById("osd").classList.remove("is-hidden");
    showScreen("player");
    player.load(item);
    showOsd();
  }

  function leavePlayer() {
    if (hideTimer) {
      clearTimeout(hideTimer);
      hideTimer = 0;
    }
    player.stop();
    showScreen("detail");
  }

  function activateDetail() {
    if (!selected || !VodCatalog.canPlay(selected, kind)) {
      return;
    }
    startPlayer(selected);
  }

  function exitApp() {
    try {
      if (window.tizen && tizen.application && tizen.application.getCurrentApplication) {
        tizen.application.getCurrentApplication().exit();
        return;
      }
    } catch (ignore) {}
    try {
      if (window.webOS && webOS.platformBack) {
        webOS.platformBack();
        return;
      }
    } catch (ignore) {}
    try {
      if (window.VIZIO && VIZIO.exitApplication) {
        VIZIO.exitApplication();
      }
    } catch (ignore) {}
  }

  function registerKeys() {
    if (!window.tizen || !tizen.tvinputdevice || !tizen.tvinputdevice.registerKey) {
      return;
    }
    var names = [
      "ColorF0Red",
      "MediaPlay",
      "MediaPause",
      "MediaStop",
      "MediaFastForward",
      "MediaRewind",
      "MediaPlayPause"
    ];
    var i;
    for (i = 0; i < names.length; i++) {
      try {
        tizen.tvinputdevice.registerKey(names[i]);
      } catch (ignore) {}
    }
  }

  function isBack(code) {
    return code === keys.back || code === 10009 || code === 461 || code === 8 || code === 27;
  }

  function gated() {
    var now = Date.now();
    if (now - lastKeyAt < 160) {
      return false;
    }
    lastKeyAt = now;
    return true;
  }

  function onHomeKey(code) {
    if (code === keys.left || code === 37) {
      if (homeIndex > 0) {
        homeIndex -= 1;
        paintFocus();
      }
      return;
    }
    if (code === keys.right || code === 39) {
      if (homeIndex < items.length - 1) {
        homeIndex += 1;
        paintFocus();
      }
      return;
    }
    if (code === keys.enter || code === 13) {
      openDetail(items[homeIndex]);
      return;
    }
    if (isBack(code)) {
      exitApp();
    }
  }

  function onDetailKey(code) {
    if (code === keys.enter || code === 13 || code === MEDIA_PLAY || code === MEDIA_PLAY_PAUSE) {
      activateDetail();
      return;
    }
    if (isBack(code)) {
      showScreen("home");
    }
  }

  function onPlayerKey(code) {
    if (code === keys.left || code === 37 || code === MEDIA_RW) {
      player.seekBy(-10);
      showOsd();
      return;
    }
    if (code === keys.right || code === 39 || code === MEDIA_FF) {
      player.seekBy(10);
      showOsd();
      return;
    }
    if (code === keys.enter || code === 13 || code === MEDIA_PLAY_PAUSE) {
      player.toggle();
      showOsd();
      return;
    }
    if (code === MEDIA_PLAY) {
      player.play();
      showOsd();
      return;
    }
    if (code === MEDIA_PAUSE) {
      player.pause();
      showOsd();
      return;
    }
    if (code === keys.up || code === 38 || code === keys.down || code === 40) {
      showOsd();
      return;
    }
    if (code === keys.red || code === 403 || code === MEDIA_STOP || isBack(code)) {
      leavePlayer();
    }
  }

  function onKey(event) {
    var code = event.keyCode || event.which;
    if (!gated()) {
      if (event.preventDefault) {
        event.preventDefault();
      }
      return;
    }
    if (screen === "player") {
      onPlayerKey(code);
    } else if (screen === "detail") {
      onDetailKey(code);
    } else {
      onHomeKey(code);
    }
    if (event.preventDefault) {
      event.preventDefault();
    }
  }

  var video = mountSurface();
  document.getElementById("device").textContent = deviceLabel();
  renderCards();
  paintFocus();
  player = createVodPlayer(kind, video, {
    onTime: renderTime,
    onState: renderState,
    onError: function (message) {
      if (screen !== "player") {
        return;
      }
      document.getElementById("osd-error").textContent = message || "";
      if (message) {
        showOsd();
      }
    }
  });
  document.getElementById("play").onclick = activateDetail;
  registerKeys();
  document.addEventListener("keydown", onKey);
})();
