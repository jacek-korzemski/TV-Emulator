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
      synopsis: "Fragment zaszyfrowany PlayReady na serwerze testowym Microsoftu. Odtwarza go AVPlay na profilach Tizen.",
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
      synopsis: "Sintel zaszyfrowany Widevine. Licencja demo Shaka Player nie wymaga tokenu. Odtwarza ją element video.",
      url: "https://storage.googleapis.com/shaka-demo-assets/sintel-widevine/dash.mpd",
      license: "https://cwip-shaka-proxy.appspot.com/no_auth"
    }
  ];

  function canPlay(item, kind) {
    if (!item) {
      return false;
    }
    if (item.drm === "none") {
      return true;
    }
    if (item.drm === "playready") {
      return kind === "avplay";
    }
    if (item.drm === "widevine") {
      return kind === "shaka";
    }
    return false;
  }

  function reason(item, kind) {
    if (canPlay(item, kind)) {
      return "";
    }
    if (item && item.drm === "playready") {
      return "PlayReady odtwarza AVPlay na Tizenie. Ten odbiornik używa Widevine.";
    }
    if (item && item.drm === "widevine") {
      return "Widevine odtwarza Shaka w elemencie video. Ten odbiornik używa PlayReady.";
    }
    return "Tego zabezpieczenia nie odtworzy ten telewizor.";
  }

  return {
    items: items,
    canPlay: canPlay,
    reason: reason
  };
})();
