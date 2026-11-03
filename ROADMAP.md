# Waiting until tokens refresh

As I said - this is totally vibe-coded app, no wonder what is inside. Lets say - it's a way to train "new way of programming, because some CEO believe this is the future. I have to be prepared for this."

So below are some things to do after refresh of tokens in next month:

## 1 Optimization

Some profiles like Tizen are now rendered on GPU. For Tizen 9, 6.5 and 4 - this is enough. AVPlay doesn't slow down the profile. Sadly - profiles that uses Chromium + Edge for DRMs are still a little slow in comparison to real hardware (even though real hardware has much less computing power). Previous agent asked about optimization this kind of profiles said as follows.

### AI Agent about optimization

A few localized changes in `ChromaPresenter` should noticeably reduce UI lag—across all profiles that use an Edge-based surface, not just on VIDAA 9. There is no separate, slower code path for that specific profile.

The primary performance cost lies in the `Draw` method: the Windows Graphics Capture frame callback holds the buffer until the drawing process is complete, at which point it calls `Present(1, …)`. A sync interval of 1 blocks the thread until the V-blank; since the pool contains only 2 frames, any third frame is forced to wait. Furthermore, for every single frame, the code re-wraps the DXGI texture and recreates the Direct2D bitmap.

```154:158:src/TvEmulator/Services/ChromaPresenter.cs
_pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
_winrtDevice,
DirectXPixelFormat.B8G8R8A8UIntNormalized,
2,
size);
```

```255:281:src/TvEmulator/Services/ChromaPresenter.cs
void Draw(Direct3D11CaptureFrame frame)
{
var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>(); 
// ... CreateBitmapFromDxgiSurface on every frame ...
_d2dContext.EndDraw(); 
_swapChain!.Present(1, PresentFlags.None); 
_dcomp!.Commit(); 
}
```

These are low-effort, high-impact fixes:

- Use `Present(0)` instead of `Present(1)`. Since the swap chain is compositional (`CreateSwapChainForComposition`), V-sync is handled by the DWM (Desktop Window Manager), so the capture callback shouldn't be waiting for it.
- Increase the frame pool size from 2 to 3 and release the capture frame before calling `Present`.
- Implement a cache for two Direct2D bitmaps, keyed by the texture pointer. The pool ends up recycling the same surfaces over and over.
- At a 1:1 scale (this session is 1920×1080), use `InterpolationMode.NearestNeighbor` instead of `Linear`.

Separately, after the window is adjusted, `Maintain` calls `Sink` every second—resulting in two `SetWindowPos` operations to push it to the bottom of the stack. If the window is already positioned there, these calls can be skipped.

The chroma effect at 1080p is inexpensive compared to this blocking behavior. Reverting to color key and using `--disable-gpu` would speed up the presenter, though it would slow down the actual rendering of the page.

This session was also running with debugging enabled (otherwise, this log wouldn't exist). `CdpWatcher` invokes `Network.enable`, and the planner adds the `--remote-debugging-port=9223` flag. Furthermore, the network inspection feature in Chromium 111 introduces overhead before the presenter even begins its work.

## 2 Verify compatibility with apps that serve live content

Some VOD platforms offers also live content. On some profiles it works, and on other - it doesn't. Sometimes, it's because services doesn't like 127.0.0.1 as hosts, and we need proxy, and other have some other issues. So - I have to check every profile and verify its compatibility with live content, and adjust every profile so it would run fast and easy.