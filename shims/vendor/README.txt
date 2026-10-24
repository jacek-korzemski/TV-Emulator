shaka-player 4.16.54 (npm), dist/shaka-player.compiled.js, Apache-2.0.
codem-isoboxer 0.3.10 (npm), dist/iso_boxer.min.js, MIT.
Both are used only by shims/videoplane.html inside the WebView2 video plane.
ISOBoxer must load before Shaka. Shaka 4.16 plays DASH and Smooth Streaming VOD; it rejects MSS live.
