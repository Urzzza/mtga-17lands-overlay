# Windows fullscreen release gate

**NOT RUN.** A successful build or core test run does not satisfy this gate. Current development environment: macOS ARM64, without a live Windows/Arena session.

Run this check **first** on Windows 11 x64, before accepting the candidate UI for release:

```powershell
.\Overlay.Windows.exe --fullscreen-probe
```

The probe uses synthetic cards, requires no statistics import, and tracks Arena's actual foreground window. It must remain visible and click-through at the lower-left of Arena during normal Alt+Enter fullscreen transitions. The probe label deliberately says FULLSCREEN PROBE.

Record Windows version, Arena version and installation channel, GPU/driver, monitor resolution/scale, fullscreen optimizations setting, and candidate commit/build identity. Record PASS/FAIL and observations for each:

- [ ] With Arena foreground, probe is visible and game input passes through every part of it; clicking does not activate the overlay or minimize Arena.
- [ ] Toggle Alt+Enter ten times. The probe remains visible and reanchors within one second after every transition, without changing Arena's display settings.
- [ ] Alt+Tab out/in ten times. The panel hides outside Arena and returns when Arena becomes foreground; it never steals focus.
- [ ] Minimize/restore Arena and move it between monitors with 100%, 150%, and 200% scaling; panel follows and stays inside the game client area.
- [ ] Change resolution and test the smallest supported window size. All current pack cards remain readable and on screen.
- [ ] Exit probe. Start normally and run an actual Premier, Traditional, and Quick Draft. Confirm each detected set/format, pack and pick number against Arena.
- [ ] Verify unlogged P1P1 shows waiting, every logged pack replaces the prior one, each pick clears it, and drafting exit/completion hides it.
- [ ] Start the app mid-draft; restart Arena; confirm logs recover without resurrecting a picked pack.
- [ ] Open dataset downloads, select multiple sets/formats, confirm unavailable pairs are skipped, download/import a small selection, cancel another queue, and reopen to confirm selections are remembered.
- [ ] Import a known dataset, verify a few card rates by hand, cancel a replacement, disconnect network, and verify cached statistics and metadata remain usable.

If the fullscreen visibility/input test fails, **do not release or label fullscreen support as complete**. Capture the failing configuration and stop release work. Do not silently replace the requirement with windowed play, install Overwolf, alter Arena settings, or introduce process hooks. Revisit the rendering design with concrete Windows evidence.

Forced exclusive fullscreen is unverified and is not assumed equivalent to Arena's normal Alt+Enter behavior. Publish signed release artifacts only after this checklist is completed and its evidence is recorded.
