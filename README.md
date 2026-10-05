# Holo Snek Refresh

**0.4.0 · Windows x64 · GPL-3.0-only**

The TIFF/GIF contains the interpreter, the Snake program and its live state. The C# harness loads the approved interpreter from the image pixels, supplies arrow-key input, executes the image program, and writes each new state back into pixels.

## Play

Download this repository as a ZIP, **extract the whole folder**, then double-click **Play.cmd**. The compiled harness and its .NET dependencies are included. No Python, SDK, installation or build step is required.

You can also download the [Windows release](https://github.com/LKVexa/holo-snek-pixel-arcade/releases/tag/v0.4.0).

**Arrows/WASD** steer, **Space** pauses/resumes, **R** restarts, **O** opens a saved image, and **E** saves TIFF and GIF continuation images. The game starts automatically. Open a saved image in this harness to continue playing its encoded state. Ordinary image viewers display it; this harness executes it.

## Files

- `game.tiff` and `game.gif`: identical executable runtime, program and starting state in pixels.
- `CSharpHarness/`: the compiled Windows harness, its framework dependencies, C# source and dependency notices.
- `Play.cmd`: opens the image in the harness.
- `LICENSE` and this README.

The host verifies a pinned SHA-256 before loading the interpreter from the pixels. It contains no Snake-rule routine or loose `ImageRuntime.dll` fallback. The .NET CLR, image decoder, input and rendering remain external bootstrap services. The adjacent hologram is a numerical interference visualization; computation runs on the CPU.

## Verification

The packaged EXE passed both image formats, save/reopen continuation, image-only score and collision edits, corrupt-input rejection, arrow handling, pause/resume and three automatic timer refreshes. Those tests run with PATH restricted to Windows System32, without installed Python or .NET. The independent VM audit includes 15 C# self-tests and 1,396 differential cases.

Input limits include 32 MiB files, 32 frames, 960 × 720 pixels, a 24,000-byte payload, 256 program instructions and 30,000 executed instructions per refresh. TIFF/GIF structures are preflighted before native decoding. Unsupported images and unapproved runtimes are rejected. Keep the image at its original size; lossy editing can destroy executable bits.

## Source and license

All project code, runtime, images and documentation are **GPL version 3 only**; see `LICENSE`. The harness and image interpreter source are in `CSharpHarness/Source/`. To rebuild, install .NET SDK 10.0.401 and run `CSharpHarness/Source/Build.cmd`. The compiled module must match the approved image runtime hash. Full authoring, tests and audit source are also included in `source.zip` inside the Windows release.

Bundled Microsoft framework dependencies retain their MIT and upstream third-party licenses, supplied in `CSharpHarness/`. They are not relicensed copies of this project's code.

Copyright © 2026 LKVexa.
