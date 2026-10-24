/*
 * ES5. Electron 0.36 returns a path string from require('electron'); 1.0+ returns the API.
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

var bounds = (arg('tv-bounds') || '80,80,960,540').split(',');
var x = parseInt(bounds[0], 10) || 0;
var y = parseInt(bounds[1], 10) || 0;
var width = parseInt(bounds[2], 10) || 960;
var height = parseInt(bounds[3], 10) || 540;
var scale = arg('tv-scale') || '1';
var userData = arg('tv-user-data');
var debugPort = arg('tv-debug-port');
var gpu = arg('tv-gpu');
var url = arg('tv-url');
var major = parseInt(String(process.versions.electron || '0').split('.')[0], 10) || 0;

if (userData) {
  try {
    app.setPath('userData', userData);
  } catch (ignore) {}
}
try {
  app.commandLine.appendSwitch('force-device-scale-factor', scale);
  app.commandLine.appendSwitch('no-proxy-server');
  if (debugPort) {
    app.commandLine.appendSwitch('remote-debugging-port', debugPort);
  }
  if (gpu === '0') {
    app.commandLine.appendSwitch('disable-gpu');
    try {
      app.disableHardwareAcceleration();
    } catch (ignore) {}
  }
} catch (ignore) {}

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
    transparent: true,
    resizable: false,
    useContentSize: true,
    show: true,
    webPreferences: prefs
  };
  if (major >= 2) {
    options.backgroundColor = '#00000000';
  }
  if (major >= 10) {
    options.thickFrame = false;
  }
  var win = new BrowserWindow(options);
  if (typeof win.setBackgroundColor === 'function' && major >= 2) {
    try {
      win.setBackgroundColor('#00000000');
    } catch (ignore) {}
  }
  if (win.webContents && typeof win.webContents.on === 'function') {
    win.webContents.on('new-window', function (event) {
      event.preventDefault();
    });
  }
  win.loadURL(url);
});
