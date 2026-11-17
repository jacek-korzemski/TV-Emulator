/* ES5. Three public DASH demos: clear, PlayReady, Widevine. */
var VodCatalog = (function () {
  var items = [
    {
      id: "angel",
      mark: "01",
      title: "Angel One",
      year: "demo",
      badge: "Bez DRM",
      drm: "none",
      synopsis: "Krótka animacja w DASH, bez szyfrowania. Ten sam manifest odtwarza AVPlay i Shaka.",
      url: "https://storage.googleapis.com/shaka-demo-assets/angel-one/dash.mpd"
    },
    {
      id: "bbb",
      mark: "02",
      title: "Big Buck Bunny",
      year: "2008",
      badge: "PlayReady",
      drm: "playready",
      synopsis: "Fragment zaszyfrowany PlayReady na serwerze testowym Microsoftu. Odtwarzają go profile, które zgłaszają PlayReady.",
      url: "https://test.playready.microsoft.com/media/dash/APPLEENC_CBCS_BBB_1080p/1080p.mpd",
      license: "https://test.playready.microsoft.com/service/rightsmanager.asmx?cfg=(persist:false,contentkey:W31bfVt9W31bfVt9W31bfQ==,ckt:aes128bitcbc)"
    },
    {
      id: "sintel",
      mark: "03",
      title: "Sintel",
      year: "2010",
      badge: "Widevine",
      drm: "widevine",
      synopsis: "Sintel zaszyfrowany Widevine. Licencja demo nie wymaga tokenu. Odtwarzają go profile, które zgłaszają Widevine.",
      url: "https://storage.googleapis.com/shaka-demo-assets/sintel-widevine/dash.mpd",
      license: "https://cwip-shaka-proxy.appspot.com/no_auth"
    }
  ];

  function profileAllows(drm) {
    var profile = window.__TV_EMULATOR_PROFILE__ || {};
    var list = profile.drm;
    var i;
    if (!list) {
      return null;
    }
    for (i = 0; i < list.length; i++) {
      if (list[i] === drm) {
        return true;
      }
    }
    return false;
  }

  function canPlay(item, kind) {
    var allowed;
    if (!item) {
      return false;
    }
    if (item.drm === "none") {
      return true;
    }
    if (item.drm === "playready" || item.drm === "widevine") {
      if ((window.__TV_EMULATOR_PROFILE__ || {}).platform === "hbbtv") {
        return false;
      }
      allowed = profileAllows(item.drm);
      if (allowed !== null) {
        return allowed;
      }
      if (item.drm === "playready") {
        return kind === "avplay";
      }
      return kind === "shaka";
    }
    return false;
  }

  function reason(item, kind) {
    if (canPlay(item, kind)) {
      return "";
    }
    if ((window.__TV_EMULATOR_PROFILE__ || {}).platform === "hbbtv") {
      return "HbbTV w tym profilu otwiera strumień przez video/broadcast, nie przez EME.";
    }
    if (item && item.drm === "playready") {
      return "Ten profil nie zgłasza PlayReady.";
    }
    if (item && item.drm === "widevine") {
      return "Ten profil nie zgłasza Widevine.";
    }
    return "Tego zabezpieczenia nie odtworzy ten telewizor.";
  }

  return {
    items: items,
    canPlay: canPlay,
    reason: reason
  };
})();
