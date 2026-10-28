(function () {
  var info = document.getElementById("info");
  var lines = [navigator.userAgent];

  function call(name) {
    if (typeof window[name] !== "function") {
      return;
    }
    try {
      lines.push(name + " " + window[name]());
    } catch (err) {
      lines.push(name + " threw");
    }
  }

  call("Hisense_GetDeviceID");
  call("Hisense_GetModelName");
  call("Hisense_GetBrand");
  call("Hisense_GetFirmWareVersion");
  call("Hisense_GetChipSetName");
  call("Hisense_GetOSVersion");
  call("Hisense_GetApiVersion");
  call("Hisense_GetResolution");
  call("Hisense_GetSupportForHDR");
  call("Hisense_GetChromiumVersion");
  call("Hisense_GetMenuLanguageCode");
  if (window.VIZIO) {
    lines.push("VIZIO present");
  }
  if (window.DeviceInfo && window.DeviceInfo.Capability) {
    lines.push("DeviceInfo " + window.DeviceInfo.Capability.os + " " + window.DeviceInfo.Product.deviceID);
  }
  if (window.Panasonic) {
    lines.push("Panasonic " + window.Panasonic.model);
  }
  if (window.Sony) {
    lines.push("Sony " + window.Sony.model);
  }
  if (window.webOS) {
    lines.push("webOS");
  }
  if (window.webapis && window.webapis.avplay) {
    lines.push("webapis.avplay");
  }
  info.textContent = lines.join("\n");

  document.addEventListener("keydown", function (event) {
    document.getElementById("key").textContent = "keyCode " + event.keyCode;
  });
})();
