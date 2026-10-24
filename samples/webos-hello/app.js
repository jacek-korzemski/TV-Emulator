(function () {
  webOS.deviceInfo(function (info) {
    document.getElementById("info").textContent = info.modelName + "\nsdk " + info.sdkVersion + "\n" + navigator.userAgent;
  });

  webOS.fetchAppInfo(function (app) {
    document.getElementById("app").textContent = app.title + " (" + app.id + ")";
  });

  document.addEventListener("keydown", function (e) {
    document.getElementById("key").textContent = "keyCode " + e.keyCode;
    if (e.keyCode === 461) {
      webOS.platformBack();
    }
  });
})();
