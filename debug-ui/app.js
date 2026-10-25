(function () {
  var state = {
    tab: "console",
    status: null,
    console: [],
    network: [],
    log: [],
    memory: { engine: [], video: [] },
    gc: { engine: [], video: [] },
    heap: {},
    progress: {},
    heapSort: {
      retained: { key: "selfSize", dir: -1 },
      grew: { key: "sizeDelta", dir: -1 },
      freed: { key: "sizeDelta", dir: 1 }
    },
    levels: { error: true, warning: true, info: true, debug: false },
    netType: "all",
    netQuery: "",
    openNet: null,
    netDetail: null,
    openConsole: null,
    connected: false
  };

  var detailToken = 0;
  var stick = true;
  var socket = null;
  var retryMs = 1000;
  var types = [
    ["all", "Wszystkie"],
    ["xhr", "XHR"],
    ["document", "Dokument"],
    ["script", "JS"],
    ["stylesheet", "CSS"],
    ["image", "Obraz"],
    ["media", "Media"],
    ["font", "Font"],
    ["websocket", "WS"],
    ["other", "Inne"]
  ];

  document.getElementById("tabs").addEventListener("click", function (event) {
    var button = event.target.closest("button");
    if (!button) return;
    state.tab = button.getAttribute("data-tab");
    paintTabs();
    renderToolbar();
    renderList();
  });
  document.getElementById("toolbar").addEventListener("click", onToolbarClick);
  document.getElementById("toolbar").addEventListener("input", onToolbarInput);
  var pinning = false;
  document.getElementById("scroller").addEventListener("scroll", function () {
    if (!stick || pinning || !followsLive()) return;
    var node = document.getElementById("scroller");
    if (node.scrollHeight - node.scrollTop - node.clientHeight < 2) return;
    pinning = true;
    node.scrollTop = node.scrollHeight;
    pinning = false;
  });
  document.getElementById("scroller").addEventListener("click", onListClick);
  document.getElementById("pin").addEventListener("click", function () {
    stick = !stick;
    paintPin();
    if (!stick) return;
    var node = document.getElementById("scroller");
    pinning = true;
    node.scrollTop = node.scrollHeight;
    pinning = false;
  });
  document.getElementById("help-modal").addEventListener("click", function (event) {
    if (event.target.getAttribute("data-action") === "close-help") closeHelp();
  });
  document.addEventListener("keydown", function (event) {
    if (event.key === "Escape") closeHelp();
  });
  paintTabs();
  paintPin();
  renderToolbar();
  renderList();
  connect();

  function connect() {
    var proto = location.protocol === "https:" ? "wss://" : "ws://";
    socket = new WebSocket(proto + location.host + "/ws");
    socket.onopen = function () {
      state.connected = true;
      retryMs = 1000;
      updateStatus();
    };
    socket.onclose = function () {
      state.connected = false;
      updateStatus();
      window.setTimeout(connect, retryMs);
      retryMs = Math.min(5000, retryMs + 500);
    };
    socket.onmessage = function (event) {
      var message;
      try {
        message = JSON.parse(event.data);
      } catch (ignore) {
        return;
      }
      apply(message);
    };
  }

  function send(payload) {
    if (!socket || socket.readyState !== 1) return;
    socket.send(JSON.stringify(payload));
  }

  function apply(message) {
    if (message.type === "hello") {
      state.status = message.status || null;
      state.console = message.console || [];
      state.network = message.network || [];
      state.log = message.log || [];
      state.memory = message.memory || { engine: [], video: [] };
      state.gc = message.gc || { engine: [], video: [] };
      state.heap = message.heap || {};
      state.openNet = null;
      state.netDetail = null;
    } else if (message.type === "reset") {
      state.status = message.status || null;
      state.console = [];
      state.network = [];
      state.log = [];
      state.memory = { engine: [], video: [] };
      state.gc = { engine: [], video: [] };
      state.heap = {};
      state.progress = {};
      state.openNet = null;
      state.netDetail = null;
      state.openConsole = null;
    } else if (message.type === "status") {
      state.status = message.status || state.status;
    } else if (message.type === "console" && message.entry) {
      state.console.push(message.entry);
      if (state.console.length > 2000) state.console.shift();
    } else if (message.type === "network" && message.entry) {
      upsertNetwork(message.entry);
    } else if (message.type === "log") {
      state.log.push(message.entry || "");
      if (state.log.length > 2000) state.log.shift();
    } else if (message.type === "memory" && message.sample) {
      var sample = message.sample;
      var bucket = state.memory[sample.source] || [];
      bucket.push(sample);
      if (bucket.length > 90) bucket.shift();
      state.memory[sample.source] = bucket;
    } else if (message.type === "gc" && message.sample) {
      var tracked = state.gc[message.sample.source] || [];
      tracked.push(message.sample);
      if (tracked.length > 240) tracked.shift();
      state.gc[message.sample.source] = tracked;
    } else if (message.type === "heap" && message.report) {
      state.heap[message.report.source] = message.report;
      delete state.progress[message.report.source];
      if (state.progressTimer) {
        clearTimeout(state.progressTimer);
        state.progressTimer = null;
      }
    } else if (message.type === "heap-progress") {
      var percent = message.total ? Math.round(message.done * 100 / message.total) : 0;
      var previous = state.progress[message.source];
      var same = previous && !previous.error && previous.percent === percent;
      state.progress[message.source] = {
        done: message.done,
        total: message.total,
        finished: message.finished,
        percent: percent,
        since: same && previous.since ? previous.since : Date.now()
      };
      if (state.progressTimer) clearTimeout(state.progressTimer);
      state.progressTimer = setTimeout(function () {
        if (state.tab === "memory") renderList();
      }, 15000);
    } else if (message.type === "heap-error") {
      state.progress[message.source] = { error: message.message };
    } else if (message.type === "cleared") {
      if (message.channel === "console") state.console = [];
      if (message.channel === "network") {
        state.network = [];
        state.openNet = null;
        state.netDetail = null;
      }
      if (message.channel === "log") state.log = [];
    }

    updateStatus();
    if (message.type === "hello" || message.type === "reset" || affectsTab(message)) renderList();
  }

  function affectsTab(message) {
    if (message.type === "console" || message.type === "cleared" && message.channel === "console") return state.tab === "console";
    if (message.type === "network" || message.type === "cleared" && message.channel === "network") return state.tab === "network";
    if (message.type === "log" || message.type === "cleared" && message.channel === "log") return state.tab === "log";
    if (message.type === "memory" || message.type === "gc" || message.type === "heap" || message.type === "heap-progress" || message.type === "heap-error") return state.tab === "memory";
    return false;
  }

  function upsertNetwork(entry) {
    var found = false;
    var i;
    for (i = 0; i < state.network.length; i++) {
      if (state.network[i].id === entry.id) {
        state.network[i] = entry;
        found = true;
        break;
      }
    }
    if (!found) state.network.push(entry);
    if (state.network.length > 300) state.network.shift();
    if (state.openNet === entry.id) loadDetail(entry.id);
  }

  function loadDetail(id) {
    var token = ++detailToken;
    fetch("/api/network/" + id).then(function (response) {
      if (!response.ok) throw new Error("brak");
      return response.json();
    }).then(function (detail) {
      if (token !== detailToken || state.openNet !== id) return;
      state.netDetail = detail;
      if (state.tab === "network") renderList();
    }).catch(function () {});
  }

  function updateStatus() {
    var status = state.status || {};
    var title = document.getElementById("status-title");
    var meta = document.getElementById("status-meta");
    var conn = document.getElementById("status-conn");
    conn.textContent = state.connected ? "na żywo" : "łączenie…";
    conn.className = state.connected ? "live" : "";
    if (!status.active && !status.profile) {
      title.textContent = "Brak sesji";
      meta.textContent = "Uruchom aplikację w emulatorze. Ten adres: " + location.host;
      return;
    }
    if (!status.active) {
      title.textContent = "Sesja zakończona";
      meta.textContent = [status.profile, status.engine, status.logFile].filter(Boolean).join(" · ");
      return;
    }
    if (!status.diagnostics) {
      title.textContent = status.profile || "Sesja";
      meta.textContent = "Diagnostyka wyłączona. Włącz zapis diagnostyczny przed uruchomieniem, żeby dostać konsolę, sieć i pamięć.";
      return;
    }
    title.textContent = status.profile || "Sesja";
    var cdp = status.cdp || {};
    meta.textContent = (status.engine || "") + " · CDP strony: " + cdpLabel(cdp.engine) + " · CDP wideo: " + cdpLabel(cdp.video) + " · " + location.host;
  }

  function cdpLabel(value) {
    if (value === "open") return "podłączone";
    if (value === "connecting") return "czekam";
    return "wyłączone";
  }

  function paintTabs() {
    var buttons = document.querySelectorAll("#tabs button");
    var i;
    for (i = 0; i < buttons.length; i++) {
      if (buttons[i].getAttribute("data-tab") === state.tab) buttons[i].setAttribute("aria-current", "page");
      else buttons[i].removeAttribute("aria-current");
    }
  }

  function renderToolbar() {
    var html = "";
    if (state.tab === "console") {
      html += levelButton("error", "Błędy");
      html += levelButton("warning", "Ostrzeżenia");
      html += levelButton("info", "Info");
      html += levelButton("debug", "Debug");
      html += '<button type="button" data-action="clear" data-channel="console">Wyczyść</button>';
    } else if (state.tab === "network") {
      var i;
      for (i = 0; i < types.length; i++) {
        html += '<button type="button" data-action="type" data-type="' + types[i][0] + '" aria-pressed="' + (state.netType === types[i][0] ? "true" : "false") + '">' + types[i][1] + "</button>";
      }
      html += '<input id="net-query" type="search" placeholder="Filtr adresu" value="' + esc(state.netQuery) + '">';
      html += '<button type="button" data-action="clear" data-channel="network">Wyczyść</button>';
    } else if (state.tab === "memory") {
      html += '<button type="button" data-action="snapshot" data-target="engine">Zrzut sterty strony</button>';
      html += '<button type="button" data-action="snapshot" data-target="video">Zrzut sterty wideo</button>';
      html += '<button type="button" data-action="help" aria-label="Jak czytać pamięć">ⓘ</button>';
    } else {
      html += '<button type="button" data-action="clear" data-channel="log">Wyczyść</button>';
    }
    document.getElementById("toolbar").innerHTML = html;
  }

  function levelButton(level, label) {
    return '<button type="button" data-action="level" data-level="' + level + '" aria-pressed="' + (state.levels[level] ? "true" : "false") + '">' + label + "</button>";
  }

  function onToolbarClick(event) {
    var button = event.target.closest("button");
    if (!button) return;
    var action = button.getAttribute("data-action");
    if (action === "level") {
      var level = button.getAttribute("data-level");
      state.levels[level] = !state.levels[level];
      button.setAttribute("aria-pressed", state.levels[level] ? "true" : "false");
      renderList();
    } else if (action === "type") {
      state.netType = button.getAttribute("data-type");
      renderToolbar();
      renderList();
    } else if (action === "clear") {
      send({ action: "clear", channel: button.getAttribute("data-channel") });
    } else if (action === "snapshot") {
      var target = button.getAttribute("data-target");
      if (target === "engine" && usesOldGc(state.status && state.status.engine)) {
        var proceed = window.confirm("Pełny zrzut na Chromium starszym niż 62 potrafi zabić przeglądarkę. Śledzenie GC na żywo już działa bez zrzutu. Zrobić pełny zrzut mimo to?");
        if (!proceed) return;
      }
      send({ action: "snapshot", target: target });
    } else if (action === "help") {
      openHelp();
    }
  }

  function onToolbarInput(event) {
    if (event.target.id !== "net-query") return;
    state.netQuery = event.target.value;
    renderList();
  }

  function onListClick(event) {
    var button = event.target.closest("button");
    if (!button) return;
    if (button.getAttribute("data-console")) {
      var consoleId = Number(button.getAttribute("data-console"));
      state.openConsole = state.openConsole === consoleId ? null : consoleId;
      renderList();
    } else if (button.getAttribute("data-net")) {
      var netId = Number(button.getAttribute("data-net"));
      if (state.openNet === netId) {
        state.openNet = null;
        state.netDetail = null;
        renderList();
        return;
      }
      state.openNet = netId;
      state.netDetail = null;
      renderList();
      loadDetail(netId);
    } else if (button.getAttribute("data-sort")) {
      var tableId = button.getAttribute("data-table");
      var key = button.getAttribute("data-sort");
      var current = state.heapSort[tableId];
      if (current && current.key === key) current.dir = -current.dir;
      else state.heapSort[tableId] = { key: key, dir: key === "name" ? 1 : -1 };
      renderList();
    }
  }

  function renderList() {
    var scroller = document.getElementById("scroller");
    var previous = scroller.scrollTop;
    var html = "";
    if (state.tab === "console") html = renderConsole();
    else if (state.tab === "network") html = renderNetwork();
    else if (state.tab === "memory") html = renderMemory();
    else html = renderLog();
    scroller.innerHTML = html;
    if (state.tab === "memory") drawSparks();
    if (stick && followsLive()) scroller.scrollTop = scroller.scrollHeight;
    else scroller.scrollTop = previous;
  }

  function followsLive() {
    return state.tab === "console" || state.tab === "network" || state.tab === "log";
  }

  function paintPin() {
    var button = document.getElementById("pin");
    button.setAttribute("aria-pressed", stick ? "true" : "false");
    button.setAttribute("aria-label", stick
      ? "Przypięte do najnowszych. Kliknij, żeby zatrzymać."
      : "Przewijanie zatrzymane. Kliknij, żeby śledzić najnowsze wpisy.");
    setHidden(button.querySelector(".lock-closed"), !stick);
    setHidden(button.querySelector(".lock-open"), stick);
  }

  function setHidden(node, hidden) {
    if (hidden) node.setAttribute("hidden", "");
    else node.removeAttribute("hidden");
  }

  function renderConsole() {
    var rows = state.console.filter(function (entry) { return state.levels[entry.level]; });
    var groups = groupConsole(rows);
    var hidden = 0;
    if (groups.length > 500) {
      hidden = groups.length - 500;
      groups = groups.slice(groups.length - 500);
    }
    if (!groups.length) return '<p class="empty">Brak linii konsoli dla włączonych poziomów.</p>';
    var html = hidden ? '<p class="note">Starsze linie: ' + hidden + ". Pokazuję ostatnie 500.</p>" : "";
    groups.forEach(function (group) {
      if (group.ids.indexOf(state.openConsole) !== -1) state.openConsole = group.id;
      var open = state.openConsole === group.id;
      var expandable = group.stacks.length > 0;
      if (expandable) {
        html += '<button type="button" class="console-row can-open" data-console="' + group.id + '" aria-expanded="' + (open ? "true" : "false") + '">';
      } else {
        html += '<div class="console-row">';
      }
      html += expandable ? twist(open) : '<span class="twist" aria-hidden="true"></span>';
      html += '<div class="meta">' + esc(group.time) + " · " + esc(sourceLabel(group.source));
      if (group.places.length) html += " · " + esc(group.places.join(", "));
      html += "</div>";
      html += '<div class="text"><span class="badge level-' + esc(group.level) + '">' + esc(levelLabel(group.level)) + "</span>" + esc(group.text) + "</div>";
      html += expandable ? "</button>" : "</div>";
      if (open && group.stacks.length) html += '<pre class="stack">' + esc(group.stacks.join("\n\n")) + "</pre>";
    });
    return html;
  }

  function groupConsole(rows) {
    var groups = [];
    rows.forEach(function (entry) {
      var index;
      for (index = groups.length - 1; index >= 0; index--) {
        var group = groups[index];
        var gap = elapsed(group.time, entry.time);
        if (gap > 150) break;
        if (group.level !== entry.level || group.source !== entry.source || group.text !== entry.text) continue;
        if (!group.shim && !isShimUrl(entry.url)) continue;
        addPlace(group, entry);
        if (isShimUrl(entry.url)) group.shim = true;
        if (entry.stack && group.stacks.indexOf(entry.stack) === -1) group.stacks.push(entry.stack);
        group.ids.push(entry.id);
        return;
      }
      var created = {
        id: entry.id,
        ids: [entry.id],
        time: entry.time,
        source: entry.source,
        level: entry.level,
        text: entry.text,
        places: [],
        stacks: entry.stack ? [entry.stack] : [],
        shim: isShimUrl(entry.url)
      };
      addPlace(created, entry);
      groups.push(created);
    });
    return groups;
  }

  function addPlace(group, entry) {
    var place = formatPlace(entry);
    if (place && group.places.indexOf(place) === -1) group.places.push(place);
  }

  function formatPlace(entry) {
    if (!entry.url) return "";
    return shortUrl(entry.url) + (entry.line ? ":" + entry.line : "");
  }

  function isShimUrl(url) {
    return String(url || "").indexOf("/__emulator/platform.js") !== -1;
  }

  function elapsed(earlier, later) {
    var start = timeMs(earlier);
    var end = timeMs(later);
    if (start == null || end == null) return Infinity;
    var delta = end - start;
    if (delta < -12 * 60 * 60 * 1000) delta += 24 * 60 * 60 * 1000;
    return delta < 0 ? Infinity : delta;
  }

  function timeMs(value) {
    var match = /^(\d{2}):(\d{2}):(\d{2})\.(\d{1,3})$/.exec(value || "");
    if (!match) return null;
    var fraction = match[4];
    while (fraction.length < 3) fraction += "0";
    return ((Number(match[1]) * 60 + Number(match[2])) * 60 + Number(match[3])) * 1000 + Number(fraction);
  }

  function twist(open) {
    return '<span class="twist' + (open ? " open" : "") + '" aria-hidden="true"><svg viewBox="0 0 20 20" width="18" height="18"><path d="M7 4.5 L13 10 L7 15.5" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/></svg></span>';
  }

  function renderNetwork() {
    var query = state.netQuery.trim().toLowerCase();
    var rows = state.network.filter(function (entry) {
      if (state.netType !== "all" && entry.type !== state.netType) return false;
      if (query && String(entry.url || "").toLowerCase().indexOf(query) === -1) return false;
      return true;
    });
    if (!rows.length) return '<p class="empty">Brak żądań dla tego filtra.</p>';
    var html = "";
    rows.forEach(function (entry) {
      var code = entry.status ? String(entry.status) : "…";
      var openNet = state.openNet === entry.id;
      html += '<button type="button" class="net-row" data-net="' + entry.id + '" aria-expanded="' + (openNet ? "true" : "false") + '">';
      html += twist(openNet);
      html += '<span class="when">' + esc(entry.time || "") + "</span>";
      html += '<span class="method">' + esc(entry.method || "GET") + "</span>";
      html += '<span class="status ' + statusClass(entry.status) + '">' + esc(code) + "</span>";
      html += '<span class="url">' + esc(entry.url || "") + "</span>";
      html += '<span class="meta">' + esc(typeName(entry.type)) + (entry.rawType && entry.rawType.toLowerCase() !== entry.type ? " · " + esc(entry.rawType) : "");
      html += " · " + esc(sourceLabel(entry.source));
      if (entry.durationMs != null) html += " · " + esc(formatDuration(entry.durationMs));
      if (entry.encodedSize != null) html += " · " + esc(formatBytes(entry.encodedSize));
      if (entry.error) html += " · " + esc(entry.error);
      html += "</span></button>";
      if (state.openNet === entry.id) html += renderDetail(entry);
    });
    return html;
  }

  function renderDetail(entry) {
    var detail = state.netDetail && state.netDetail.id === entry.id ? state.netDetail : null;
    if (!detail) return '<div class="detail"><p class="muted">Wczytuję treść…</p></div>';
    var html = '<div class="detail">';
    html += "<h3>Nagłówki żądania</h3><pre>" + esc(detail.requestHeaders || "—") + "</pre>";
    html += "<h3>Payload</h3><pre>" + esc(bodyText(detail.requestBody, detail.hasRequestBody, detail)) + "</pre>";
    html += "<h3>Nagłówki odpowiedzi</h3><pre>" + esc(detail.responseHeaders || "—") + "</pre>";
    html += "<h3>Odpowiedź</h3><pre>" + esc(bodyText(detail.responseBody, detail.hasResponseBody, detail)) + "</pre>";
    html += "</div>";
    return html;
  }

  function bodyText(value, present, detail) {
    if (value) return value + (detail.bodyTruncated ? "\n… obcięte do 256 KB" : "");
    if (detail.bodySkipReason) return detail.bodySkipReason;
    if (present) return "Treść jest pusta.";
    return "Brak treści.";
  }

  function renderMemory() {
    var html = '<p class="note">Liczby to rozmiar własny obiektów, nie drzewo dominatorów z DevTools. Różnica dwóch zrzutów pokazuje, co zniknęło, a co zostało. Linia śledzonych obiektów pokazuje zbiórki na żywo: stary GC spada rzadko i stromo, nowszy częściej i łagodniej.</p>';
    if (usesOldGc(state.status && state.status.engine))
      html += '<p class="note">Ten silnik jest starszy niż Chromium 62. Śledzenie GC działa bez pełnego zrzutu. Pełny zrzut potrafi zabić przeglądarkę — sesja wtedy sama się zamyka i pasek zadań wraca.</p>';
    html += '<div class="memory-grid">';
    html += memoryCard("engine", "Strona");
    html += memoryCard("video", "Wideo");
    html += "</div>";
    html += heapBlock("engine", "Strona");
    html += heapBlock("video", "Wideo");
    return html;
  }

  function memoryCard(source, label) {
    var samples = state.memory[source] || [];
    var tracked = state.gc[source] || [];
    var last = samples.length ? samples[samples.length - 1] : null;
    var lastGc = tracked.length ? tracked[tracked.length - 1] : null;
    var html = '<section class="card"><h2>' + label + "</h2>";
    if (!last && !lastGc) {
      html += '<p class="muted">Brak odczytu sterty.</p></section>';
      return html;
    }
    if (last) {
      html += '<div class="figure">' + esc(formatBytes(last.used)) + "</div>";
      html += '<p class="muted">z ' + esc(formatBytes(last.total)) + " · węzły DOM: " + esc(last.nodes) + " · " + esc(last.time) + "</p>";
      html += '<canvas data-spark="' + source + '"></canvas>';
    } else {
      html += '<p class="muted">Brak odczytu performance.memory.</p>';
    }
    if (lastGc) {
      var previous = tracked.length > 1 ? tracked[tracked.length - 2] : null;
      var drop = previous && (lastGc.size < previous.size || lastGc.count < previous.count);
      html += '<p class="muted">Śledzone obiekty' + (drop ? " · zbiórka" : "") + "</p>";
      html += '<div class="figure">' + esc(formatBytes(lastGc.size)) + "</div>";
      html += '<p class="muted">' + esc(lastGc.count) + " sztuk · " + esc(lastGc.time) + "</p>";
      html += '<canvas data-gc="' + source + '"></canvas>';
    } else {
      html += '<p class="muted">Brak śledzenia GC.</p>';
    }
    html += "</section>";
    return html;
  }

  function heapBlock(source, label) {
    var progress = state.progress[source];
    var report = state.heap[source];
    var html = '<section class="card" style="margin-top:12px"><h2>Zrzut · ' + label + "</h2>";
    if (progress && progress.error) html += '<p class="level-error">' + esc(progress.error) + "</p>";
    else if (progress) {
      var percent = progress.percent != null ? progress.percent : (progress.total ? Math.round(progress.done * 100 / progress.total) : 0);
      var stalled = progress.since && Date.now() - progress.since >= 15000 && percent < 100;
      html += '<p class="muted">Zrzut trwa: ' + percent + "%." + (stalled ? " Duża sterta, silnik nadal po niej chodzi." : "") + "</p>";
    }
    if (!report) {
      html += '<p class="muted">Jeszcze nie było zrzutu.</p></section>';
      return html;
    }
    html += '<p class="muted">' + esc(report.at) + " · klas: " + esc(report.classes) + " · razem " + esc(formatBytes(report.totalSelf)) + "</p>";
    if (report.filePath) html += '<p class="muted">' + esc(report.filePath) + "</p>";
    if (!report.hasPrevious) html += '<p class="note">To pierwszy zrzut. Zrób drugi, żeby zobaczyć, co urosło i co zniknęło.</p>';
    html += '<div class="heap-columns cards three">';
    html += heapTable("Zostało", report.retained, false, "retained");
    html += heapTable("Urosło", report.grew, true, "grew");
    html += heapTable("Zniknęło", report.freed, true, "freed");
    html += "</div></section>";
    return html;
  }

  function heapTable(title, rows, delta, tableId) {
    var spec = state.heapSort[tableId];
    var html = "<div><h3>" + title + "</h3>";
    if (!rows || !rows.length) return html + '<p class="muted">Pusto.</p></div>';
    var sorted = rows.slice().sort(function (a, b) {
      return compareHeap(a, b, spec.key, spec.dir);
    });
    html += "<table><thead><tr>";
    html += sortHeader(tableId, "name", "Konstruktor", spec);
    html += sortHeader(tableId, "count", "Sztuk", spec);
    html += sortHeader(tableId, "selfSize", "Rozmiar", spec);
    if (delta) html += sortHeader(tableId, "sizeDelta", "Zmiana", spec);
    html += "</tr></thead><tbody>";
    sorted.forEach(function (row) {
      html += "<tr><td>" + esc(row.name) + "</td><td>" + esc(row.count);
      if (delta && row.countDelta) html += " (" + (row.countDelta > 0 ? "+" : "") + row.countDelta + ")";
      html += "</td><td>" + esc(formatBytes(row.selfSize)) + "</td>";
      if (delta) html += "<td>" + esc(signedBytes(row.sizeDelta)) + "</td>";
      html += "</tr>";
    });
    html += "</tbody></table></div>";
    return html;
  }

  function sortHeader(tableId, key, label, spec) {
    var active = spec.key === key;
    var arrow = active ? (spec.dir < 0 ? " ↓" : " ↑") : "";
    var aria = active ? (spec.dir < 0 ? "descending" : "ascending") : "none";
    return '<th><button type="button" data-sort="' + key + '" data-table="' + tableId + '" aria-sort="' + aria + '">' + label + arrow + "</button></th>";
  }

  function compareHeap(a, b, key, dir) {
    if (key === "name") return dir * String(a.name || "").localeCompare(String(b.name || ""), "pl");
    return dir * ((Number(a[key]) || 0) - (Number(b[key]) || 0));
  }

  function renderLog() {
    if (!state.log.length) return '<p class="empty">Brak linii logu.</p>';
    var rows = state.log;
    var hidden = 0;
    if (rows.length > 500) {
      hidden = rows.length - 500;
      rows = rows.slice(rows.length - 500);
    }
    var html = hidden ? '<p class="note">Starsze linie: ' + hidden + ". Pokazuję ostatnie 500.</p>" : "";
    rows.forEach(function (line) {
      html += '<div class="log-line">' + esc(line) + "</div>";
    });
    return html;
  }

  function drawSparks() {
    var canvases = document.querySelectorAll("canvas[data-spark]");
    var i;
    for (i = 0; i < canvases.length; i++) {
      drawSpark(canvases[i], state.memory[canvases[i].getAttribute("data-spark")] || [], "used");
    }
    var tracked = document.querySelectorAll("canvas[data-gc]");
    for (i = 0; i < tracked.length; i++) {
      drawSpark(tracked[i], state.gc[tracked[i].getAttribute("data-gc")] || [], "size");
    }
  }

  function drawSpark(canvas, samples, field) {
    var width = canvas.clientWidth || 280;
    var height = 64;
    var ratio = window.devicePixelRatio || 1;
    canvas.width = Math.floor(width * ratio);
    canvas.height = Math.floor(height * ratio);
    var context = canvas.getContext("2d");
    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    context.clearRect(0, 0, width, height);
    var values = [];
    samples.forEach(function (sample) {
      if (sample[field] != null) values.push(Number(sample[field]));
    });
    if (!values.length) return;
    var min = Math.min.apply(null, values);
    var max = Math.max.apply(null, values);
    if (max === min) max = min + 1;
    context.beginPath();
    context.strokeStyle = "#f2c14e";
    context.lineWidth = 2;
    values.forEach(function (value, index) {
      var x = values.length === 1 ? 2 : (index / (values.length - 1)) * (width - 4) + 2;
      var y = height - 4 - ((value - min) / (max - min)) * (height - 8);
      if (index === 0) context.moveTo(x, y);
      else context.lineTo(x, y);
    });
    context.stroke();
  }

  function openHelp() {
    var modal = document.getElementById("help-modal");
    modal.hidden = false;
    var button = modal.querySelector(".help-panel button");
    if (button) button.focus();
  }

  function closeHelp() {
    document.getElementById("help-modal").hidden = true;
  }

  function usesOldGc(engine) {
    var match = /^chromium-(\d+)$/.exec(engine || "");
    return !!match && Number(match[1]) < 62;
  }

  function sourceLabel(source) {
    if (source === "video") return "wideo";
    if (source === "shim") return "shim";
    return "silnik";
  }

  function levelLabel(level) {
    if (level === "error") return "błąd";
    if (level === "warning") return "ostrzeżenie";
    if (level === "debug") return "debug";
    return "info";
  }

  function typeName(type) {
    var i;
    for (i = 0; i < types.length; i++) {
      if (types[i][0] === type) return types[i][1];
    }
    return type || "Inne";
  }

  function statusClass(code) {
    if (!code) return "pending";
    if (code >= 400) return "bad";
    if (code >= 300) return "redirect";
    return "ok";
  }

  function shortUrl(url) {
    try {
      var parsed = new URL(url, location.href);
      return parsed.pathname + parsed.search;
    } catch (ignore) {
      return url;
    }
  }

  function formatDuration(ms) {
    if (ms < 1000) return ms + " ms";
    return (ms / 1000).toFixed(2) + " s";
  }

  function formatBytes(value) {
    if (value == null || value === "") return "—";
    var number = Number(value);
    if (!isFinite(number)) return "—";
    var sign = number < 0 ? "-" : "";
    number = Math.abs(number);
    if (number < 1024) return sign + Math.round(number) + " B";
    if (number < 1048576) return sign + (number / 1024).toFixed(1) + " KB";
    return sign + (number / 1048576).toFixed(1) + " MB";
  }

  function signedBytes(value) {
    if (!value) return "0 B";
    return (value > 0 ? "+" : "") + formatBytes(value);
  }

  function esc(value) {
    return String(value == null ? "" : value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }
})();
