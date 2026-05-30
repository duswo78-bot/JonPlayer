# JonPlayer Architecture and Structure

## 1. Overview
JonPlayer is a high-performance, custom-built media player for Windows, built on **.NET 8** and **WPF**. To overcome the limitations of standard WPF media elements and VLC wrappers, JonPlayer utilizes a robust hardware-accelerated playback pipeline.

## 2. Core Technologies
- **UI Framework**: WPF (Windows Presentation Foundation)
- **Media Decoding**: FFmpeg (via `FFmpeg.AutoGen` bindings)
- **Hardware Rendering**: Direct3D 11 & Direct3D 9Ex (via `Vortice.Windows` bindings)
- **Interop**: `D3DImage` (WPF native interop for high-performance GPU surface sharing)

## 3. Project Structure

### `App.xaml` / `App.xaml.cs`
The entry point of the WPF application. Manages application-level resources and startup logic.

### `MainWindow.xaml` / `MainWindow.xaml.cs`
The main user interface.
- **UI Components**:
  - Custom Title Bar (Borderless window)
  - Video display area (`VideoGrid`) using `D3D11VideoRenderer`
  - Playback controls (Timeline slider, Play/Pause, Rewind, Fast Forward)
  - Fullscreen overlay (`FsBottomStrip`) that appears smoothly via mouse movement.
- **Logic**:
  - Handles drag-and-drop file loading.
  - Manages playback states, syncing the timeline slider with decoder callbacks.
  - Precise mouse-coordinate-based seeking.
  - Fullscreen toggling and UI overlays.

### `FFmpegVideoDecoder.cs`
The core media engine responsible for reading and decoding media files.
- **Initialization**: Opens media streams, finds the optimal video stream, and allocates FFmpeg contexts (`avformat`, `avcodec`).
- **Decoding Loop**: Runs on a dedicated background thread (`_decodeThread`).
  - Reads packets (`av_read_frame`).
  - Decodes them into frames (`avcodec_receive_frame`).
  - Converts frames into high-quality BGRA format using `sws_scale`.
- **Synchronization**: Uses a `Stopwatch` and thread synchronization (`Monitor.Wait`) to perfectly match the decoded frame timestamps (PTS) with real-time playback, supporting variable playback speeds (e.g., 0.5x, 2.0x).
- **Callbacks**: Fires events (`FrameDecoded`, `PositionChanged`, `TimeUpdated`, `PlaybackFinished`) to notify the UI and renderer.

### `D3D11VideoRenderer.cs`
The hardware-accelerated rendering component that connects FFmpeg to WPF.
- Inherits from `Image` and overrides `Source` with `D3DImage`.
- **Initialization**: Creates a `Direct3D 11` device and a legacy `Direct3D 9Ex` device (required for WPF interop).
- **Surface Sharing**: Creates a shared `Texture2D` in D3D11 and opens it in D3D9 via `SharedHandle`.
- **Rendering**:
  - Receives raw BGRA pointer from `FFmpegVideoDecoder`.
  - Uses `UpdateSubresource` on the D3D11 device to instantly copy the frame to the GPU.
  - Calls `D3DImage.AddDirtyRect` asynchronously (`BeginInvoke`) to tell WPF to redraw the screen without blocking the decode thread, ensuring 0 deadlock and high FPS.

### `Assets/`
Contains all static resources.
- `logo.png`: The transparent player logo used in the top-left title bar.
- `BG logo.png`: The splash image displayed in the center of the screen when no video is playing.
- `ffmpeg/`: Directory containing the native FFmpeg DLLs (`avcodec`, `avformat`, `avutil`, `swscale`, etc.).

## 4. Key Workflows

### Playback Flow
1. User opens a file -> `MainWindow.PlayFile()`
2. `FFmpegVideoDecoder.Open()` is called:
   - Formats and codecs are allocated.
   - Dedicated `DecodeLoop` thread starts.
3. Frames are decoded and converted to BGRA.
4. `FrameDecoded` event fires -> `D3D11VideoRenderer.RenderFrame()`
5. Renderer copies data to GPU and triggers WPF `D3DImage` refresh.

### Seek Flow
1. User clicks the timeline slider.
2. `MainWindow` calculates the exact mouse X coordinate and derives a target ratio.
3. `FFmpegVideoDecoder.Seek(ratio)` is called.
4. Decoder uses `av_seek_frame` with the global timebase (`AV_TIME_BASE`) to reliably jump to the nearest keyframe.
5. Decoding resumes smoothly from the new timestamp.

### Clean Shutdown
When `Stop()` is called or the window is closed:
1. `_isRunning` flag is set to false.
2. Decode thread breaks out of its loop and is safely joined.
3. Unmanaged FFmpeg pointers and GC pinned handles are freed in `Cleanup()`.
4. This strictly prevents memory leaks and Use-After-Free crashes.
