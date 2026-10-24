/*
 * Video plane behind the Tizen page. It runs in WebView2 (Edge), not in the pinned Chromium,
 * because only Edge has H.264/AAC and PlayReady on Windows. The host relays webapis.avplay
 * commands here and forwards every message posted back to the page as an AVPlay event.
 *
 * Edge rejects a PlayReady license at MediaKeySession.update (2154840069 / 0x80704005) when
 * the session type does not match the license. recommendation enforces that: a persistent
 * license needs persistent-license, a non-persistent one needs temporary with
 * persistentState not-allowed. The license answer arrives only after prepare has already
 * resolved, so a rejected update reloads this stream with the next PlayReady profile.
 *
 * The library is Shaka 4.16 with codem-isoboxer loaded first. Shaka reads
 * window.ISOBoxer while transmuxing Smooth Streaming. That covers the MSS VOD
 * served to Tizen 4.0. Shaka rejects MSS live with MSS_LIVE_CONTENT_NOT_SUPPORTED.
 */
(() => {
  'use strict';

  const host = window.chrome && window.chrome.webview;
  let video = document.getElementById('plane');
  const shadeLayer = document.getElementById('shade-plane');
  const shadeNodes = new Map();
  let lastShadeCommand = null;
  const NetworkingEngine = shaka.net.NetworkingEngine;
  const RECOMMENDATION = 'com.microsoft.playready.recommendation';
  const SOFTWARE = 'com.microsoft.playready.software';
  const PLAYREADY = 'com.microsoft.playready';

  let player = null;
  let gen = 0;
  let url = '';
  let licenseServer = '';
  let customData = '';
  let prepared = false;
  let announcedPrepared = false;
  let wantPlaying = false;
  let resumeAfterRestore = false;
  let trackRefs = new Map();
  let subtitles = null;
  let lastTimeMs = -1;
  let loading = false;
  let queue = Promise.resolve();
  let activeToken = 0;
  let playerGeneration = 0;
  let scheduledGeneration = -1;
  let drmRetries = 0;
  let modeQueue = [];
  let modeIndex = 0;
  let soapRetried = false;
  let soapCustomData = false;
  let licensePersistence = 'unknown';
  let lastDrmError = null;
  let emePersistence = 'unchanged';
  let objectFit = 'fill';
  let playbackRate = 1;
  let failLoad = null;

  const emit = (type, data) => {
    host.postMessage(Object.assign({ type, gen }, data || {}));
  };

  window.addEventListener('error', (event) => {
    host.postMessage({ type: 'log', text: 'js-error ' + event.message + ' ' + event.filename + ':' + event.lineno });
  });
  window.addEventListener('unhandledrejection', (event) => {
    host.postMessage({ type: 'log', text: 'unhandledrejection ' + String(event.reason && event.reason.message || event.reason) });
  });

  const describe = (error) => {
    if (!error) {
      return '';
    }
    if (error instanceof shaka.util.Error) {
      const head = 'shaka ' + error.category + '/' + error.code;
      const data = error.data || [];
      const inner = data.find((item) => item instanceof shaka.util.Error);
      if (inner) {
        return head + ' <- ' + describe(inner);
      }
      if (error.code === shaka.util.Error.Code.MSS_LIVE_CONTENT_NOT_SUPPORTED) {
        return head + ' live MSS is not supported';
      }
      if (error.code === shaka.util.Error.Code.BAD_HTTP_STATUS) {
        const body = typeof data[2] === 'string' ? data[2] : '';
        const fault = /<faultstring>([\s\S]*?)<\/faultstring>/i.exec(body);
        return head + ' HTTP ' + data[1] + (fault ? ' ' + fault[1].trim() : '');
      }
      return head + ' ' + JSON.stringify(data).slice(0, 300);
    }
    return String(error.message || error);
  };

  const avplayError = (error) => {
    if (!(error instanceof shaka.util.Error)) {
      return 'PLAYER_ERROR_GENEREIC';
    }
    switch (error.category) {
      case shaka.util.Error.Category.NETWORK:
        return 'PLAYER_ERROR_CONNECTION_FAILED';
      case shaka.util.Error.Category.MANIFEST:
      case shaka.util.Error.Category.MEDIA:
        return 'PLAYER_ERROR_NOT_SUPPORTED_FILE';
      case shaka.util.Error.Category.DRM:
        return 'PLAYER_ERROR_DRM_NO_LICENSE';
      default:
        return 'PLAYER_ERROR_GENEREIC';
    }
  };

  const isRetryableDrm = (error) => {
    if (!(error instanceof shaka.util.Error)) {
      return false;
    }
    // 6008 is the PlayReady session-type mismatch. 6001/6002/6005 happen when Edge
    // refuses the persistentState we asked for, so the next profile still has to run.
    const code = error.code;
    const Code = shaka.util.Error.Code;
    if (error.category === shaka.util.Error.Category.DRM && (
      code === Code.LICENSE_RESPONSE_REJECTED ||
      code === Code.REQUESTED_KEY_SYSTEM_CONFIG_UNAVAILABLE ||
      code === Code.FAILED_TO_CREATE_CDM ||
      code === Code.FAILED_TO_CREATE_SESSION ||
      code === Code.FAILED_TO_GENERATE_LICENSE_REQUEST
    )) {
      return true;
    }
    return (error.data || []).some((item) => isRetryableDrm(item));
  };

  const withTimeout = (promise, ms) => new Promise((resolve, reject) => {
    const timer = setTimeout(() => resolve(), ms);
    promise.then((value) => {
      clearTimeout(timer);
      resolve(value);
    }, (error) => {
      clearTimeout(timer);
      reject(error);
    });
  });

  const raceCancel = async (token, work) => {
    let timer = 0;
    const cancelled = new Promise((resolve) => {
      timer = setInterval(() => {
        if (token !== activeToken) {
          resolve();
        }
      }, 40);
    });
    try {
      await Promise.race([work, cancelled]);
    } finally {
      clearInterval(timer);
    }
  };

  class CueForwarder {
    constructor() {
      this.cues = [];
      this.visible = false;
    }
    configure() {}
    append(cues) {
      for (const cue of cues) {
        this.cues.push(cue);
      }
    }
    remove(start, end) {
      this.cues = this.cues.filter((cue) => cue.endTime <= start || cue.startTime >= end);
      return true;
    }
    destroy() {
      this.cues = [];
      return Promise.resolve();
    }
    isTextVisible() {
      return this.visible;
    }
    setTextVisibility(on) {
      this.visible = on;
    }
    setTextLanguage() {}
    enableTextDisplayer() {}
    activeAt(seconds) {
      return this.cues.find((cue) => cue.startTime <= seconds && cue.endTime > seconds) || null;
    }
  }

  let lastCue = null;
  const forwardSubtitle = (element) => {
    if (!subtitles || !subtitles.visible) {
      return;
    }
    const cue = subtitles.activeAt(element.currentTime);
    if (!cue || cue === lastCue) {
      return;
    }
    lastCue = cue;
    const text = [cue.payload || ''].concat((cue.nestedCues || []).map((nested) => nested.payload || '')).filter(Boolean).join('\n');
    emit('subtitle', { durationMs: Math.round((cue.endTime - element.currentTime) * 1000), text });
  };

  const onTime = (event) => {
    const element = event.currentTarget;
    if (element !== video) {
      return;
    }
    if (mseMode) {
      emitMseState();
      return;
    }
    if (!prepared) {
      return;
    }
    const ms = Math.floor(element.currentTime * 1000);
    if (ms !== lastTimeMs) {
      lastTimeMs = ms;
      emit('time', { ms });
    }
    forwardSubtitle(element);
  };

  const onEnded = (event) => {
    if (event.currentTarget !== video) {
      return;
    }
    if (mseMode) {
      emit('mse-ended', mseSnapshot(null));
      return;
    }
    if (!prepared) {
      return;
    }
    emit('completed');
  };

  const bindVideo = (element) => {
    if (element.dataset.tvBound === '1') {
      return;
    }
    element.dataset.tvBound = '1';
    element.addEventListener('timeupdate', onTime);
    element.addEventListener('ended', onEnded);
  };

  const replaceVideoElement = (old) => {
    const next = document.createElement('video');
    next.id = 'plane';
    next.playsInline = true;
    next.autoplay = false;
    next.style.objectFit = objectFit;
    next.playbackRate = playbackRate;
    old.replaceWith(next);
    bindVideo(next);
    return next;
  };

  const fourCC = (codec) => {
    const lower = String(codec || '').toLowerCase();
    if (lower.startsWith('avc')) return 'H264';
    if (lower.startsWith('hvc') || lower.startsWith('hev')) return 'HEVC';
    if (lower.startsWith('mp4a')) return 'AACL';
    if (lower.startsWith('ec-3')) return 'EAC3';
    if (lower.startsWith('ac-3')) return 'AC3';
    return lower.toUpperCase();
  };

  const trackInfo = () => {
    const tracks = [];
    if (!player) {
      return { tracks, current: [] };
    }
    const current = [];
    trackRefs = new Map();
    let index = 0;

    const variants = player.getVariantTracks();
    const active = variants.find((track) => track.active) || variants[0];
    if (active) {
      const entry = {
        index: index++,
        type: 'VIDEO',
        extra_info: JSON.stringify({
          fourCC: fourCC(active.videoCodec),
          Width: String(active.width || 0),
          Height: String(active.height || 0),
          Bit_rate: String(active.videoBandwidth || active.bandwidth || 0)
        })
      };
      tracks.push(entry);
      current.push(entry);
    }

    for (const audio of player.getAudioTracks()) {
      const entry = {
        index: index++,
        type: 'AUDIO',
        extra_info: JSON.stringify({
          language: audio.language || '',
          channels: String(audio.channelsCount || 2),
          sample_rate: String(audio.audioSamplingRate || 48000),
          bit_rate: String(audio.bandwidth || 0),
          fourCC: fourCC(audio.codecs)
        })
      };
      trackRefs.set(entry.index, { kind: 'audio', ref: audio });
      tracks.push(entry);
      if (audio.active) {
        current.push(entry);
      }
    }

    let textNumber = 0;
    for (const text of player.getTextTracks()) {
      const entry = {
        index: index++,
        type: 'TEXT',
        extra_info: JSON.stringify({
          track_num: String(textNumber++),
          track_lang: text.language || '',
          subtitle_type: '-1',
          fourCC: String(text.mimeType || '').indexOf('vtt') !== -1 ? 'WEBVTT' : 'TTML'
        })
      };
      trackRefs.set(entry.index, { kind: 'text', ref: text });
      tracks.push(entry);
      if (text.active) {
        current.push(entry);
      }
    }

    return { tracks, current };
  };

  const timing = () => {
    const live = player.isLive();
    const range = player.seekRange();
    const durationMs = live ? Math.round(range.end * 1000) : Math.round((video.duration || 0) * 1000);
    return {
      live,
      durationMs: Number.isFinite(durationMs) ? durationMs : 0,
      liveStartMs: Math.round(range.start * 1000),
      liveEndMs: Math.round(range.end * 1000)
    };
  };

  const toBytes = (data) => {
    if (!data) {
      return null;
    }
    if (data instanceof ArrayBuffer) {
      return new Uint8Array(data);
    }
    if (ArrayBuffer.isView(data)) {
      return new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
    }
    return null;
  };

  const indexOfBytes = (haystack, needle) => {
    if (!needle.length || haystack.length < needle.length) {
      return -1;
    }
    for (let i = 0; i <= haystack.length - needle.length; i++) {
      let found = true;
      for (let j = 0; j < needle.length; j++) {
        if (haystack[i + j] !== needle[j]) {
          found = false;
          break;
        }
      }
      if (found) {
        return i;
      }
    }
    return -1;
  };

  const asciiBytes = (word, wide) => {
    const bytes = [];
    for (let i = 0; i < word.length; i++) {
      bytes.push(word.charCodeAt(i));
      if (wide) {
        bytes.push(0);
      }
    }
    return bytes;
  };

  // "Persistent" is a suffix of "NonPersistent", so the non-persistent word has to win.
  const readPersistence = (data) => {
    const bytes = toBytes(data);
    if (!bytes) {
      return 'unknown';
    }
    const nonPersistent = [asciiBytes('NonPersistent', false), asciiBytes('NonPersistent', true)];
    const persistent = [asciiBytes('Persistent', false), asciiBytes('Persistent', true)];
    if (nonPersistent.some((needle) => indexOfBytes(bytes, needle) !== -1)) {
      return 'NonPersistent';
    }
    if (persistent.some((needle) => indexOfBytes(bytes, needle) !== -1)) {
      return 'Persistent';
    }
    return 'unknown';
  };

  const decodeBody = (data) => {
    const bytes = toBytes(data);
    if (!bytes || !bytes.length) {
      return null;
    }
    let encoding = 'utf-8';
    let bom = false;
    if (bytes.length >= 2 && bytes[0] === 0xFF && bytes[1] === 0xFE) {
      encoding = 'utf-16le';
      bom = true;
    } else if (bytes.length >= 4 && bytes[1] === 0 && bytes[3] === 0) {
      encoding = 'utf-16le';
    }
    const text = new TextDecoder(encoding).decode(bom ? bytes.subarray(2) : bytes);
    return { text, encoding, bom };
  };

  const encodeBody = (text, decoded) => {
    if (decoded.encoding === 'utf-16le') {
      const bom = decoded.bom ? 2 : 0;
      const out = new Uint8Array(bom + text.length * 2);
      if (decoded.bom) {
        out[0] = 0xFF;
        out[1] = 0xFE;
      }
      for (let i = 0; i < text.length; i++) {
        const code = text.charCodeAt(i);
        out[bom + i * 2] = code & 0xFF;
        out[bom + i * 2 + 1] = code >> 8;
      }
      return out;
    }
    return new TextEncoder().encode(text);
  };

  // Some Tizen 4 MSS manifests write video chunk durations as an unsigned
  // 32-bit value plus 2^32 (for example 4314967296 instead of 20000000 ticks).
  // AVPlay truncates these values to uint32. Shaka keeps the full number,
  // considers the first video segment hundreds of seconds long and fetches
  // only audio after the first two seconds. Match AVPlay before Shaka parses
  // the manifest, without changing the presentation duration or correctly
  // encoded audio chunks.
  const normalizeMssVideoDurations = (response) => {
    const decoded = decodeBody(response.data);
    if (!decoded || decoded.text.indexOf('<SmoothStreamingMedia') === -1) {
      return 0;
    }
    let count = 0;
    const limit = 0x100000000;
    const text = decoded.text.replace(
      /<StreamIndex\b(?=[^>]*\bType\s*=\s*["']video["'])[^>]*>[\s\S]*?<\/StreamIndex>/gi,
      (stream) => stream.replace(/(\bd\s*=\s*)(["'])(\d+)\2/gi, (attribute, prefix, quote, raw) => {
        const value = Number(raw);
        if (!Number.isSafeInteger(value) || value < limit) {
          return attribute;
        }
        count += 1;
        return prefix + quote + (value % limit) + quote;
      })
    );
    if (count > 0) {
      response.data = encodeBody(text, decoded);
    }
    return count;
  };

  const escapeXml = (value) => String(value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');

  // CustomData stays outside <LA Id="SignedData">. The CDM signature covers that node only.
  const injectSoapCustomData = (request) => {
    const decoded = decodeBody(request.body);
    if (!decoded || /<CustomData[\s>]/i.test(decoded.text)) {
      return;
    }
    const payload = '<CustomData>' + escapeXml(customData) + '</CustomData>';
    let text = decoded.text;
    // Case-sensitive: the inner CDM node is </Challenge> and it sits inside the signed tree.
    // Only the SOAP wrapper </challenge> / </AcquireLicense> is a safe place to add the token.
    if (text.indexOf('</challenge>') !== -1) {
      text = text.replace('</challenge>', '</challenge>' + payload);
    } else if (text.indexOf('</AcquireLicense>') !== -1) {
      text = text.replace('</AcquireLicense>', payload + '</AcquireLicense>');
    } else {
      return;
    }
    request.body = encodeBody(text, decoded);
  };

  const playReadyConfig = (config) => {
    const next = Object.assign({}, config);
    if (emePersistence === 'persistent') {
      next.persistentState = 'required';
      next.sessionTypes = ['persistent-license'];
    } else {
      next.persistentState = 'not-allowed';
      next.sessionTypes = ['temporary'];
    }
    return next;
  };

  const installPlayReadyAccess = () => {
    const requestAccess = navigator.requestMediaKeySystemAccess.bind(navigator);
    navigator.requestMediaKeySystemAccess = (keySystem, configs) => {
      if (keySystem === RECOMMENDATION && emePersistence !== 'unchanged' && Array.isArray(configs)) {
        configs = configs.map(playReadyConfig);
      }
      return requestAccess(keySystem, configs);
    };

    // Edge's decodingInfo asks for key-system access inside the browser, so wrapping
    // requestMediaKeySystemAccess alone does not change the session it grants.
    if (navigator.mediaCapabilities && navigator.mediaCapabilities.decodingInfo) {
      try {
        const decodingInfo = navigator.mediaCapabilities.decodingInfo.bind(navigator.mediaCapabilities);
        navigator.mediaCapabilities.decodingInfo = (config) => {
          const keySystem = config && config.keySystemConfiguration;
          if (keySystem && keySystem.keySystem === RECOMMENDATION && emePersistence !== 'unchanged') {
            config = Object.assign({}, config, { keySystemConfiguration: playReadyConfig(keySystem) });
          }
          return decodingInfo(config);
        };
      } catch (ignore) {}
    }
  };

  const initialModes = () => {
    if (/persist\s*:\s*false/i.test(licenseServer)) {
      return ['recommendation-temporary', 'software-temporary', 'legacy'];
    }
    // Hardware PlayReady (recommendation) draws into a protected overlay window.
    // That window stays hidden while the color-keyed Chromium window covers the
    // plane, so the page shows a white video area even though the clock moves.
    // Software PlayReady paints ordinary frames into this page, which the color
    // key can show. Hardware remains the fallback if the license refuses it.
    return ['software-temporary', 'recommendation-persistent', 'recommendation-temporary', 'legacy'];
  };

  const armModes = () => {
    modeQueue = initialModes();
    modeIndex = 0;
    soapRetried = false;
    soapCustomData = false;
    licensePersistence = 'unknown';
    lastDrmError = null;
    drmRetries = 0;
    scheduledGeneration = -1;
  };

  const advanceMode = () => {
    if (licensePersistence === 'NonPersistent') {
      const preferred = modeQueue.indexOf('recommendation-temporary');
      if (preferred > modeIndex) {
        modeIndex = preferred;
        return true;
      }
    } else if (licensePersistence === 'Persistent') {
      const software = modeQueue.indexOf('software-persistent');
      if (software > modeIndex) {
        modeIndex = software;
        return true;
      }
      const preferred = modeQueue.indexOf('recommendation-persistent');
      if (preferred > modeIndex) {
        modeIndex = preferred;
        return true;
      }
    }
    if (modeIndex < modeQueue.length - 1) {
      modeIndex += 1;
      return true;
    }
    if (!soapRetried && customData) {
      soapRetried = true;
      soapCustomData = true;
      modeQueue.push(licensePersistence === 'NonPersistent' ? 'recommendation-temporary' : 'recommendation-persistent');
      modeIndex = modeQueue.length - 1;
      return true;
    }
    return false;
  };

  const applyMode = (mode) => {
    if (mode === 'recommendation-temporary' || mode === 'software-temporary') {
      emePersistence = 'temporary';
    } else if (mode === 'recommendation-persistent' || mode === 'software-persistent') {
      emePersistence = 'persistent';
    } else {
      emePersistence = 'unchanged';
    }
  };

  const drmConfig = (mode) => {
    const servers = licenseServer ? {
      [PLAYREADY]: licenseServer,
      [RECOMMENDATION]: licenseServer,
      [SOFTWARE]: licenseServer
    } : {};
    if (mode === 'legacy') {
      return {
        servers,
        keySystemsMapping: {},
        preferredKeySystems: [PLAYREADY],
        advanced: {}
      };
    }
    if (mode === 'software-persistent' || mode === 'software-temporary') {
      const advanced = mode === 'software-persistent'
        ? { persistentStateRequired: true, sessionType: 'persistent-license', videoRobustness: ['150'], audioRobustness: ['150'] }
        : { persistentStateRequired: false, sessionType: 'temporary', videoRobustness: ['150'], audioRobustness: ['150'] };
      return {
        servers,
        keySystemsMapping: { [PLAYREADY]: SOFTWARE, [RECOMMENDATION]: SOFTWARE },
        preferredKeySystems: [SOFTWARE],
        advanced: {
          [PLAYREADY]: advanced,
          [SOFTWARE]: advanced,
          [RECOMMENDATION]: advanced
        }
      };
    }
    const persistent = mode === 'recommendation-persistent';
    const advanced = persistent
      ? { persistentStateRequired: true, sessionType: 'persistent-license' }
      : { persistentStateRequired: false, sessionType: 'temporary' };
    return {
      servers,
      keySystemsMapping: { [PLAYREADY]: RECOMMENDATION },
      preferredKeySystems: [RECOMMENDATION],
      advanced: {
        [PLAYREADY]: advanced,
        [RECOMMENDATION]: advanced
      }
    };
  };

  const reportFinalDrm = (error) => {
    const message = describe(error);
    emit('drm', { drmType: 'PLAYREADY', data: { name: 'DrmError', message } });
    if (announcedPrepared) {
      emit('error', { code: 'PLAYER_ERROR_DRM_NO_LICENSE', message });
    } else {
      emit('prepareError', { code: 'PLAYER_ERROR_DRM_NO_LICENSE', message });
    }
  };

  const scheduleDrmRetry = (generation, token, error) => {
    if (scheduledGeneration === generation) {
      return;
    }
    scheduledGeneration = generation;
    lastDrmError = error || lastDrmError;
    queue = queue.then(() => raceCancel(token, retryAfterDrm(generation, token, error)));
  };

  const retryAfterDrm = async (generation, token, error) => {
    if (generation !== playerGeneration || token !== activeToken) {
      return;
    }
    drmRetries += 1;
    if (drmRetries > 6 || !advanceMode()) {
      reportFinalDrm(error || lastDrmError);
      return;
    }
    await attemptLoad(token);
  };

  const startPlayback = () => {
    const played = video.play();
    if (played && typeof played.catch === 'function') {
      played.catch((error) => {
        if (!wantPlaying || (error && error.name === 'AbortError')) {
          return;
        }
        emit('error', { code: 'PLAYER_ERROR_INVALID_OPERATION', message: describe(error) });
      });
    }
  };

  const destroyPlayer = async () => {
    playerGeneration += 1;
    if (failLoad) {
      const reject = failLoad;
      failLoad = null;
      reject(Object.assign(new Error('cancelled'), { name: 'AbortError' }));
    }
    const current = player;
    player = null;
    prepared = false;
    loading = false;
    lastTimeMs = -1;
    lastCue = null;
    try {
      video.pause();
    } catch (ignore) {}
    video = replaceVideoElement(video);
    if (!current) {
      return;
    }
    await withTimeout(current.destroy().catch(() => {}), 1500);
  };

  const wire = (instance, token) => {
    const generation = playerGeneration;
    instance.getNetworkingEngine().registerRequestFilter((type, request) => {
      if (type !== NetworkingEngine.RequestType.LICENSE) {
        return;
      }
      if (customData) {
        request.headers['X-CustomData'] = customData;
      }
      if (soapCustomData && customData && request.body) {
        injectSoapCustomData(request);
      }
    });
    instance.getNetworkingEngine().registerResponseFilter((type, response) => {
      if (type === NetworkingEngine.RequestType.MANIFEST) {
        const corrected = normalizeMssVideoDurations(response);
        if (corrected > 0) {
          emit('log', { text: 'mss corrected wrapped video durations=' + corrected });
        }
        return;
      }
      if (type !== NetworkingEngine.RequestType.LICENSE) {
        return;
      }
      const persistence = readPersistence(response.data);
      if (persistence !== 'unknown') {
        licensePersistence = persistence;
      }
      emit('drm', {
        drmType: 'PLAYREADY',
        data: { name: 'LicenseResponse', status: response.status || 200, persistence }
      });
    });
    instance.addEventListener('error', (event) => {
      if (generation !== playerGeneration) {
        return;
      }
      const error = event.detail;
      if (isRetryableDrm(error)) {
        lastDrmError = error;
        scheduleDrmRetry(generation, token, error);
        if (failLoad) {
          const reject = failLoad;
          failLoad = null;
          reject(error);
        }
        return;
      }
      if (error && error.category === shaka.util.Error.Category.DRM) {
        emit('drm', { drmType: 'PLAYREADY', data: { name: 'DrmError', message: describe(error) } });
      }
      if (prepared || announcedPrepared) {
        emit('error', { code: avplayError(error), message: describe(error) });
      }
    });
    instance.addEventListener('buffering', (event) => {
      if (generation === playerGeneration && prepared) {
        emit('buffering', { state: event.buffering ? 'start' : 'complete' });
      }
    });
    instance.addEventListener('adaptation', () => {
      if (generation !== playerGeneration || !prepared) {
        return;
      }
      emit('tracks', trackInfo());
      emit('event', { name: 'PLAYER_MSG_RESOLUTION_CHANGED', data: '' });
    });
    instance.addEventListener('trackschanged', () => {
      if (generation === playerGeneration && prepared) {
        emit('tracks', trackInfo());
      }
    });
  };

  const ensurePlayer = async (token) => {
    if (player) {
      return player;
    }
    player = new shaka.Player();
    await player.attach(video);
    wire(player, token);
    return player;
  };

  const attemptLoad = async (token) => {
    const mode = modeQueue[modeIndex];
    if (!mode || token !== activeToken) {
      return;
    }
    applyMode(mode);
    emit('log', { text: 'playready ' + mode + (soapCustomData ? ' soap' : '') });
    await destroyPlayer();
    if (token !== activeToken) {
      return;
    }
    try {
      const instance = await ensurePlayer(token);
      subtitles = new CueForwarder();
      instance.configure({
        streaming: { clearDecodingCache: true },
        textDisplayFactory: () => subtitles,
        drm: drmConfig(mode)
      });
      video.autoplay = false;
      const element = video;
      loading = true;
      await new Promise((resolve, reject) => {
        failLoad = reject;
        instance.load(url).then((value) => {
          if (failLoad) {
            failLoad = null;
            resolve(value);
          }
        }, (loadError) => {
          if (failLoad) {
            failLoad = null;
            reject(loadError);
          }
        });
      });
      loading = false;
      if (token !== activeToken || element !== video) {
        return;
      }
      video.pause();
      prepared = true;
      if (!announcedPrepared) {
        announcedPrepared = true;
        emit('prepared', Object.assign(timing(), trackInfo()));
      }
      if (wantPlaying) {
        startPlayback();
      }
    } catch (error) {
      loading = false;
      if (token !== activeToken || (error && error.name === 'AbortError')) {
        return;
      }
      if (isRetryableDrm(error) || isRetryableDrm(lastDrmError)) {
        scheduleDrmRetry(playerGeneration, token, isRetryableDrm(error) ? error : lastDrmError);
        return;
      }
      if (!announcedPrepared) {
        emit('prepareError', { code: avplayError(error), message: describe(error) });
      } else {
        emit('error', { code: avplayError(error), message: describe(error) });
      }
    }
  };

  const parseDrm = (data) => {
    if (typeof data === 'string') {
      try {
        return JSON.parse(data);
      } catch (ignore) {
        return {};
      }
    }
    return data || {};
  };

  const clearShades = () => {
    for (const node of shadeNodes.values()) {
      node.remove();
    }
    shadeNodes.clear();
    lastShadeCommand = null;
  };

  const paintShade = (node, item) => {
    node.style.left = (Number(item.x) || 0) + 'px';
    node.style.top = (Number(item.y) || 0) + 'px';
    node.style.width = Math.max(0, Number(item.w) || 0) + 'px';
    node.style.height = Math.max(0, Number(item.h) || 0) + 'px';
    node.style.backgroundColor = item.color || 'transparent';
    node.style.backgroundImage = item.image && item.image !== 'none' ? item.image : 'none';
    node.style.borderRadius = item.radius || '0';
    node.style.opacity = String(item.opacity == null ? 1 : Math.max(0, Math.min(1, Number(item.opacity) || 0)));
  };

  const syncShades = (command) => {
    lastShadeCommand = command;
    const viewport = command.viewport || {};
    const sourceWidth = Math.max(1, Number(viewport.w) || window.innerWidth);
    const sourceHeight = Math.max(1, Number(viewport.h) || window.innerHeight);
    shadeLayer.style.width = sourceWidth + 'px';
    shadeLayer.style.height = sourceHeight + 'px';
    shadeLayer.style.transform = 'scale(' + (window.innerWidth / sourceWidth) + ',' + (window.innerHeight / sourceHeight) + ')';

    const live = new Set();
    for (const item of Array.isArray(command.items) ? command.items : []) {
      const id = String(item.id || '');
      if (!id) {
        continue;
      }
      live.add(id);
      let node = shadeNodes.get(id);
      if (!node) {
        node = document.createElement('div');
        node.dataset.shadeId = id;
        shadeNodes.set(id, node);
        shadeLayer.appendChild(node);
      }
      paintShade(node, item);
    }
    for (const [id, node] of shadeNodes) {
      if (!live.has(id)) {
        node.remove();
        shadeNodes.delete(id);
      }
    }
  };

  window.addEventListener('resize', () => {
    if (lastShadeCommand) {
      syncShades(lastShadeCommand);
    }
  });

  // webOS keeps Shaka and its Widevine license RPC in the old Chromium. This
  // plane only supplies the CDM and the decoder. PlayReady mapping stays on the
  // Tizen path and is not used here.
  let mseMode = false;
  let mseSource = null;
  let mseKeys = null;
  const mseBuffers = new Map();
  const mseSessions = new Map();
  let pendingEme = [];
  let pendingMseTime = null;

  const bytesFromBase64 = (text) => {
    const binary = atob(text || '');
    const out = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
      out[i] = binary.charCodeAt(i);
    }
    return out;
  };

  const base64FromBytes = (buffer) => {
    const bytes = new Uint8Array(buffer);
    let binary = '';
    const step = 0x8000;
    for (let i = 0; i < bytes.length; i += step) {
      binary += String.fromCharCode.apply(null, bytes.subarray(i, i + step));
    }
    return btoa(binary);
  };

  const rangeList = (ranges) => {
    const out = [];
    try {
      for (let i = 0; i < ranges.length; i++) {
        out.push([ranges.start(i), ranges.end(i)]);
      }
    } catch (ignore) {}
    return out;
  };

  const mseSnapshot = (buffer) => ({
    time: video.currentTime || 0,
    duration: Number.isFinite(video.duration) ? video.duration : 0,
    paused: video.paused,
    ready: video.readyState || 0,
    width: video.videoWidth || 0,
    height: video.videoHeight || 0,
    ranges: buffer ? rangeList(buffer.buffered) : [],
    videoRanges: rangeList(video.buffered)
  });

  const emitMseState = () => {
    if (mseMode) {
      emit('mse-state', { state: mseSnapshot(null) });
    }
  };

  const waitBuffer = (buffer, work) => new Promise((resolve, reject) => {
    let settled = false;
    const finish = (error) => {
      if (settled) {
        return;
      }
      settled = true;
      buffer.removeEventListener('updateend', onEnd);
      buffer.removeEventListener('error', onError);
      if (error) {
        reject(error);
      } else {
        resolve();
      }
    };
    const onEnd = () => finish();
    const onError = () => finish(new Error('source buffer failed'));
    buffer.addEventListener('updateend', onEnd);
    buffer.addEventListener('error', onError);
    try {
      work();
    } catch (error) {
      finish(error);
    }
  });

  const widevineConfig = () => ({
    initDataTypes: ['cenc', 'keyids', 'webm'],
    audioCapabilities: [
      { contentType: 'audio/mp4; codecs="mp4a.40.2"', robustness: 'SW_SECURE_CRYPTO' },
      { contentType: 'audio/mp4; codecs="mp4a.40.5"', robustness: 'SW_SECURE_CRYPTO' }
    ],
    videoCapabilities: [
      { contentType: 'video/mp4; codecs="avc1.42E01E"', robustness: 'SW_SECURE_CRYPTO' },
      { contentType: 'video/mp4; codecs="avc1.4d401f"', robustness: 'SW_SECURE_CRYPTO' },
      { contentType: 'video/mp4; codecs="avc1.64001f"', robustness: 'SW_SECURE_CRYPTO' },
      { contentType: 'video/mp4; codecs="avc1.640028"', robustness: 'SW_SECURE_CRYPTO' }
    ],
    distinctiveIdentifier: 'optional',
    persistentState: 'optional',
    sessionTypes: ['temporary']
  });

  const flushEme = async () => {
    const queued = pendingEme;
    pendingEme = [];
    const rank = (op) => op === 'cert' ? 0 : op === 'generate' ? 1 : op === 'update' ? 2 : 9;
    queued.sort((left, right) => rank(left.op) - rank(right.op));
    for (const command of queued) {
      if (command.op === 'cert') {
        await applyCertificate(command);
      } else if (command.op === 'generate') {
        await generateWidevine(command);
      } else if (command.op === 'update') {
        await updateWidevine(command);
      }
    }
  };

  const applyCertificate = async (command) => {
    const ok = mseKeys ? await mseKeys.setServerCertificate(bytesFromBase64(command.bytes || '')) : false;
    emit('eme-cert', { id: String(command.id || ''), ok: ok !== false });
  };

  const applyPendingMseSeek = () => {
    if (pendingMseTime == null || video.readyState === 0) {
      return;
    }
    const target = pendingMseTime;
    try {
      video.currentTime = target;
      pendingMseTime = null;
    } catch (ignore) {}
  };

  const closeMse = async (failPending) => {
    mseMode = false;
    pendingMseTime = null;
    if (failPending) {
      const queued = pendingEme;
      pendingEme = [];
      queued.forEach((command) => {
        emit('mse-error', { id: String(command.id || ''), op: command.op || '', message: 'media source closed' });
      });
    }
    for (const session of mseSessions.values()) {
      try {
        await session.close();
      } catch (ignore) {}
    }
    mseSessions.clear();
    mseBuffers.clear();
    mseKeys = null;
    mseSource = null;
    try {
      video.pause();
    } catch (ignore) {}
    try {
      await video.setMediaKeys(null);
    } catch (ignore) {}
    video = replaceVideoElement(video);
  };

  const openMse = async () => {
    await closeMse(false);
    mseMode = true;
    const source = new MediaSource();
    mseSource = source;
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('sourceopen timeout')), 8000);
      source.addEventListener('sourceopen', () => {
        clearTimeout(timer);
        resolve();
      }, { once: true });
      video.src = URL.createObjectURL(source);
    });
    let access = null;
    try {
      access = await navigator.requestMediaKeySystemAccess('com.widevine.alpha', [widevineConfig()]);
    } catch (error) {
      const plain = widevineConfig();
      plain.audioCapabilities.forEach((item) => { delete item.robustness; });
      plain.videoCapabilities.forEach((item) => { delete item.robustness; });
      access = await navigator.requestMediaKeySystemAccess('com.widevine.alpha', [plain]);
    }
    mseKeys = await access.createMediaKeys();
    await video.setMediaKeys(mseKeys);
    await flushEme();
    emit('log', { text: 'widevine mse ready' });
    emit('mse-ready', {});
  };

  const addMseBuffer = async (command) => {
    if (!mseSource || mseSource.readyState !== 'open') {
      throw new Error('media source is not open');
    }
    const buffer = mseSource.addSourceBuffer(String(command.mime || 'video/mp4'));
    mseBuffers.set(String(command.id || ''), buffer);
  };

  const appendMse = async (command) => {
    const buffer = mseBuffers.get(String(command.id || ''));
    if (!buffer) {
      throw new Error('missing source buffer');
    }
    const bytes = bytesFromBase64(command.bytes || '');
    await waitBuffer(buffer, () => buffer.appendBuffer(bytes));
    applyPendingMseSeek();
    emit('mse-update', Object.assign({ id: String(command.id || '') }, mseSnapshot(buffer)));
    emitMseState();
  };

  const removeMse = async (command) => {
    const buffer = mseBuffers.get(String(command.id || ''));
    if (!buffer) {
      return;
    }
    const start = Number(command.start) || 0;
    const end = Number(command.end) || 0;
    if (end > start) {
      await waitBuffer(buffer, () => buffer.remove(start, end));
    }
    emit('mse-update', Object.assign({ id: String(command.id || '') }, mseSnapshot(buffer)));
  };

  const generateWidevine = async (command) => {
    if (!mseKeys) {
      throw new Error('widevine keys are not ready');
    }
    const id = String(command.id || '');
    const session = mseKeys.createSession('temporary');
    mseSessions.set(id, session);
    session.addEventListener('message', (event) => {
      emit('eme-message', {
        id,
        messageType: event.messageType || 'license-request',
        bytes: base64FromBytes(event.message)
      });
    });
    session.addEventListener('keystatuseschange', () => {
      const statuses = [];
      session.keyStatuses.forEach((status) => statuses.push(status));
      emit('eme-keys', { id, sessionId: session.sessionId || '', statuses });
    });
    await session.generateRequest(command.initDataType || 'cenc', bytesFromBase64(command.bytes || ''));
    emit('eme-generated', { id, sessionId: session.sessionId || '' });
  };

  const updateWidevine = async (command) => {
    const session = mseSessions.get(String(command.id || ''));
    if (!session) {
      throw new Error('missing widevine session');
    }
    await session.update(bytesFromBase64(command.bytes || ''));
    emit('eme-updated', { id: String(command.id || '') });
  };

  const handlers = {
    async open(command) {
      activeToken += 1;
      await destroyPlayer();
      gen = command.gen;
      url = command.url || '';
      licenseServer = '';
      customData = '';
      wantPlaying = false;
      announcedPrepared = false;
      soapCustomData = false;
      licensePersistence = 'unknown';
    },
    async close() {
      activeToken += 1;
      wantPlaying = false;
      announcedPrepared = false;
      clearShades();
      await destroyPlayer();
      url = '';
    },
    async reset() {
      activeToken += 1;
      wantPlaying = false;
      announcedPrepared = false;
      clearShades();
      await destroyPlayer();
      url = '';
    },
    async stop() {
      activeToken += 1;
      wantPlaying = false;
      announcedPrepared = false;
      await destroyPlayer();
    },
    async drm(command) {
      if (command.drmType === 'PLAYREADY' && command.operation === 'SetProperties') {
        const props = parseDrm(command.data);
        licenseServer = props.LicenseServer || '';
        customData = props.CustomData == null ? '' : String(props.CustomData);
      }
    },
    async prepare() {
      activeToken += 1;
      const token = activeToken;
      announcedPrepared = false;
      armModes();
      await raceCancel(token, attemptLoad(token));
    },
    async play() {
      wantPlaying = true;
      if (!prepared) {
        return;
      }
      startPlayback();
    },
    async pause() {
      wantPlaying = false;
      video.pause();
    },
    async jump(command) {
      await seekTo(video.currentTime + (Number(command.ms) || 0) / 1000, command.callback);
    },
    async seek(command) {
      await seekTo((Number(command.ms) || 0) / 1000, command.callback);
    },
    async displayMethod(command) {
      const fits = {
        PLAYER_DISPLAY_MODE_LETTER_BOX: 'contain',
        PLAYER_DISPLAY_MODE_ORIGIN_SIZE: 'none',
        PLAYER_DISPLAY_MODE_FULL_SCREEN: 'fill',
        PLAYER_DISPLAY_MODE_CROPPED_FULL: 'cover',
        PLAYER_DISPLAY_MODE_ORIGIN_OR_LETTER: 'scale-down',
        PLAYER_DISPLAY_MODE_AUTO_ASPECT_RATIO: 'contain'
      };
      objectFit = fits[command.method] || 'fill';
      video.style.objectFit = objectFit;
    },
    async track(command) {
      const found = trackRefs.get(Number(command.index));
      if (!found || !player) {
        return;
      }
      if (found.kind === 'audio') {
        player.selectAudioTrack(found.ref);
      } else {
        player.selectTextTrack(found.ref);
        if (subtitles) {
          subtitles.setTextVisibility(true);
        }
      }
      emit('tracks', trackInfo());
    },
    async speed(command) {
      playbackRate = Number(command.rate) || 1;
      video.playbackRate = playbackRate;
    },
    async suspend() {
      resumeAfterRestore = wantPlaying || !video.paused;
      wantPlaying = false;
      video.pause();
    },
    async restore() {
      if (resumeAfterRestore) {
        resumeAfterRestore = false;
        await handlers.play();
      }
    },
    async shade(command) {
      syncShades(command);
    },
    async display() {},
    async property() {},
    async media(command) {
      const op = command.op || '';
      const id = String(command.id || '');
      if (op === 'open') {
        await openMse();
        return;
      }
      if (op === 'close') {
        await closeMse(true);
        emit('mse-closed', {});
        return;
      }
      if (op === 'add') {
        await addMseBuffer(command);
        return;
      }
      if (op === 'append') {
        await appendMse(command);
        return;
      }
      if (op === 'remove') {
        await removeMse(command);
        return;
      }
      if (op === 'abort') {
        const buffer = mseBuffers.get(id);
        if (buffer) {
          try {
            buffer.abort();
          } catch (ignore) {}
        }
        return;
      }
      if (op === 'offset') {
        const buffer = mseBuffers.get(id);
        if (buffer) {
          buffer.timestampOffset = Number(command.seconds) || 0;
        }
        return;
      }
      if (op === 'window') {
        const buffer = mseBuffers.get(id);
        if (buffer) {
          buffer.appendWindowStart = 0;
          buffer.appendWindowEnd = command.end == null ? Infinity : Number(command.end);
          buffer.appendWindowStart = Number(command.start) || 0;
        }
        return;
      }
      if (op === 'type') {
        const buffer = mseBuffers.get(id);
        if (buffer && buffer.changeType) {
          buffer.changeType(String(command.mime || ''));
        }
        return;
      }
      if (op === 'mode') {
        const buffer = mseBuffers.get(id);
        if (buffer) {
          try {
            buffer.mode = command.mode === 'sequence' ? 'sequence' : 'segments';
          } catch (ignore) {}
        }
        return;
      }
      if (op === 'end') {
        if (mseSource && mseSource.readyState === 'open') {
          mseSource.endOfStream();
        }
        return;
      }
      if (op === 'duration') {
        const duration = command.seconds === 'Infinity' ? Infinity : Number(command.seconds);
        if (mseSource && (Number.isFinite(duration) || duration === Infinity)) {
          mseSource.duration = duration;
        }
        emitMseState();
        return;
      }
      if (op === 'play') {
        const played = video.play();
        if (played && typeof played.catch === 'function') {
          played.catch(() => {});
        }
        emitMseState();
        return;
      }
      if (op === 'pause') {
        video.pause();
        emitMseState();
        return;
      }
      if (op === 'seek') {
        seekMse(Number(command.seconds) || 0);
        return;
      }
      if (op === 'rate') {
        video.playbackRate = Number(command.rate) || 1;
        return;
      }
      if (op === 'generate') {
        if (!mseKeys) {
          pendingEme.push(command);
          return;
        }
        await generateWidevine(command);
        return;
      }
      if (op === 'update') {
        if (!mseSessions.has(id)) {
          pendingEme.push(command);
          return;
        }
        await updateWidevine(command);
        return;
      }
      if (op === 'cert') {
        if (!mseKeys) {
          pendingEme.push(command);
          return;
        }
        await applyCertificate(command);
      }
    }
  };

  // An empty MSE element never fires seeked. Waiting for it here holds the
  // single command queue, so a following Widevine update never reaches the CDM.
  const seekMse = (target) => {
    pendingMseTime = target;
    if (video.readyState === 0) {
      emit('mse-seeked', { state: Object.assign(mseSnapshot(null), { time: target }) });
      return;
    }
    try {
      video.currentTime = target;
      pendingMseTime = null;
    } catch (ignore) {}
    emit('mse-seeked', { state: Object.assign(mseSnapshot(null), { time: target }) });
  };

  const seekTo = async (seconds, callback) => {
    const element = video;
    if (!prepared || !player) {
      emit('jumped', { ok: false, callback });
      return;
    }
    const range = player.seekRange();
    const target = Math.min(Math.max(seconds, range.start), range.end);
    await new Promise((resolve) => {
      const done = () => {
        element.removeEventListener('seeked', done);
        resolve();
      };
      element.addEventListener('seeked', done);
      element.currentTime = target;
      setTimeout(done, 5000);
    });
    if (element !== video) {
      emit('jumped', { ok: false, callback });
      return;
    }
    lastTimeMs = Math.floor(element.currentTime * 1000);
    emit('time', { ms: lastTimeMs });
    emit('jumped', { ok: true, callback });
  };

  host.addEventListener('message', (event) => {
    const command = event.data || {};
    const handler = handlers[command.cmd];
    if (!handler) {
      return;
    }
    if (['open', 'close', 'stop', 'reset'].indexOf(command.cmd) !== -1) {
      activeToken += 1;
      if (failLoad) {
        const reject = failLoad;
        failLoad = null;
        reject(Object.assign(new Error('cancelled'), { name: 'AbortError' }));
      }
      if (loading && player) {
        loading = false;
        player.unload().catch(() => {});
      }
    }
    queue = queue.then(() => handler(command)).catch((error) => {
      if (command.cmd === 'media') {
        emit('mse-error', { id: String(command.id || ''), op: command.op || '', message: describe(error) });
        return;
      }
      emit('error', { code: 'PLAYER_ERROR_GENEREIC', message: command.cmd + ': ' + describe(error) });
    });
  });

  bindVideo(video);
  try {
    shaka.polyfill.installAll();
  } catch (error) {
    host.postMessage({ type: 'log', text: 'polyfill ' + (error && error.message ? error.message : error) });
  }
  installPlayReadyAccess();
  emit('ready', { shaka: shaka.Player.version });
})();
