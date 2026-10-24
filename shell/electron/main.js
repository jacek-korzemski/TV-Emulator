/*
 * ES5 on purpose: Electron 0.36 ships Node 5.1. require('electron') is a path
 * string before 1.0 and the API object after that.
 * The page never sees Node. This window is transparent, so the TV app's own
 * pixels (including fades and scrims) composite over the video plane.
 */
var electron = require('electron');
var app;
var BrowserWindow;
var Menu;
if (typeof electron === 'string') {
  app = require('app');
  BrowserWindow = require('browser-window');
  try {
    Menu = require('menu');
  } catch (ignore) {
    Menu = null;
  }
} else {
  app = electron.app;
  BrowserWindow = electron.BrowserWindow;
  Menu = electron.Menu || null;
}

function arg(name) {
  var prefix = '--' + name + '=';
  var i;
  for (i = 0; i < process.argv.length; i++) {
    if (process.argv[i].indexOf(prefix) === 0) {
      return process.argv[i].substring(prefix.length);
    }
  }
  return '';
}

function switches() {
  var prefix = '--tv-switch=';
  var found = [];
  var i;
  for (i = 0; i < process.argv.length; i++) {
    if (process.argv[i].indexOf(prefix) === 0) {
      found.push(process.argv[i].substring(prefix.length));
    }
  }
  return found;
}

var bounds = (arg('tv-bounds') || '0,0,1920,1080').split(',');
var x = parseInt(bounds[0], 10) || 0;
var y = parseInt(bounds[1], 10) || 0;
var width = parseInt(bounds[2], 10) || 1920;
var height = parseInt(bounds[3], 10) || 1080;
var scale = arg('tv-scale') || '1';
var userData = arg('tv-user-data');
var debugPort = arg('tv-debug-port');
var url = arg('tv-url');
var ua = arg('tv-user-agent');
var major = parseInt(String(process.versions.electron || '0').split('.')[0], 10) || 0;
var extra = switches();

if (userData) {
  try {
    app.setPath('userData', userData);
  } catch (ignore) {}
}

try {
  app.commandLine.appendSwitch('disable-gpu');
  if (typeof app.disableHardwareAcceleration === 'function') {
    app.disableHardwareAcceleration();
  }
  app.commandLine.appendSwitch('force-device-scale-factor', scale);
  app.commandLine.appendSwitch('no-proxy-server');
  if (debugPort) {
    app.commandLine.appendSwitch('remote-debugging-port', debugPort);
  }
  for (var s = 0; s < extra.length; s++) {
    var spec = extra[s];
    var eq = spec.indexOf('=');
    if (eq === -1) {
      app.commandLine.appendSwitch(spec);
    } else {
      app.commandLine.appendSwitch(spec.substring(0, eq), spec.substring(eq + 1));
    }
    if (spec === 'disable-gpu' && typeof app.disableHardwareAcceleration === 'function') {
      app.disableHardwareAcceleration();
    }
  }
} catch (ignore) {}

if (ua && major >= 4) {
  try {
    app.userAgentFallback = ua;
  } catch (ignore) {}
}

app.on('window-all-closed', function () {
  app.quit();
});

app.on('ready', function () {
  if (Menu && typeof Menu.setApplicationMenu === 'function') {
    try {
      Menu.setApplicationMenu(null);
    } catch (ignore) {}
  }
  var prefs = { nodeIntegration: false, plugins: false, backgroundThrottling: false };
  if (major >= 5) {
    prefs.contextIsolation = true;
  }
  var options = {
    x: x,
    y: y,
    width: width,
    height: height,
    frame: false,
    transparent: false,
    resizable: false,
    useContentSize: true,
    show: true,
    webPreferences: prefs
  };
  var win = new BrowserWindow(options);
  if (win.webContents && typeof win.webContents.on === 'function') {
    win.webContents.on('new-window', function (event) {
      event.preventDefault();
    });
  }
  if (ua && win.webContents && typeof win.webContents.setUserAgent === 'function') {
    try {
      win.webContents.setUserAgent(ua);
    } catch (ignore) {}
  }
  win.loadURL(url);
});
