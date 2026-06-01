# JonPlayer

A modern, high-performance media player built with C#, WPF, and FFmpeg. JonPlayer features a sleek, hardware-accelerated user interface with dynamic glassmorphism aesthetics, designed to deliver a premium viewing and listening experience.

## Features

- **Hardware Acceleration:** Zero-copy GPU rendering utilizing D3D11VA and FFmpeg for ultra-smooth video playback with minimal CPU usage.
- **Sleek UI/UX:** 
  - Dynamic Glassmorphism UI (auto-adapts to media content).
  - Clean and modern controls overlay that auto-hides during playback.
  - Smooth micro-animations and aesthetic transitions.
- **Audio & Video Support:** 
  - Wide format support powered by FFmpeg (MP4, MKV, AVI, MP3, WAV, FLAC, etc.).
  - Automatic extraction and display of embedded Cover Art (ID3 tags) for audio files, elegantly blurred into the background.
- **Advanced Playback Controls:**
  - Highly responsive Seeking (Forward/Backward) perfectly synced between video and audio streams.
  - Playlist management (Auto-play next, Drag & Drop support).
  - Playback Speed Control (0.25x to 2.0x).
  - Volume control with mute functionality.
  - State memory (automatically remembers and restores the last played media after finishing).
- **Fullscreen & Multi-Monitor:** Intelligent fullscreen mode that properly fills the monitor where the window is currently located, with standard keyboard shortcuts.
- **Keyboard Shortcuts:**
  - `Space`: Play / Pause
  - `Esc`: Exit Fullscreen
  - `Up/Down`: Volume Control
  - `Left/Right`: Seek Backward / Forward
  - `F`: Toggle Fullscreen

## Prerequisites

- Windows OS
- .NET 8.0 SDK or Runtime
- FFmpeg 6.1 (or compatible) Shared Libraries (`.dll`s):
  - `avcodec-61.dll`
  - `avformat-61.dll`
  - `avutil-59.dll`
  - `swscale-8.dll`
  - `swresample-5.dll`
  *Note: These DLLs must be placed in the output directory or added to your system's PATH.*

## Getting Started

1. Clone the repository.
2. Ensure you have the required FFmpeg shared DLLs downloaded (from gyan.dev or BtbN).
3. Place the FFmpeg DLLs in the output directory (e.g., `bin/Debug/net8.0-windows/`).
4. Build and run the project using Visual Studio or the .NET CLI:
   ```bash
   dotnet build
   dotnet run --project JonPlayer
   ```

## Architecture

- `FFmpegMediaDecoder.cs`: Handles media decoding using FFmpeg.Interop. It spins up dedicated threads for reading packets, decoding video frames, and decoding audio frames to prevent UI blocking.
- `D3D11VideoRenderer.cs`: Manages DirectX 11 hardware context to securely map FFmpeg's decoded GPU frames directly into WPF's `D3DImage` without expensive CPU memory copies.
- `MainWindow.xaml`: The primary WPF window housing the MediaElement wrapper, custom controls, and Glassmorphism design system.
