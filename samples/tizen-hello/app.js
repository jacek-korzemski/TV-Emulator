(function () {
  var info = document.getElementById("info");
  var lines = [];

  try {
    lines.push("platform " + tizen.systeminfo.getCapability("http://tizen.org/feature/platform.version"));
  } catch (err) {
    lines.push("capability " + err.message);
  }

  try {
    lines.push(webapis.productinfo.getModel() + " / " + webapis.productinfo.getFirmware());
    lines.push("DUID " + webapis.productinfo.getDuid());
    lines.push(webapis.network.isConnectedToGateway() ? "sieć " + webapis.network.getIp() : "brak sieci");
  } catch (err2) {
    lines.push("webapis " + err2.message);
  }

  lines.push(navigator.userAgent);
  info.textContent = lines.join("\n");

  try {
    tizen.tvinputdevice.registerKey("ColorF0Red");
  } catch (err3) {}

  document.addEventListener("keydown", function (e) {
    document.getElementById("key").textContent = "keyCode " + e.keyCode;
  });
})();
