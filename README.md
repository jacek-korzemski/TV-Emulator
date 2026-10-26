### AI disclaimer

This app is vibe-coded. The author knew the behavior they wanted, understood the problems they hit, and told agents what was wrong and why. They have not personally walked the codebase. **This is not a portfolio project.** It is a toy, shared in case someone else needs the same thing.

---

# TV Emulator

A Windows program that opens a page the way a chosen Samsung Tizen or LG webOS television would. **Launch** fills a 16:9 picture. You drive it with a remote: keyboard or gamepad. The mouse is not how you use the page.

A short public overview lives at <https://jacek-korzemski.github.io/TV-Emulator/> after the landing page has been published once (see [Publish the landing page](#publish-the-landing-page)).

## What it emulates

| Profile | Chromium | Picture |
| --- | --- | --- |
| Samsung Tizen 3.0 (2017) | 47 | avplay |
| Samsung Tizen 4.0 (2018) | 56 | avplay |
| Samsung Tizen 5.0 (2019) | 63 | avplay |
| Samsung Tizen 6.5 (2022) | 85 | avplay |
| LG webOS TV 4.x (2018–2019) | 53 | edge |
| Hisense VIDAA U2-class | 74 | edge |
| Hisense VIDAA U6 | 88 | edge |
| Vizio SmartCast | 47, 56, 63 | edge |
| Philips NetTV / early Saphi | 49 | edge |
| Philips Saphi 2020–2022 | 84 | edge |
| Titan OS | Chrome 122 | edge |
| HbbTV 1.5 | 53 | edge |
| Panasonic my Home Screen (2016) | Firefox 44 | edge |
| Sony BRAVIA / Vewd | 47 | edge |

The page runs in that pinned browser, with a shim for the set (`tizen`, `webapis`, `webOS`, VIDAA `Hisense_Get*`, `VIZIO`, Philips `DeviceInfo`, HbbTV `video/broadcast`, `Panasonic`, or `Sony`) plus the remote key codes.

`edge` means the picture is the WebView2 plane under the page. `page` means the page's own engine plays it. Titan still runs the page in Chrome for Testing 122, but that build has no Widevine CDM, so the picture goes through the Edge plane like the older Chromium sets. Public Chromium snapshots still have no H.264 and no Widevine, so VIDAA U6 stays on `edge` even though that generation's Chrome is newer. Panasonic is Firefox 44 (Firefox OS 2.5). That build has no Widevine CDM, so the picture uses the Edge plane. It also has no Chrome DevTools Protocol, so the live console, network, and memory tabs stay empty; the session log still works. Sony is Chromium 47 with an OMI user agent. The Vewd TV binary is not public.

Video does not play inside that old Chromium. Those builds lack the codecs and DRM a TV app expects.

- **Tizen is Chromium plus avplay.** `webapis.avplay` is the native Samsung player. Calls are forwarded to a WebView2 window (current Edge) behind the page. Edge has H.264, AAC, and PlayReady, so the video plane can play a clear or PlayReady DASH stream. The page punches a key color and the video shows through, the same idea as a TV video layer under the HTML.
- **webOS is Chromium plus Edge.** webOS 4 plays in the HTML engine with Widevine, not avplay. Chromium 53 has no Widevine CDM and no H.264. The page still picks variants and downloads segments. Edge owns the Widevine session and the decoder. The video element in the old page is only a clock. PlayReady is the Tizen path and is not used here.

Processor and memory caps are optional and stored on this PC. The profile files leave them empty. **Moc telewizora**, under the profile list, applies a cap to the browser process only. The video plane is not capped.

## What you need

- Windows 10 or Windows 11.
- Several gigabytes free. The first setup downloads old browsers.
- Internet for that first download, and again if you play the video sample.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), version 8.0.426 or any newer .NET 8.

WebView2, used for the video picture, is usually already on Windows. The installer is linked below if a film will not start.

## Get started

1. Unpack the archive or clone the repository.
2. Open PowerShell in the folder that contains `TvEmulator.sln`. In File Explorer you can click the address bar, type `powershell`, and press Enter.
3. Check that .NET is visible:

```powershell
dotnet --version
```

The number should start with `8.`. If PowerShell says it does not recognize `dotnet`, install the SDK from the link above and open a new window.

4. Download the browsers that match the televisions:

```powershell
powershell -ExecutionPolicy Bypass -File tools\fetch-engines.ps1
```

This takes a while. The script prints `GET` while downloading, `UNZIP` while unpacking, and `READY` when that browser is already on disk. The last line should be `FETCH_DONE`.

Television profiles use only the `chromium-*` browsers.

5. Start the program:

```powershell
dotnet run --project src\TvEmulator\TvEmulator.csproj
```

The first build takes a moment. Then the **TV Emulator** window opens.

Leave the program in this folder. It looks for `TvEmulator.sln` beside itself. A lone `TvEmulator.exe` copied to the desktop will not start correctly.

Next time: open PowerShell in the same folder and run only `dotnet run`.

## Settings window

- **TV profile.** The name and year are on the first line. Under that: the Chromium version and the playback method, `avplay` on Tizen, `edge` on webOS.
- **App.** A folder that contains `index.html`, or an address starting with `http://` or `https://`. **Browse** picks a folder.
- **Monitor.** The screen for the 16:9 picture. With two monitors, pick the one you are looking at.
- **Settings (Ustawienia).** Saves a default app, a folder or an `http(s)` address, and fills it in at every start. Uncheck TV profiles you do not use and they leave the profile list. New profiles stay visible until you hide them. Both live in `%LOCALAPPDATA%\TvEmulator\settings.json`. Leave at least one profile visible.
- **Magic remote pointer.** When checked, the cursor is visible.
- **Diagnostic log.** On at startup. The program writes `logs\session-….log`. After a failure, the bar at the bottom of the window shows that path.

**Moc telewizora**, under the profile list, caps the selected television: a percent of one logical thread, and a memory ceiling in MB for the browser process. Empty fields turn the cap off. The numbers are saved in the same `settings.json`. The line under the list shows them. The video plane stays outside the cap.

### What to open first

On startup the app field is the default saved in **Ustawienia**. When no default is set, it is `samples\tizen-hello`: a simple page with a title and key codes, and no video. Pick a Tizen profile and press **Launch** (`Uruchom`).

To see video, point at `samples\tizen-avplay`. You need internet. Enter or the `1` key plays a stream without DRM.

For the webOS profile, point at `samples\webos-hello`.

`samples\platform-hello` prints the user agent and whichever device API the selected profile installed. `samples\hbbtv-broadcast` is for the HbbTV profile: Enter binds `video/broadcast` to a clear DASH stream.

Your own app is a folder with `index.html`, or a pasted page address.

## Controls

- Arrows and Enter work like a remote.
- Esc is Back.
- F1, F2, F3, and F4 are the color keys.
- Gamepad: D-pad, A = Enter, B = Back, LB, RB, X, and Y = colors.
- **Ctrl+Shift+Q** returns to the settings window.

End a session with that shortcut or by closing the settings window. Killing Chromium from Task Manager drops the page’s saved session.

## Live preview

The settings window shows a preview address as soon as the program starts (it prefers port **2137**). Open it in a browser on this PC, or on a phone on the same Wi-Fi. The preview UI is in Polish. It follows the running session over a WebSocket.

Turn on **Diagnostic log** before **Launch**. That is what attaches to the page and fills the preview. The same switch writes `logs\session-….log`.

Four tabs:

### Console (`Konsola`)

Live `console` output and uncaught exceptions from the television page, with level filters. Stacks can be expanded. The list keeps the newest lines and can be cleared from the toolbar.

### Network (`Sieć`)

Requests captured from the page as they happen: XHR, document, script, stylesheet, image, media, font, WebSocket, and the rest. Filter with the type chips or the URL box. Open a row for request and response headers and for bodies (bodies are cut off around 256 KB; some media and binary bodies are skipped). About 300 entries are kept.

### Memory (`Pamięć`)

Two sources, **page** and **video**. They do not share a heap. The player and the app are measured apart.

The top number and the first line are `performance.memory`: how much of the JavaScript heap is in use, plus a DOM node count. That is not a list of objects. A sparkline shows the used heap over time.

The second line is a lighter trace of live objects. It updates in small pieces and does not pause the page. A drop is a garbage collection. The older collector drops rarely and steeply. The newer one drops more often, in smaller steps.

**Heap snapshot** (`Zrzut sterty`) pauses the television page for a moment and dumps the heap. The first snapshot is a baseline. The next one is a diff against the previous:

- **Zostało** — still there, largest own size.
- **Urosło** — grew since the previous snapshot.
- **Zniknęło** — fewer instances or fewer bytes, which is usually what the collector reclaimed.

Column headers sort the table. A leak looks like a constructor that grows between snapshots and does not shrink after you leave a screen: `Array`, `Object`, string, `ArrayBuffer`, or DOM nodes. Snapshot on the screen, leave it, snapshot again.

A full snapshot on Chromium older than 62 can kill that browser. The 2017–2018 Tizen profiles and the webOS 4 profile are in that range. Prefer the live lines there, and treat a full dump as a last resort.

### Log (`Log`)

The session diagnostic lines, the same stream that lands in `logs\session-….log`.

## When something fails

**PowerShell does not recognize `dotnet`.** Install the .NET 8 SDK and open PowerShell again. `dotnet --version` should show `8.…`.

**The script will not run, or it complains about execution policy.** Use the full `ExecutionPolicy Bypass` command from [Get started](#get-started).

**The download stopped halfway.** Run the script again. `READY` means that engine is already present and will not be downloaded twice. Wait for `FETCH_DONE`.

**The bottom of the window says the engine file for `chromium-…` is missing.** The download script never reached `FETCH_DONE`. Run it again from the folder that contains `TvEmulator.sln`.

**Windows or antivirus blocks `chrome.exe`.** Allow it. These are official old Chromium builds chosen to match the televisions, not a TV installer.

**`TvEmulator.sln` was not found.** The program was started outside the project folder. Go back and use `dotnet run` as above.

**It asks for a folder with `index.html` or an http(s) address.** The chosen folder has no `index.html`, or the address does not start with `http://` or `https://`.

**You see “the engine picture is not visible”.** The picture is on the monitor selected in the list. Check the other screen. **Ctrl+Shift+Q** returns to settings.

**The log mentions WebView2, or “the video plane (WebView2) did not start”.** Install the [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) and start again. `tizen-hello` is not supposed to show a film. For a picture, use `samples\tizen-avplay`.

**Chromium failed to start, or there is no free localhost port.** A previous session is still running. Return to it with **Ctrl+Shift+Q**, or close the program window and try again.

**You want to sign in again.** Close the program, then delete `%LOCALAPPDATA%\TvEmulator\userdata`. You can paste `%LOCALAPPDATA%\TvEmulator` into the Explorer address bar. The default app and hidden profiles are in `settings.json` beside that folder. Deleting only `userdata` leaves them in place.

## Publish the landing page

The site in `docs/` is published only when someone runs the workflow by hand. A push does not deploy it.

1. In the GitHub repository, open **Actions**.
2. Choose **Deploy landing page**.
3. **Run workflow** on `main`.

The first successful run needs GitHub Pages to use **GitHub Actions** as the source (Settings → Pages). After that, the site is at <https://jacek-korzemski.github.io/TV-Emulator/>.

## Changelog

Landing-page notes live in `docs/changelog.json`. In Cursor, `/update-changelog` reads changes that are not on `master` (or `main` if there is no master), writes them in plain language, and prepends one entry at the top of that file.

## Tests

From the project folder: `dotnet test`.
