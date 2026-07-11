using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;

namespace JonPlayer;

public class FFmpegMediaDecoder : IDisposable
{
	public enum SwVideoBufferLayout
	{
		None,
		Bgra,
		Nv12
	}

	public class DecodedVideoFrame : IDisposable
	{
		public IntPtr TexturePtr;

		public IntPtr BgraPointer;

		public IntPtr Nv12Pointer;

		public SwVideoBufferLayout BufferLayout;

		public int Width;

		public int Height;

		public int SliceIndexOrStride;

		public bool IsD3D11;

		public double PtsTime;

		public unsafe AVFrame* AvFrame;

		private int _disposed;

		public unsafe void Dispose()
		{
			// Idempotent: render teardown + PresentBlack may dispose the same held frame twice.
			if (Interlocked.Exchange(ref _disposed, 1) != 0)
			{
				return;
			}
			if (AvFrame != null)
			{
				fixed (AVFrame** frame = &AvFrame)
				{
					ffmpeg.av_frame_free(frame);
				}
				AvFrame = null;
			}
			TexturePtr = IntPtr.Zero;
			BgraPointer = IntPtr.Zero;
			Nv12Pointer = IntPtr.Zero;
		}
	}

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private unsafe delegate AVPixelFormat AVPixelFormat_get_format_func(AVCodecContext* s, AVPixelFormat* fmt);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private unsafe delegate int InterruptCallbackDelegate(void* opaque);

	public static bool EnableHwAccel;

	private static readonly ArrayPool<byte> _audioBufferPool;

	private static readonly string _seekLogPath;

	private unsafe AVFormatContext* _formatContext;

	private unsafe AVFormatContext* _audioFormatContext;

	private unsafe AVCodecContext* _videoCodecContext;

	private int _videoStreamIndex = -1;

	private int _width;

	private int _height;

	private unsafe SwsContext* _swsContext;

	private AVPixelFormat _swsSrcFormat = AVPixelFormat.AV_PIX_FMT_NONE;

	private unsafe SwsContext* _swsNv12Context;

	private AVPixelFormat _swsNv12SrcFormat = AVPixelFormat.AV_PIX_FMT_NONE;

	private unsafe byte*[]? _swsNv12DstData;

	private readonly int[] _swsNv12DstStrides = new int[8];

	private double _lastValidPtsTime;

	private double _baseAudioPtsMs = -1.0;

	private long _totalOutputSamples;

	private double _lastValidAudioPtsTime;

	private const int SwsBgraPoolSize = 15;

	/// <summary>SW NV12 rotating pool. Must stay larger than SW decoded queue + one held render frame.</summary>
	private const int SwNv12PoolSize = 16;

	/// <summary>Phase1 (#40): upload NV12/YUV420P to GPU instead of sws_scale→BGRA when true.</summary>
	public static bool EnableSwGpuYuvPath = true;

	// libswscale flags — see libavutil/pixdesc.h
	private const int SwsFlagBilinear = 2;
	private const int SwsFlagBicubic = 4;
	private const int SwsFlagArea = 0x20;
	private const int SwsFlagFullChrHInt = 0x2000;
	private const int SwsFlagFullChrHInp = 0x4000;
	private const int SwsFlagAccurateRnd = 0x40000;
	private IntPtr[] _swsBgraPointers = new IntPtr[SwsBgraPoolSize];
	private int _swsBgraBufferIndex = 0;
	private int _swsBgraBufferSize = 0;

	private IntPtr[] _swNv12Pointers = new IntPtr[SwNv12PoolSize];
	private int _swNv12BufferIndex = 0;
	private int _swNv12BufferSize = 0;


	// Pre-allocated to avoid per-frame 'new byte*[8]' / 'new int[8]' allocations in hot SW decode path
	private unsafe byte*[] _swsDstData;
	private readonly int[] _swsDstStrides = new int[8];

	/// <summary>
	/// Centralized free for SW BGRA native buffer pool. Safe to call multiple times.
	/// </summary>
	private unsafe void FreeSwsBgraPool()
	{
		for (int i = 0; i < SwsBgraPoolSize; i++)
		{
			if (_swsBgraPointers[i] != IntPtr.Zero)
			{
				NativeMemory.Free((void*)_swsBgraPointers[i]);
				_swsBgraPointers[i] = IntPtr.Zero;
			}
		}
		_swsBgraBufferSize = 0;
	}

	private unsafe void FreeSwNv12Pool()
	{
		for (int i = 0; i < SwNv12PoolSize; i++)
		{
			if (_swNv12Pointers[i] != IntPtr.Zero)
			{
				NativeMemory.Free((void*)_swNv12Pointers[i]);
				_swNv12Pointers[i] = IntPtr.Zero;
			}
		}
		_swNv12BufferSize = 0;
	}

	private static int GetNv12BufferSize(int width, int height) => width * height + width * (height / 2);

	private static bool CanCopyDirectToNv12(AVPixelFormat format)
	{
		return format == AVPixelFormat.AV_PIX_FMT_NV12
			|| format == AVPixelFormat.AV_PIX_FMT_YUV420P
			|| format == AVPixelFormat.AV_PIX_FMT_YUVJ420P;
	}

	private unsafe static bool CanSwsConvertToNv12(AVPixelFormat format)
	{
		if (format == AVPixelFormat.AV_PIX_FMT_NONE || IsHardwarePixelFormat(format))
		{
			return false;
		}
		AVPixFmtDescriptor* desc = ffmpeg.av_pix_fmt_desc_get(format);
		return desc != null && (desc->flags & ffmpeg.AV_PIX_FMT_FLAG_HWACCEL) == 0;
	}

	private unsafe static void CopyAvFrameToTightNv12(AVFrame* frame, byte* dst, int width, int height)
	{
		AVPixelFormat format = (AVPixelFormat)frame->format;
		if (format == AVPixelFormat.AV_PIX_FMT_NV12)
		{
			int yStride = frame->linesize[0];
			int uvStride = frame->linesize[1];
			for (int row = 0; row < height; row++)
			{
				Buffer.MemoryCopy(
					frame->data[0] + (nuint)(row * yStride),
					dst + (nuint)(row * width),
					width,
					width);
			}
			byte* dstUv = dst + (nuint)(width * height);
			int chromaHeight = height / 2;
			for (int row = 0; row < chromaHeight; row++)
			{
				Buffer.MemoryCopy(
					frame->data[1] + (nuint)(row * uvStride),
					dstUv + (nuint)(row * width),
					width,
					width);
			}
			return;
		}
		if (format == AVPixelFormat.AV_PIX_FMT_YUV420P || format == AVPixelFormat.AV_PIX_FMT_YUVJ420P)
		{
			int yStride = frame->linesize[0];
			int uStride = frame->linesize[1];
			int vStride = frame->linesize[2];
			for (int row = 0; row < height; row++)
			{
				Buffer.MemoryCopy(
					frame->data[0] + (nuint)(row * yStride),
					dst + (nuint)(row * width),
					width,
					width);
			}
			byte* dstUv = dst + (nuint)(width * height);
			int chromaHeight = height / 2;
			int chromaWidth = width / 2;
			// Interleave U/V 8 bytes at a time to cut per-pixel loop overhead (SW hot path).
			for (int row = 0; row < chromaHeight; row++)
			{
				byte* uRow = frame->data[1] + (nuint)(row * uStride);
				byte* vRow = frame->data[2] + (nuint)(row * vStride);
				byte* dstRow = dstUv + (nuint)(row * width);
				int col = 0;
				int packed = chromaWidth & ~7;
				for (; col < packed; col += 8)
				{
					dstRow[0] = uRow[0]; dstRow[1] = vRow[0];
					dstRow[2] = uRow[1]; dstRow[3] = vRow[1];
					dstRow[4] = uRow[2]; dstRow[5] = vRow[2];
					dstRow[6] = uRow[3]; dstRow[7] = vRow[3];
					dstRow[8] = uRow[4]; dstRow[9] = vRow[4];
					dstRow[10] = uRow[5]; dstRow[11] = vRow[5];
					dstRow[12] = uRow[6]; dstRow[13] = vRow[6];
					dstRow[14] = uRow[7]; dstRow[15] = vRow[7];
					uRow += 8;
					vRow += 8;
					dstRow += 16;
				}
				for (; col < chromaWidth; col++)
				{
					*dstRow++ = *uRow++;
					*dstRow++ = *vRow++;
				}
			}
		}
	}

	private unsafe bool TrySwsScaleToTightNv12(AVFrame* src, byte* dst, int width, int height)
	{
		AVPixelFormat srcFormat = (AVPixelFormat)src->format;
		if (!CanSwsConvertToNv12(srcFormat))
		{
			return false;
		}
		if (_swsNv12Context == null || _swsNv12SrcFormat != srcFormat)
		{
			if (_swsNv12Context != null)
			{
				ffmpeg.sws_freeContext(_swsNv12Context);
				_swsNv12Context = null;
			}
			int swsFlags = GetSwsFlags(src->width, src->height, width, height);
			_swsNv12Context = ffmpeg.sws_getContext(
				src->width, src->height, srcFormat,
				width, height, AVPixelFormat.AV_PIX_FMT_NV12,
				swsFlags, null, null, null);
			_swsNv12SrcFormat = srcFormat;
		}
		if (_swsNv12Context == null)
		{
			return false;
		}
		if (_swsNv12DstData == null)
		{
			_swsNv12DstData = new byte*[8];
		}
		_swsNv12DstData[0] = dst;
		_swsNv12DstData[1] = dst + (nuint)(width * height);
		_swsNv12DstStrides[0] = width;
		_swsNv12DstStrides[1] = width;
		return ffmpeg.sws_scale(
			_swsNv12Context,
			src->data, src->linesize,
			0, src->height,
			_swsNv12DstData, _swsNv12DstStrides) >= 0;
	}

	private unsafe bool TryFillGpuNv12DecodedFrame(AVFrame* src, DecodedVideoFrame decodedVideoFrame)
	{
		if (!EnableSwGpuYuvPath)
		{
			return false;
		}
		AVPixelFormat srcFormat = (AVPixelFormat)src->format;
		bool directCopy = CanCopyDirectToNv12(srcFormat);
		bool swsConvert = !directCopy && CanSwsConvertToNv12(srcFormat);
		if (!directCopy && !swsConvert)
		{
			return false;
		}
		int width = src->width;
		int height = src->height;
		if (width <= 0 || height <= 0)
		{
			return false;
		}
		int nv12Bytes = GetNv12BufferSize(width, height);
		if (_swNv12BufferSize < nv12Bytes)
		{
			FreeSwNv12Pool();
			for (int i = 0; i < SwNv12PoolSize; i++)
			{
				_swNv12Pointers[i] = (IntPtr)NativeMemory.Alloc((nuint)nv12Bytes);
			}
			_swNv12BufferSize = nv12Bytes;
		}
		_swNv12BufferIndex = (_swNv12BufferIndex + 1) % SwNv12PoolSize;
		IntPtr nv12Ptr = _swNv12Pointers[_swNv12BufferIndex];
		bool filled;
		if (directCopy)
		{
			CopyAvFrameToTightNv12(src, (byte*)nv12Ptr, width, height);
			filled = true;
		}
		else
		{
			filled = TrySwsScaleToTightNv12(src, (byte*)nv12Ptr, width, height);
		}
		if (!filled)
		{
			return false;
		}
		decodedVideoFrame.BufferLayout = SwVideoBufferLayout.Nv12;
		decodedVideoFrame.Nv12Pointer = nv12Ptr;
		decodedVideoFrame.BgraPointer = IntPtr.Zero;
		decodedVideoFrame.Width = width;
		decodedVideoFrame.Height = height;
		decodedVideoFrame.SliceIndexOrStride = width;
		return true;
	}

private unsafe AVCodecContext* _audioCodecContext;

	private int _audioStreamIndex = -1;

	private unsafe SwrContext* _swrContext;

	private byte[]? _audioBuffer;

	private GCHandle _audioBufferHandle;

	private IntPtr _audioBufferPointer;

	private int _audioMaxBufferSize;

	private unsafe AVFilterGraph* _audioFilterGraph;

	private unsafe AVFilterContext* _abufferCtx;

	private unsafe AVFilterContext* _atempoCtx;

	private unsafe AVFilterContext* _abuffersinkCtx;

	private unsafe AVFrame* _filteredAudioFrame;

	private unsafe AVFrame* _audioFrame;

	private unsafe AVFrame* _videoFrame;

	private unsafe AVPacket* _packet;

	private unsafe AVFrame* _previousD3D11Frame;

	private Thread? _readThread;

	private Thread? _videoThread;

	private Thread? _audioThread;

	private ConcurrentQueue<IntPtr> _videoPacketQueue = new ConcurrentQueue<IntPtr>();
	private volatile int _videoPacketQueueSizeBytes;
	private ManualResetEventSlim _videoPacketAvailableEvent = new ManualResetEventSlim(false);

	private ConcurrentQueue<IntPtr> _audioPacketQueue = new ConcurrentQueue<IntPtr>();
	private volatile int _audioPacketQueueSizeBytes;
	private ManualResetEventSlim _audioPacketAvailableEvent = new ManualResetEventSlim(false);

	private ConcurrentBag<IntPtr> _packetPool = new ConcurrentBag<IntPtr>();

	private ConcurrentBag<IntPtr> _framePool = new ConcurrentBag<IntPtr>();

	private ConcurrentQueue<DecodedVideoFrame> _decodedVideoQueue = new ConcurrentQueue<DecodedVideoFrame>();

	private double _avStartOffsetMs;

	private bool _syncAvOffsetFromStreamStart = true;

	// Stream timeline alignment only (firstVideo - firstAudio). NEVER used to "zero" AvDiff —
	// walking offset makes AvDiff look synced while lips are wrong (offset absorbs the error).
	private double _emaAvDiffMs;

	/// <summary>True when video is CPU-decoded (no D3D11 frames).</summary>
	private volatile bool _isSoftwareVideoDecode;

	// SW startup only: wait for a few decoded frames before first paint (does NOT touch audio PCM).
	private const int SwStartupMinDecodedFrames = 3;

	private enum SeekPhase { None, Active }

	private volatile SeekPhase _seekPhase = SeekPhase.None;

	private double _seekRequestedMs = -1.0;

	private double _seekLandPtsMs = -1.0;

	private volatile bool _seekVideoLanded;

	private volatile bool _seekAudioLanded;

	private const double SeekAudioLeadToleranceMs = 50.0;

	private volatile bool _suppressAudioOutput;

	private volatile bool _awaitingPostSeekVideoDisplay;
	private double _postSeekTargetMs = -1.0;

	private volatile int _videoDecodeWarmup;

	// Audio-only UI clock: anchored to PCM actually submitted to WaveOut.
	private double _audioOutputAnchorPtsMs = -1.0;

	private long _audioOutputSamplesSubmitted;

	private double _audioOutputEndPtsMs = -1.0;

	private double _lastDisplayedVideoPtsMs = -1.0;

	private volatile bool _isFirstVideoFrame = true;

	private volatile bool _isFirstAudioFrame = true;

	private volatile bool _isPreBuffering;
	private DateTime _postSeekReducedPrebufferUntil = DateTime.MinValue;

	private double _firstAudioPtsMs = -1.0;

	private double _firstVideoPtsMs = -1.0;

	private volatile bool _lastDecodedFrameIsD3D11;

	public bool LastDecodedFrameIsHardware => _lastDecodedFrameIsD3D11;

	private volatile bool _isRunning;

	private volatile bool _isPaused;

	private readonly object _lock = new object();

	private Stopwatch _masterClockStopwatch = new Stopwatch();

	private double _currentPlaybackPtsTime;

	private string? _currentPath;

	private string? _separateAudioUrl;

	private double _seekTargetMs = -1.0;
	private int _seekGeneration;
	private int _activeSeekGen;

	// === SEEK STABILIZATION & OPTIMIZATION NOTES ===
	// - _seekGeneration + _activeSeekGen: rapid consecutive seeks cancel older ones' landing.
	// - small forward seek: selective PTS clean of decoded frames (keep future work).
	// - _postSeekReducedPrebufferUntil: shorter prebuffer after seek for snappier resume.
	// - Land on first video frame (keyframe) for fast completion, but UI clock prefers user target.
	// - Do not extend "isSeekingVideo" state longer than necessary (affects queueing and FPS).
	// These were added to make seek both accurate and stable. Protect them.

	private volatile bool _isFinished;

	private volatile bool _notifiedPlaybackFinished;

	private volatile bool _videoEof;

	private AVPixelFormat_get_format_func? _getFormatCallback;

	private double _playbackSpeed = 1.0;

	private volatile bool _speedChanged;

	private int _needsVideoFlush;

	private int _needsAudioFlush;

	private volatile bool _isSeekingVideo;

	private volatile bool _isSeekingAudio;



	private bool _isDisposed;

	private int _isOpening;

	private volatile bool _videoFiltersChanged;

	private volatile bool _rebuildAudioFilters;

	private InterruptCallbackDelegate? _interruptCallback;

	private volatile bool _isInterruptRequested;

	private DecoderStats _stats;

	private int _framesDecodedThisSecond;

	private DateTime _lastFpsCalcTime;

	private double _fpsMeasureLastPtsMs = -1.0;

	private double _streamFpsBaseline = 24.0;

	private const double FpsRefineSmoothingAlpha = 0.18;

	private const double NtscFilmFps = 24000.0 / 1001.0;

	private const double NtscVideoFps = 30000.0 / 1001.0;

	private const double NtscDoubleFps = 60000.0 / 1001.0;

	private double _totalDecodeTimeMs;

	private int _decodeTimeSamples;

	private int _packetsReadThisSecond;

	private int _audioFramesDecodedThisSecond;

	private DateTime _lastReaderFpsCalcTime = DateTime.UtcNow;

	private DateTime _lastAudioFpsCalcTime = DateTime.UtcNow;

	private double _lastVideoDecodeTimeMs;

	private int _loggedSwBgraFallbackFormat = int.MinValue;

	private double _lastAudioDecodeTimeMs;

	private long _droppedFrameCount;

	private int _lateFrameCount;

	private double _lastPoolWaitMs;

	private IntPtr _d3d11DevicePtr;

	private IntPtr _d3d11ContextPtr;

	private int _activeThreads;

	private int _isCleanedUp;

	private int _disposeState;

	public int AudioSampleRate { get; private set; }

	public int AudioChannels { get; private set; }

	public bool IsRunning => _isRunning;

	public bool IsPaused => _isPaused;

	public bool IsSeekActive => _seekPhase == SeekPhase.Active;

	public Func<double>? GetAudioBufferedDurationMs { get; set; }

	public Func<double>? GetAudioHardwareLatencyMs { get; set; }

	public unsafe double DurationSeconds
	{
		get
		{
			if (_formatContext == null)
			{
				return 0.0;
			}
			return (double)_formatContext->duration / 1000000.0;
		}
	}

	public int Width => _width;

	public int Height => _height;

	public double PlaybackSpeed => _playbackSpeed;

	public double AudioVolumeLevel { get; private set; } = 1.0;


	public double AudioVocalGain { get; private set; }

	public double VideoBrightness { get; private set; }

	public double VideoContrast { get; private set; } = 1.0;


	public double VideoSaturation { get; private set; } = 1.0;


	public bool IsPlaying
	{
		get
		{
			if (_isRunning)
			{
				return !_isPaused;
			}
			return false;
		}
	}

	public bool IsFinished => _isFinished;

	public bool HasVideo => _videoStreamIndex != -1;

	public int DecodedVideoFrameCount => _decodedVideoQueue.Count;

	public bool IsSeekIdle
	{
		get
		{
			lock (_lock)
			{
				return _seekPhase == SeekPhase.None && _seekTargetMs < 0.0;
			}
		}
	}



	public void BeginVideoDecodeWarmup() => Interlocked.Exchange(ref _videoDecodeWarmup, 1);

	public void EndVideoDecodeWarmup() => Interlocked.Exchange(ref _videoDecodeWarmup, 0);

	public event Action<IntPtr, int, int, int, bool>? FrameDecoded;

	public event Action<byte[], int, double>? AudioDataAvailable;

	public event Action<double>? RotationDetected;

	public event Action<double>? PositionChanged;

	public event Action<TimeSpan, TimeSpan>? TimeUpdated;

	public event Action? PlaybackFinished;

	public event Action? SeekPerformed;

	public event Action? SeekInitiated;

	private static void SeekLog(string msg)
	{
		try
		{
			File.AppendAllText(_seekLogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
		}
		catch
		{
		}
	}

	private unsafe AVPacket* GetPacket()
	{
		if (_packetPool.TryTake(out var result))
		{
			AVPacket* ptr = (AVPacket*)result;
			ffmpeg.av_packet_unref(ptr);
			return ptr;
		}
		return ffmpeg.av_packet_alloc();
	}

	private unsafe void ReturnPacket(AVPacket* pkt)
	{
		ffmpeg.av_packet_unref(pkt);
		if (_packetPool.Count < 32)
			_packetPool.Add((nint)pkt);
		else
			ffmpeg.av_packet_free(&pkt);
	}

	private unsafe AVFrame* GetFrame()
	{
		if (_framePool.TryTake(out var result))
		{
			AVFrame* ptr = (AVFrame*)result;
			ffmpeg.av_frame_unref(ptr);
			return ptr;
		}
		return ffmpeg.av_frame_alloc();
	}

	private unsafe void ReturnFrame(AVFrame* frame)
	{
		ffmpeg.av_frame_unref(frame);
		if (_framePool.Count < 16)
			_framePool.Add((nint)frame);
		else
			ffmpeg.av_frame_free(&frame);
	}

	public double TargetFps => _stats.TargetFps;

	public double AvStartOffsetMs => GetAvStartOffsetMs();

	public double GetCurrentTimeMs() { return GetMasterClockPts(); }

	public bool ConsumePendingAudioBufferClear() => true;

	public double GetAudioPlayheadPts()
	{
		if (_audioStreamIndex == -1 || _firstAudioPtsMs < 0.0)
		{
			return double.NaN;
		}
		double bufferedMs = GetAudioBufferedDurationMs?.Invoke() ?? 0.0;
		// WaveOut DesiredLatency overlaps BufferedDuration; only a small device residual.
		double hwLatency = GetAudioHardwareLatencyMs?.Invoke() ?? 0.0;
		double deviceResidualMs = Math.Clamp(hwLatency * 0.20, 15.0, 45.0);
		double playhead = _lastValidAudioPtsTime - (bufferedMs + deviceResidualMs) * _playbackSpeed;
		return playhead < 0.0 ? 0.0 : playhead;
	}

	/// <summary>
	/// Signed A/V error in master(audio) domain.
	/// Positive = audio ahead of displayed video (video late / needs catch-up).
	/// </summary>
	public double ComputeAvDiffMs(double videoPtsMs)
	{
		if (_audioStreamIndex == -1 || _lastDisplayedVideoPtsMs < 0.0 && videoPtsMs < 0.0)
		{
			return 0.0;
		}
		double audioPlayhead = GetAudioPlayheadPts();
		if (double.IsNaN(audioPlayhead))
		{
			return 0.0;
		}
		double v = videoPtsMs >= 0.0 ? videoPtsMs : _lastDisplayedVideoPtsMs;
		if (v < 0.0)
		{
			return 0.0;
		}
		return audioPlayhead - (v - GetAvStartOffsetMs());
	}

	public bool IsSoftwareVideoDecode => _isSoftwareVideoDecode;

	private bool IsAudioOnlyPlayback => _videoStreamIndex == -1 && _audioStreamIndex != -1;

	private int GetSoftwareDecodedFrameQueueCap()
	{
		int pixels = _width * _height;
		// Jitter buffer for SW (pool is 16). Never starve presentation by being too small.
		if (pixels > 3840 * 2160)
		{
			return 8;
		}
		if (pixels > 1920 * 1080)
		{
			return 10;
		}
		return 10;
	}

	public void ReleasePostSeekPlayback()
	{
		lock (_lock)
		{
			if (IsAudioOnlyPlayback)
			{
				_suppressAudioOutput = false;
			}
		}
	}

	public void NotifyAudioSamplesSubmitted(int sampleCount, double chunkEndPtsMs)
	{
		if (!IsAudioOnlyPlayback || sampleCount <= 0 || AudioSampleRate <= 0)
		{
			return;
		}
		if (_audioOutputAnchorPtsMs < 0.0)
		{
			double chunkMs = sampleCount * 1000.0 / AudioSampleRate;
			_audioOutputAnchorPtsMs = Math.Max(0.0, chunkEndPtsMs - chunkMs);
			_audioOutputSamplesSubmitted = 0L;
		}
		_audioOutputSamplesSubmitted += sampleCount;
		_audioOutputEndPtsMs = _audioOutputAnchorPtsMs
			+ _audioOutputSamplesSubmitted * 1000.0 / AudioSampleRate * _playbackSpeed;
	}

	private void ResetAudioOutputClock(double anchorPtsMs)
	{
		_audioOutputAnchorPtsMs = anchorPtsMs;
		_audioOutputSamplesSubmitted = 0L;
		_audioOutputEndPtsMs = -1.0;
	}

	private void ReleasePostSeekAudioOutput(double videoPtsMs)
	{
		if (!_suppressAudioOutput || !HasVideo)
		{
			return;
		}
		// First picture is on screen — start audio. Do NOT gate/throttle PCM quality after this.
		_suppressAudioOutput = false;
		_awaitingPostSeekVideoDisplay = false;
		if (!_syncAvOffsetFromStreamStart)
		{
			SetAvStartOffsetMs(0.0);
		}
		SeekLog($"[AV_START_RELEASE] videoPts={videoPtsMs:F0} sw={_isSoftwareVideoDecode} offset={GetAvStartOffsetMs():F0}");
	}

	private double GetAudioOutputPlayheadPts()
	{
		if (_audioOutputEndPtsMs < 0.0)
		{
			return double.NaN;
		}
		double bufferedMs = GetAudioBufferedDurationMs?.Invoke() ?? 0.0;
		double hwLatency = GetAudioHardwareLatencyMs?.Invoke() ?? 0.0;
		double playhead = _audioOutputEndPtsMs - (bufferedMs + hwLatency) * _playbackSpeed;
		return playhead < 0.0 ? 0.0 : playhead;
	}

	private double GetAudioOnlyUiClockPts()
	{
		double outputPlayhead = GetAudioOutputPlayheadPts();
		if (!double.IsNaN(outputPlayhead))
		{
			return outputPlayhead;
		}
		return _currentPlaybackPtsTime;
	}

	private double GetStopwatchClockPts()
	{
		if (_masterClockStopwatch.IsRunning)
		{
			return _currentPlaybackPtsTime + (double)_masterClockStopwatch.ElapsedMilliseconds * _playbackSpeed;
		}
		return _currentPlaybackPtsTime;
	}

	public double GetMasterClockPts()
	{
		if (_isPaused)
		{
			return _currentPlaybackPtsTime;
		}
		if (_seekPhase == SeekPhase.Active)
		{
			if (_seekLandPtsMs >= 0.0)
			{
				return _seekLandPtsMs;
			}
			return _currentPlaybackPtsTime;
		}
		if (IsAudioOnlyPlayback)
		{
			return GetAudioOnlyUiClockPts();
		}
		if (_audioStreamIndex != -1 && !_isSeekingAudio)
		{
			double audioPlayhead = GetAudioPlayheadPts();
			if (!double.IsNaN(audioPlayhead))
			{
				return audioPlayhead;
			}
		}
		return GetStopwatchClockPts();
	}

	private double CapturePlaybackPtsMs()
	{
		if (IsAudioOnlyPlayback)
		{
			return GetAudioOnlyUiClockPts();
		}
		if (_audioStreamIndex != -1 && !_isSeekingAudio)
		{
			double audioPlayhead = GetAudioPlayheadPts();
			if (!double.IsNaN(audioPlayhead))
			{
				return audioPlayhead;
			}
		}
		return GetStopwatchClockPts();
	}

	/// <summary>
	/// Common origin for A/V (container start). Using per-stream start_time for each stream
	/// independently made Offset absorb multi-second fake "sync" (Diff≈0 while lips wrong).
	/// </summary>
	private unsafe static double GetContainerStartOffsetMs(AVFormatContext* formatContext)
	{
		if (formatContext == null)
		{
			return 0.0;
		}
		if (formatContext->start_time != ffmpeg.AV_NOPTS_VALUE)
		{
			// AV_TIME_BASE = 1_000_000
			return (double)formatContext->start_time * 1000.0 / 1000000.0;
		}
		// Fallback: earliest stream start on the container.
		double minStart = double.MaxValue;
		for (uint i = 0; i < formatContext->nb_streams; i++)
		{
			AVStream* st = formatContext->streams[i];
			if (st != null && st->start_time != ffmpeg.AV_NOPTS_VALUE)
			{
				double ms = (double)st->start_time * ffmpeg.av_q2d(st->time_base) * 1000.0;
				if (ms < minStart)
				{
					minStart = ms;
				}
			}
		}
		return minStart < double.MaxValue ? minStart : 0.0;
	}

	private unsafe static double GetStreamStartOffsetMs(AVFormatContext* formatContext, int streamIndex)
	{
		// Kept for seek helpers that still need stream-local mapping.
		if (formatContext == null || streamIndex < 0 || streamIndex >= formatContext->nb_streams)
		{
			return 0.0;
		}
		AVStream* ptr = formatContext->streams[streamIndex];
		if (ptr != null && ptr->start_time != ffmpeg.AV_NOPTS_VALUE)
		{
			return (double)ptr->start_time * ffmpeg.av_q2d(ptr->time_base) * 1000.0;
		}
		return GetContainerStartOffsetMs(formatContext);
	}

	/// <summary>
	/// Media time in ms on a SHARED container timeline (same origin for audio and video).
	/// </summary>
	private unsafe static double GetNormalizedPtsMs(long pts, AVFormatContext* formatContext, int streamIndex)
	{
		if (formatContext == null || streamIndex < 0 || streamIndex >= formatContext->nb_streams || pts == ffmpeg.AV_NOPTS_VALUE)
		{
			return 0.0;
		}
		AVStream* ptr = formatContext->streams[streamIndex];
		if (ptr == null)
		{
			return 0.0;
		}
		double absoluteMs = (double)pts * ffmpeg.av_q2d(ptr->time_base) * 1000.0;
		return absoluteMs - GetContainerStartOffsetMs(formatContext);
	}

	private unsafe static long GetSeekTimestamp(AVFormatContext* formatContext, double targetMs)
	{
		return ((formatContext != null && formatContext->start_time != ffmpeg.AV_NOPTS_VALUE) ? formatContext->start_time : 0) + (long)(targetMs / 1000.0 * 1000000.0);
	}

	private unsafe static long GetStreamSeekTimestamp(AVFormatContext* formatContext, int streamIndex, double targetMs)
	{
		if (formatContext == null || streamIndex < 0 || streamIndex >= formatContext->nb_streams)
		{
			return ffmpeg.AV_NOPTS_VALUE;
		}
		AVStream* ptr = formatContext->streams[streamIndex];
		if (ptr == null)
		{
			return ffmpeg.AV_NOPTS_VALUE;
		}
		return (long)((targetMs + GetStreamStartOffsetMs(formatContext, streamIndex)) / 1000.0 / ffmpeg.av_q2d(ptr->time_base));
	}

	private unsafe static int SeekFormatContext(AVFormatContext* formatContext, int streamIndex, double targetMs)
	{
		long streamSeekTimestamp = GetStreamSeekTimestamp(formatContext, streamIndex, targetMs);
		if (streamSeekTimestamp != ffmpeg.AV_NOPTS_VALUE)
		{
			int num = ffmpeg.av_seek_frame(formatContext, streamIndex, streamSeekTimestamp, 1);
			if (num >= 0)
			{
				return num;
			}
			num = ffmpeg.avformat_seek_file(formatContext, streamIndex, long.MinValue, streamSeekTimestamp, streamSeekTimestamp, 1);
			if (num >= 0)
			{
				return num;
			}
		}
		long seekTimestamp = GetSeekTimestamp(formatContext, targetMs);
		return ffmpeg.avformat_seek_file(formatContext, -1, long.MinValue, seekTimestamp, seekTimestamp, 1);
	}

	private int GetVideoPacketQueueLimit()
	{
		int pixels = _width * _height;
		int baseLimit = 300;
		if (pixels > 3840 * 2160)
		{
			baseLimit = 20;
		}
		else if (pixels > 1920 * 1080)
		{
			baseLimit = 30;
		}
		if (DateTime.UtcNow < _postSeekReducedPrebufferUntil)
		{
			return Math.Max(5, baseLimit / 5); // smaller during post-seek
		}
		return baseLimit;
	}

	private int GetAudioPacketQueueLimit()
	{
		int pixels = _width * _height;
		int baseLimit = 600;
		if (pixels > 3840 * 2160)
		{
			baseLimit = 60;
		}
		else if (pixels > 1920 * 1080)
		{
			baseLimit = 100;
		}
		if (DateTime.UtcNow < _postSeekReducedPrebufferUntil)
		{
			return Math.Max(10, baseLimit / 5);
		}
		return baseLimit;
	}

	private int GetVideoPrebufferTarget()
	{
		int limit = GetVideoPacketQueueLimit();
		if (DateTime.UtcNow < _postSeekReducedPrebufferUntil)
		{
			return 4; // temporarily small after seek for faster resume
		}
		return Math.Max(4, limit / 4);
	}

	private int GetAudioPrebufferTarget()
	{
		int limit = GetAudioPacketQueueLimit();
		if (DateTime.UtcNow < _postSeekReducedPrebufferUntil)
		{
			return 8;
		}
		return Math.Max(8, limit / 4);
	}

	private int GetDecodedFrameQueueLimit()
	{
		int pixels = _width * _height;
		if (pixels > 3840 * 2160)
		{
			return 8;
		}
		if (pixels > 1920 * 1080)
		{
			return 10;
		}
		return 12;
	}

	/// <summary>
	/// Pick swscale flags for the SW BGRA path. Higher resolutions and downscales
	/// use stronger filters; full-chroma interpolation reduces banding on YUV sources.
	/// </summary>
	private static int GetSwsFlags(int srcWidth, int srcHeight, int dstWidth, int dstHeight)
	{
		int chromaFlags = SwsFlagFullChrHInt | SwsFlagFullChrHInp;
		long srcPixels = (long)srcWidth * srcHeight;
		long dstPixels = (long)dstWidth * dstHeight;
		bool downscaling = dstPixels < srcPixels;

		if (dstPixels > 3840L * 2160)
		{
			return SwsFlagBicubic | chromaFlags | SwsFlagAccurateRnd;
		}
		if (dstPixels > 1920L * 1080)
		{
			return SwsFlagBicubic | chromaFlags;
		}
		if (downscaling)
		{
			return SwsFlagArea | chromaFlags;
		}
		return SwsFlagBilinear | chromaFlags;
	}

	private double GetAudioDecodeBufferTargetMs()
	{
		// Smooth audio always — never shrink buffer to "fix" video lag (causes crackle).
		int pixels = _width * _height;
		if (pixels > 3840 * 2160)
		{
			return 600.0;
		}
		if (pixels > 1920 * 1080)
		{
			return 800.0;
		}
		return 1000.0;
	}

	private unsafe int InterruptCallback(void* opaque)
	{
		return _isInterruptRequested ? 1 : 0;
	}

	public DecoderStats GetStats()
	{
		_stats.PacketQueueSize = _videoPacketQueue.Count;
		_stats.AudioQueueSize = _audioPacketQueue.Count;
		_stats.VideoPacketQueueSize = _videoPacketQueue.Count;
		_stats.AudioPacketQueueSize = _audioPacketQueue.Count;
		_stats.VideoFrameQueueSize = _decodedVideoQueue.Count;
		_stats.AudioFrameQueueSize = 0;
		_stats.IsRealHwAccel = _lastDecodedFrameIsD3D11;
		_stats.DecoderMode = ResolveDecoderModeLabel();
		_stats.VideoDecodeFps = _stats.ActualFps;
		double displayedVideoPts = _lastDisplayedVideoPtsMs >= 0.0 ? _lastDisplayedVideoPtsMs : _lastValidPtsTime;
		_stats.VideoPts = displayedVideoPts;
		_stats.VideoDecodePts = _lastValidPtsTime;
		_stats.DecodeLeadMs = _lastValidPtsTime - displayedVideoPts;
		_stats.VideoDecodeTimeMs = _lastVideoDecodeTimeMs;
		_stats.AudioDecodeTimeMs = _lastAudioDecodeTimeMs;
		_stats.DroppedFrames = _droppedFrameCount;
		_stats.LateFrames = _lateFrameCount;
		_stats.SurfacePoolWaitTimeMs = _lastPoolWaitMs;

		bool hasBothStreams = _videoStreamIndex != -1 && _audioStreamIndex != -1;
		if (hasBothStreams && _lastValidAudioPtsTime > 0.0 && _lastDisplayedVideoPtsMs >= 0.0)
		{
			double audioPlayhead = GetAudioPlayheadPts();
			_stats.AudioPts = audioPlayhead;
			// AvDiff = clock-domain error (with stream offset). RawGap = content PTS gap (video - audio).
			// If Offset is wrong, Diff can look ~0 while RawGap is huge — trust RawGap for lips.
			double instant = ComputeAvDiffMs(displayedVideoPts);
			_stats.AvDiffMs = instant;
			_stats.AvSyncMs = displayedVideoPts - (double.IsNaN(audioPlayhead) ? 0.0 : audioPlayhead); // raw V-A gap
			_stats.SyncDelayMs = GetAvStartOffsetMs();
		}
		else
		{
			_stats.AudioPts = (_audioStreamIndex != -1 ? GetAudioPlayheadPts() : 0);
			_stats.AvDiffMs = 0;
			_stats.AvSyncMs = 0;
			_stats.SyncDelayMs = GetAvStartOffsetMs();
		}

		_stats.MasterClock = GetMasterClockPts();
		return _stats;
	}

	static FFmpegMediaDecoder()
	{
		EnableHwAccel = true;
		_audioBufferPool = ArrayPool<byte>.Shared;
		_seekLogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "seek_debug.log");
		ffmpeg.RootPath = AppContext.BaseDirectory;
	}

	public void SetD3D11Device(IntPtr devicePtr, IntPtr contextPtr)
	{
		_d3d11DevicePtr = devicePtr;
		_d3d11ContextPtr = contextPtr;
	}

	public unsafe void Open(string path, string? audioUrl = null, double initialSeekRatio = 0.0)
	{
		if (_isDisposed)
		{
			throw new ObjectDisposedException("FFmpegMediaDecoder");
		}
		Stop();
		JoinWorkerThreads(5000);
		int waitMs = 0;
		while ((Interlocked.CompareExchange(ref _activeThreads, 0, 0) > 0 || Interlocked.CompareExchange(ref _isOpening, 0, 0) == 1) && waitMs < 5000)
		{
			Thread.Sleep(10);
			waitMs += 10;
		}
		bool threadsIdle = Interlocked.CompareExchange(ref _activeThreads, 0, 0) == 0
			&& Interlocked.CompareExchange(ref _isOpening, 0, 0) == 0;
		if (!threadsIdle)
		{
			Logger.Warn("Decoder open wait timed out while FFmpeg work is still active; joining worker threads before cleanup.");
			JoinWorkerThreads(2000);
			threadsIdle = Interlocked.CompareExchange(ref _activeThreads, 0, 0) == 0;
		}
		EnsureReleasedBeforeOpen(threadsIdle);
		Interlocked.Exchange(ref _isCleanedUp, 0);
		Interlocked.Exchange(ref _isOpening, 1);
		_currentPath = path;
		_separateAudioUrl = audioUrl;
		_videoStreamIndex = -1;
		_audioStreamIndex = -1;
		_isInterruptRequested = false;
		_isFirstVideoFrame = true;
		_isFirstAudioFrame = true;
		_isPreBuffering = true;
		_firstAudioPtsMs = -1.0;
		_firstVideoPtsMs = -1.0;
		_avStartOffsetMs = 0.0;
		_emaAvDiffMs = 0.0;
		_isSoftwareVideoDecode = false;
		_videoDecodeWarmup = 0;
		_syncAvOffsetFromStreamStart = true;
		_lastDisplayedVideoPtsMs = -1.0;
		_seekTargetMs = -1.0;
		_seekPhase = SeekPhase.None;
		_seekRequestedMs = -1.0;
		_postSeekTargetMs = -1.0;
		_postSeekReducedPrebufferUntil = DateTime.MinValue;
		_seekLandPtsMs = -1.0;
		_postSeekTargetMs = -1.0;
		_postSeekReducedPrebufferUntil = DateTime.MinValue;
		_seekVideoLanded = false;
		_seekAudioLanded = false;
		_suppressAudioOutput = false;
		_awaitingPostSeekVideoDisplay = false;
		_audioOutputAnchorPtsMs = -1.0;
		_audioOutputSamplesSubmitted = 0L;
		_audioOutputEndPtsMs = -1.0;
		_isSeekingVideo = false;
		_isSeekingAudio = false;

		_notifiedPlaybackFinished = false;
		_currentPlaybackPtsTime = 0.0;
		_lastValidPtsTime = 0.0;
		_lastValidAudioPtsTime = 0.0;
		_baseAudioPtsMs = -1.0;
		_isFinished = false;
		_masterClockStopwatch.Reset();
		_packetsReadThisSecond = 0;
		_audioFramesDecodedThisSecond = 0;
		_droppedFrameCount = 0;
		_lateFrameCount = 0;
		_lastReaderFpsCalcTime = DateTime.UtcNow;
		_lastAudioFpsCalcTime = DateTime.UtcNow;
		_lastVideoDecodeTimeMs = 0.0;
		_lastAudioDecodeTimeMs = 0.0;
		_lastPoolWaitMs = 0.0;
		_lastDecodedFrameIsD3D11 = false;
		try
		{
			_formatContext = ffmpeg.avformat_alloc_context();
			_interruptCallback = InterruptCallback;
			_formatContext->interrupt_callback.callback = new AVIOInterruptCB_callback_func
			{
				Pointer = Marshal.GetFunctionPointerForDelegate(_interruptCallback)
			};
			_formatContext->interrupt_callback.opaque = null;
			_formatContext->probesize = 5000000L;
			_formatContext->max_analyze_duration = 2000000L;
			AVDictionary** ptr = null;
			AVDictionary* ptr2 = null;
			if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
			{
				ffmpeg.av_dict_set(&ptr2, "reconnect", "1", 0);
				ffmpeg.av_dict_set(&ptr2, "reconnect_streamed", "1", 0);
				ffmpeg.av_dict_set(&ptr2, "reconnect_delay_max", "5", 0);
				ffmpeg.av_dict_set(&ptr2, "reconnect_on_network_error", "1", 0);
				ffmpeg.av_dict_set(&ptr2, "reconnect_on_http_error", "4xx,5xx", 0);
				ffmpeg.av_dict_set(&ptr2, "rw_timeout", "10000000", 0);
				ffmpeg.av_dict_set(&ptr2, "seekable", "1", 0);
				ffmpeg.av_dict_set(&ptr2, "user_agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36", 0);
			}
			fixed (AVFormatContext** ps = &_formatContext)
			{
				if (ffmpeg.avformat_open_input(ps, path, null, &ptr2) < 0)
				{
					_formatContext = null;
					if (ptr2 != null)
					{
						ffmpeg.av_dict_free(&ptr2);
					}
					throw new Exception("Could not open file");
				}
			}
			if (ptr2 != null)
			{
				ffmpeg.av_dict_free(&ptr2);
			}
			if (ffmpeg.avformat_find_stream_info(_formatContext, null) < 0)
			{
				throw new Exception("Could not find stream info");
			}
			if (!path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
			{
				long seekTimestamp = GetSeekTimestamp(_formatContext, 0.0);
				ffmpeg.avformat_seek_file(_formatContext, -1, long.MinValue, seekTimestamp, seekTimestamp, 1);
			}
			for (int i = 0; i < _formatContext->nb_streams; i++)
			{
				if (_formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && _videoStreamIndex == -1)
				{
					if ((_formatContext->streams[i]->disposition & 0x400) == 0)
					{
						_videoStreamIndex = i;
					}
				}
				else if (_formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && _audioStreamIndex == -1)
				{
					_audioStreamIndex = i;
				}
			}
			if (_videoStreamIndex == -1 && _audioStreamIndex == -1)
			{
				throw new Exception("Could not find any video or audio stream");
			}
			double num = -1.0;
			if (initialSeekRatio > 0.0 && _formatContext->duration > 0)
			{
				num = Math.Clamp(initialSeekRatio, 0.0, 1.0) * (double)_formatContext->duration * 1000.0 / 1000000.0;
				int streamIndex = ((_videoStreamIndex != -1) ? _videoStreamIndex : _audioStreamIndex);
				int num2 = SeekFormatContext(_formatContext, streamIndex, num);
				if (num2 < 0)
				{
					SeekLog($"[OPEN_SEEK_FAIL] target={num:F0} ret={num2}");
				}
				BeginSeek(num);
			}
			if (_videoStreamIndex != -1)
			{
				AVStream* ptr3 = _formatContext->streams[_videoStreamIndex];
				double num3 = 0.0;
				bool flag = false;
				AVCodecParameters* codecpar = _formatContext->streams[_videoStreamIndex]->codecpar;
				AVPacketSideData* ptr4 = ffmpeg.av_packet_side_data_get(codecpar->coded_side_data, codecpar->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
				if (ptr4 != null && ptr4->size >= 36)
				{
					int_array9* data = (int_array9*)ptr4->data;
					double num4 = ffmpeg.av_display_rotation_get(in *data);
					if (!double.IsNaN(num4))
					{
						num3 = 0.0 - num4;
						flag = true;
					}
				}
				if (!flag)
				{
					AVDictionaryEntry* ptr5 = ffmpeg.av_dict_get(ptr3->metadata, "rotate", null, 0);
					if (ptr5 != null && ptr5->value != null && double.TryParse(Marshal.PtrToStringUTF8((nint)ptr5->value), out var result))
					{
						num3 = result;
						flag = true;
					}
				}
				if (flag && num3 != 0.0)
				{
					this.RotationDetected?.Invoke(num3);
				}
				AVCodecParameters* codecpar2 = _formatContext->streams[_videoStreamIndex]->codecpar;
				bool useHwVideoDecode;
				AVCodec* codec = ResolveVideoDecoder(codecpar2, EnableHwAccel, out useHwVideoDecode);
				if (codec == null)
				{
					throw new Exception($"No decoder for {ffmpeg.avcodec_get_name(codecpar2->codec_id)}");
				}
				_videoCodecContext = ffmpeg.avcodec_alloc_context3(codec);
				ffmpeg.avcodec_parameters_to_context(_videoCodecContext, codecpar2);
				ConfigureVideoCodecThreads(_videoCodecContext, codecpar2->codec_id, codecpar2->width, codecpar2->height);
				if (useHwVideoDecode)
				{
					_getFormatCallback = GetFormat;
					_videoCodecContext->get_format = new AVCodecContext_get_format_func
					{
						Pointer = Marshal.GetFunctionPointerForDelegate(_getFormatCallback)
					};
					useHwVideoDecode = AttachVideoHwDevice(_videoCodecContext);
					if (!useHwVideoDecode)
					{
						ClearVideoHwDevice(_videoCodecContext);
					}
				}
				bool isHighRes = IsHighResolution(codecpar2->width, codecpar2->height);
				bool hwAttempted = useHwVideoDecode && _videoCodecContext->hw_device_ctx != null;
				string codecName = ffmpeg.avcodec_get_name(codecpar2->codec_id);
				if (ffmpeg.avcodec_open2(_videoCodecContext, codec, null) < 0)
				{
					if (hwAttempted)
					{
						// High-res included: prefer SW reopen over hard fail (NV12 GPU path can still play 4K SW).
						SeekLog($"[HW_OPEN_FAIL] {_videoCodecContext->width}x{_videoCodecContext->height} {codecName} — retrying without D3D11VA{(isHighRes ? " (high-res SW fallback)" : "")}");
						if (!ReopenVideoCodecWithoutHw(codecpar2, &codec) || ffmpeg.avcodec_open2(_videoCodecContext, codec, null) < 0)
						{
							throw new Exception("Could not open video codec");
						}
						hwAttempted = false;
						ResyncDemuxAfterVideoCodecChange(num >= 0.0 ? num : 0.0);
						if (isHighRes)
						{
							SeekLog($"[SW_FALLBACK_OK] {_videoCodecContext->width}x{_videoCodecContext->height} {codecName} — software decode (HW open failed)");
						}
					}
					else
					{
						throw new Exception("Could not open video codec");
					}
				}
				else if (hwAttempted)
				{
					// 4K/8K 8-bit: skip expensive open probe. 10-bit still probes — wrong sw_format breaks D3D11.
					bool is10Bit = Is10BitVideoCodecpar(codecpar2);
					bool skipHighResProbe = isHighRes && EnableHwAccel && !is10Bit;
					bool probePassed = skipHighResProbe || ProbeVideoHwD3D11Decode(num, isHighRes);
					if (!probePassed)
					{
						SeekLog($"[HW_PROBE_FAIL] {_videoCodecContext->width}x{_videoCodecContext->height} {codecName} — D3D11 frame probe failed, retrying without D3D11VA");
						if (!ReopenVideoCodecWithoutHw(codecpar2, &codec) || ffmpeg.avcodec_open2(_videoCodecContext, codec, null) < 0)
						{
							throw new Exception("Could not open video codec");
						}
						hwAttempted = false;
						// Probe consumed packets; reseek so SW starts at the same origin as audio.
						ResyncDemuxAfterVideoCodecChange(num >= 0.0 ? num : 0.0);
						if (isHighRes)
						{
							SeekLog($"[SW_FALLBACK_OK] {_videoCodecContext->width}x{_videoCodecContext->height} {codecName} — software decode (HW probe failed)");
						}
					}
					else if (skipHighResProbe)
					{
						SeekLog($"[HW_OK] {_videoCodecContext->width}x{_videoCodecContext->height} {codecName} D3D11VA active (high-res probe skipped, 8-bit)");
					}
					else if (is10Bit)
					{
						SeekLog($"[HW_OK] {_videoCodecContext->width}x{_videoCodecContext->height} {codecName} D3D11VA active (10-bit probe passed)");
					}
					else
					{
						SeekLog($"[HW_OK] {_videoCodecContext->width}x{_videoCodecContext->height} {codecName} D3D11VA active");
					}
				}
				// High-res SW is allowed when HW is unavailable; only log (no throw).
				LogSoftwareFallbackIfNeeded(codecpar2, _videoCodecContext->hw_device_ctx != null);
				_width = _videoCodecContext->width;
				_height = _videoCodecContext->height;
				_isSoftwareVideoDecode = _videoCodecContext->hw_device_ctx == null;

				_stats = new DecoderStats
				{
					VideoInfo = $"{_width}x{_height} {ffmpeg.avcodec_get_name(_videoCodecContext->codec_id)}",
					ThreadCount = ((_videoCodecContext->thread_count == 0) ? Environment.ProcessorCount : _videoCodecContext->thread_count),
					Bitrate = _formatContext->bit_rate,
					IsHwAccel = (_videoCodecContext->hw_device_ctx != null)
				};
			}
			else
			{
				_stats = new DecoderStats
				{
					VideoInfo = "No Video",
					ThreadCount = Environment.ProcessorCount,
					Bitrate = _formatContext->bit_rate,
					IsHwAccel = false
				};
			}
			if (_audioStreamIndex == -1 && !string.IsNullOrEmpty(_separateAudioUrl))
			{
				_audioFormatContext = ffmpeg.avformat_alloc_context();
				AVDictionary* ptr7 = null;
				ffmpeg.av_dict_set(&ptr7, "reconnect", "1", 0);
				ffmpeg.av_dict_set(&ptr7, "reconnect_streamed", "1", 0);
				ffmpeg.av_dict_set(&ptr7, "reconnect_delay_max", "5", 0);
				ffmpeg.av_dict_set(&ptr7, "rw_timeout", "10000000", 0);
				ffmpeg.av_dict_set(&ptr7, "seekable", "1", 0);
				ffmpeg.av_dict_set(&ptr7, "user_agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36", 0);
				fixed (AVFormatContext** ps = &_audioFormatContext)
				{
					AVFormatContext** ps2 = ps;
					_audioFormatContext->interrupt_callback.callback = new AVIOInterruptCB_callback_func
					{
						Pointer = Marshal.GetFunctionPointerForDelegate(_interruptCallback)
					};
					_audioFormatContext->interrupt_callback.opaque = null;
					if (ffmpeg.avformat_open_input(ps2, _separateAudioUrl, null, &ptr7) < 0)
					{
						_audioFormatContext = null;
						if (ptr7 != null)
						{
							ffmpeg.av_dict_free(&ptr7);
						}
					}
				}
				if (ptr7 != null)
				{
					ffmpeg.av_dict_free(&ptr7);
				}
				if (_audioFormatContext != null)
				{
					ffmpeg.avformat_find_stream_info(_audioFormatContext, null);
					for (int j = 0; j < _audioFormatContext->nb_streams; j++)
					{
						if (_audioFormatContext->streams[j]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
						{
							_audioStreamIndex = j;
							break;
						}
					}
					if (num >= 0.0)
					{
						int num5 = SeekFormatContext(_audioFormatContext, _audioStreamIndex, num);
						if (num5 < 0)
						{
							SeekLog($"[OPEN_SEEK_FAIL_AUDIO] target={num:F0} ret={num5}");
						}
					}
				}
			}
			if (_audioStreamIndex != -1)
			{
				AVCodecParameters* codecpar3 = ((_audioFormatContext != null) ? _audioFormatContext : _formatContext)->streams[_audioStreamIndex]->codecpar;
				AVCodec* codec2 = ffmpeg.avcodec_find_decoder(codecpar3->codec_id);
				_audioCodecContext = ffmpeg.avcodec_alloc_context3(codec2);
				ffmpeg.avcodec_parameters_to_context(_audioCodecContext, codecpar3);
				if (ffmpeg.avcodec_open2(_audioCodecContext, codec2, null) < 0)
				{
					throw new Exception("Could not open audio codec");
				}
				AudioSampleRate = ((_audioCodecContext->sample_rate > 0) ? _audioCodecContext->sample_rate : 48000);
				AudioChannels = 2;
				_audioMaxBufferSize = 192000;
				_audioBuffer = new byte[_audioMaxBufferSize];
				_audioBufferHandle = GCHandle.Alloc(_audioBuffer, GCHandleType.Pinned);
				_audioBufferPointer = _audioBufferHandle.AddrOfPinnedObject();
				_stats.AudioInfo = $"{AudioSampleRate}Hz {AudioChannels}ch {ffmpeg.avcodec_get_name(_audioCodecContext->codec_id)}";
				InitAudioFilterGraph();
			}
			else
			{
				_stats.AudioInfo = "No Audio";
			}
			IntPtr result2;
			while (_videoPacketQueue.TryDequeue(out result2))
			{
				ReturnPacket((AVPacket*)result2);
			}
			DecodedVideoFrame result3;
			while (_decodedVideoQueue.TryDequeue(out result3))
			{
				result3.Dispose();
			}
			IntPtr result4;
			while (_audioPacketQueue.TryDequeue(out result4))
			{
				ReturnPacket((AVPacket*)result4);
			}
			_audioFrame = GetFrame();
			_videoFrame = GetFrame();
			_packet = GetPacket();
			_isRunning = true;
			_isPaused = true;
			_isFinished = false;
			_videoEof = false;
			if (_audioStreamIndex != -1)
			{
				Interlocked.Increment(ref _activeThreads);
				_audioThread = new Thread(AudioDecodeLoop)
				{
					IsBackground = true,
					Name = "FFmpegAudioThread"
				};
				_audioThread.Start();
			}
			Interlocked.Increment(ref _activeThreads);
			_readThread = new Thread(ReadLoop)
			{
				IsBackground = true,
				Name = "FFmpegReadThread"
			};
			_readThread.Start();
			if (HasVideo)
			{
				// Hold PCM until the first video frame is displayed (same gate as post-seek).
				// Prevents audio-first start while video is still decoding.
				_suppressAudioOutput = true;
				_awaitingPostSeekVideoDisplay = true;
				Interlocked.Increment(ref _activeThreads);
				_videoThread = new Thread(VideoDecodeLoop)
				{
					IsBackground = true,
					Name = "FFmpegVideoThread"
				};
				_videoThread.Start();
			}
		}
		catch (Exception ex)
		{
			Logger.Error("Exception during decoder Open, performing cleanup", ex);
			_isRunning = false;
			_isPaused = false;
			// Signal events so any partially started threads can wake and exit
			_videoPacketAvailableEvent.Set();
			_audioPacketAvailableEvent.Set();
			lock (_lock)
			{
				Monitor.PulseAll(_lock);
			}
			// Wait for any threads we managed to start before forcing cleanup
			JoinWorkerThreads(3000);
			Interlocked.Exchange(ref _isCleanedUp, 0);
			Cleanup();
			throw;
		}
		finally
		{
			Interlocked.Exchange(ref _isOpening, 0);
		}
	}

	private unsafe static bool CodecSupportsD3D11Hw(AVCodec* codec)
	{
		for (int i = 0; ; i++)
		{
			AVCodecHWConfig* cfg = ffmpeg.avcodec_get_hw_config(codec, i);
			if (cfg == null)
			{
				break;
			}
			if (cfg->device_type == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA && cfg->pix_fmt == AVPixelFormat.AV_PIX_FMT_D3D11)
			{
				return true;
			}
		}
		return false;
	}

	private unsafe static AVCodec* ResolveVideoDecoder(AVCodecParameters* codecpar, bool enableHw, out bool useHwDecode)
	{
		useHwDecode = false;
		if (codecpar->codec_id == AVCodecID.AV_CODEC_ID_AV1)
		{
			if (enableHw)
			{
				AVCodec* av1Codec = ffmpeg.avcodec_find_decoder_by_name("av1");
				if (av1Codec != null && CodecSupportsD3D11Hw(av1Codec))
				{
					useHwDecode = true;
					return av1Codec;
				}
			}
			AVCodec* dav1d = ffmpeg.avcodec_find_decoder_by_name("libdav1d");
			if (dav1d != null)
			{
				return dav1d;
			}
			return ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_AV1);
		}
		AVCodec* codec = ffmpeg.avcodec_find_decoder(codecpar->codec_id);
		if (enableHw && codec != null && CodecSupportsD3D11Hw(codec))
		{
			useHwDecode = true;
		}
		return codec;
	}

	private unsafe static void ConfigureVideoCodecThreads(AVCodecContext* ctx, AVCodecID codecId, int width = 0, int height = 0)
	{
		int cores = Math.Max(1, Environment.ProcessorCount);
		bool highRes = IsHighResolution(width, height);
		if (codecId == AVCodecID.AV_CODEC_ID_AV1)
		{
			// libdav1d/frame threads scale well; allow more on high-res SW.
			ctx->thread_count = Math.Min(cores, highRes ? 12 : 8);
			ctx->thread_type = ffmpeg.FF_THREAD_FRAME;
		}
		else
		{
			// H.264/HEVC: raise cap from 4 → 8 (12 on high-res) for SW decode throughput.
			ctx->thread_count = Math.Min(cores, highRes ? 12 : 8);
			ctx->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
		}
	}

	private unsafe bool AttachVideoHwDevice(AVCodecContext* ctx)
	{
		// Only share the renderer's D3D11 device. A separate av_hwdevice_ctx_create device
		// produces textures the renderer cannot CopySubresourceRegion (silent black frames).
		if (_d3d11DevicePtr == IntPtr.Zero)
		{
			SeekLog("[HW_DEVICE] no shared D3D11 device — HW path unavailable, SW fallback will be used");
			return false;
		}
		AVBufferRef* ptr = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
		if (ptr == null)
		{
			return false;
		}
		AVHWDeviceContext* data = (AVHWDeviceContext*)ptr->data;
		AVD3D11VADeviceContext* hwctx = (AVD3D11VADeviceContext*)data->hwctx;
		Marshal.AddRef(_d3d11DevicePtr);
		hwctx->device = (ID3D11Device*)_d3d11DevicePtr;
		if (_d3d11ContextPtr != IntPtr.Zero)
		{
			Marshal.AddRef(_d3d11ContextPtr);
			hwctx->device_context = (ID3D11DeviceContext*)_d3d11ContextPtr;
		}
		else
		{
			hwctx->device_context = (ID3D11DeviceContext*)IntPtr.Zero;
		}
		if (ffmpeg.av_hwdevice_ctx_init(ptr) == 0)
		{
			ctx->hw_device_ctx = ffmpeg.av_buffer_ref(ptr);
		}
		else
		{
			SeekLog("[HW_DEVICE] shared D3D11 device init failed — SW fallback will be used");
		}
		ffmpeg.av_buffer_unref(&ptr);
		if (ctx->hw_device_ctx != null)
		{
			int pixelCount = ctx->width * ctx->height;
			ctx->extra_hw_frames = (pixelCount > 3840 * 2160) ? 16 : ((pixelCount > 1920 * 1080) ? 8 : 4);
			if (!AttachVideoHwFramesPool(ctx))
			{
				SeekLog($"[HW_FRAMES_WARN] continuing with hw_device_ctx only for {ctx->width}x{ctx->height}");
			}
			return true;
		}
		return false;
	}

	private unsafe static bool Is10BitPixelFormat(AVPixelFormat format)
	{
		return format == AVPixelFormat.AV_PIX_FMT_P010LE
			|| format == AVPixelFormat.AV_PIX_FMT_P010BE
			|| format == AVPixelFormat.AV_PIX_FMT_YUV420P10LE
			|| format == AVPixelFormat.AV_PIX_FMT_YUV420P10BE
			|| format == AVPixelFormat.AV_PIX_FMT_YUV422P10LE
			|| format == AVPixelFormat.AV_PIX_FMT_YUV444P10LE;
	}

	private unsafe static bool Is10BitVideoCodecpar(AVCodecParameters* codecpar)
	{
		if (codecpar->bits_per_raw_sample > 8 || codecpar->bits_per_coded_sample > 8)
		{
			return true;
		}
		if (Is10BitPixelFormat((AVPixelFormat)codecpar->format))
		{
			return true;
		}
		if (codecpar->codec_id == AVCodecID.AV_CODEC_ID_H264
			&& (codecpar->profile == ffmpeg.AV_PROFILE_H264_HIGH_10
				|| codecpar->profile == ffmpeg.AV_PROFILE_H264_HIGH_10_INTRA))
		{
			return true;
		}
		return false;
	}

	private unsafe static bool Is10BitVideoCodecContext(AVCodecContext* ctx)
	{
		if (ctx->bits_per_raw_sample > 8)
		{
			return true;
		}
		if (Is10BitPixelFormat(ctx->sw_pix_fmt) || Is10BitPixelFormat((AVPixelFormat)ctx->pix_fmt))
		{
			return true;
		}
		return false;
	}

	private unsafe static AVPixelFormat[] BuildHwSwFormatCandidates(AVCodecContext* ctx, bool is10Bit)
	{
		AVPixelFormat preferredSwFormat = (ctx->sw_pix_fmt != AVPixelFormat.AV_PIX_FMT_NONE)
			? ctx->sw_pix_fmt
			: (is10Bit ? AVPixelFormat.AV_PIX_FMT_P010LE : AVPixelFormat.AV_PIX_FMT_NV12);
		if (is10Bit)
		{
			return new AVPixelFormat[4]
			{
				preferredSwFormat,
				AVPixelFormat.AV_PIX_FMT_P010LE,
				AVPixelFormat.AV_PIX_FMT_YUV420P10LE,
				AVPixelFormat.AV_PIX_FMT_YUV420P10BE
			};
		}
		return new AVPixelFormat[4]
		{
			preferredSwFormat,
			AVPixelFormat.AV_PIX_FMT_NV12,
			AVPixelFormat.AV_PIX_FMT_YUV420P,
			AVPixelFormat.AV_PIX_FMT_P010LE
		};
	}

	private unsafe bool AttachVideoHwFramesPool(AVCodecContext* ctx)
	{
		if (ctx->hw_device_ctx == null)
		{
			return false;
		}
		if (ctx->hw_frames_ctx != null)
		{
			AVBufferRef* existingFramesCtx = ctx->hw_frames_ctx;
			ffmpeg.av_buffer_unref(&existingFramesCtx);
			ctx->hw_frames_ctx = null;
		}
		AVBufferRef* hwFramesRef = ffmpeg.av_hwframe_ctx_alloc(ctx->hw_device_ctx);
		if (hwFramesRef == null)
		{
			SeekLog($"[HW_FRAMES_FAIL] av_hwframe_ctx_alloc failed for {ctx->width}x{ctx->height}");
			return false;
		}
		AVHWFramesContext* framesCtx = (AVHWFramesContext*)hwFramesRef->data;
		framesCtx->format = AVPixelFormat.AV_PIX_FMT_D3D11;
		framesCtx->width = ctx->width;
		framesCtx->height = ctx->height;
		int pixelCount = ctx->width * ctx->height;
		bool is10Bit = Is10BitVideoCodecContext(ctx);
		framesCtx->initial_pool_size = (pixelCount > 3840 * 2160) ? 32 : ((pixelCount > 1920 * 1080) ? 16 : 10);
		AVPixelFormat[] swFormatCandidates = BuildHwSwFormatCandidates(ctx, is10Bit);
		bool initialized = false;
		for (int i = 0; i < swFormatCandidates.Length; i++)
		{
			AVPixelFormat candidate = swFormatCandidates[i];
			if (i > 0)
			{
				bool duplicate = false;
				for (int j = 0; j < i; j++)
				{
					if (swFormatCandidates[j] == candidate)
					{
						duplicate = true;
						break;
					}
				}
				if (duplicate)
				{
					continue;
				}
			}
			framesCtx->sw_format = candidate;
			if (ffmpeg.av_hwframe_ctx_init(hwFramesRef) == 0)
			{
				initialized = true;
				break;
			}
		}
		if (!initialized)
		{
			SeekLog($"[HW_FRAMES_FAIL] av_hwframe_ctx_init failed for {ctx->width}x{ctx->height} 10bit={is10Bit} sw={ctx->sw_pix_fmt}");
			AVBufferRef* failedRef = hwFramesRef;
			ffmpeg.av_buffer_unref(&failedRef);
			return false;
		}
		ctx->hw_frames_ctx = ffmpeg.av_buffer_ref(hwFramesRef);
		AVBufferRef* cleanupRef = hwFramesRef;
		ffmpeg.av_buffer_unref(&cleanupRef);
		if (ctx->hw_frames_ctx == null)
		{
			SeekLog($"[HW_FRAMES_FAIL] could not retain hw_frames_ctx for {ctx->width}x{ctx->height}");
			return false;
		}
		SeekLog($"[HW_FRAMES_OK] {ctx->width}x{ctx->height} sw={framesCtx->sw_format} pool={framesCtx->initial_pool_size}");
		return true;
	}

	private unsafe void ClearVideoHwDevice(AVCodecContext* ctx)
	{
		_getFormatCallback = null;
		ctx->get_format = null;
		if (ctx->hw_frames_ctx != null)
		{
			AVBufferRef* hwFramesCtx = ctx->hw_frames_ctx;
			ffmpeg.av_buffer_unref(&hwFramesCtx);
			ctx->hw_frames_ctx = null;
		}
		if (ctx->hw_device_ctx != null)
		{
			AVBufferRef* hwDeviceCtx = ctx->hw_device_ctx;
			ffmpeg.av_buffer_unref(&hwDeviceCtx);
			ctx->hw_device_ctx = null;
		}
	}

	private static bool IsHighResolution(int width, int height)
	{
		return width > 0 && height > 0 && width * height > 1920 * 1080;
	}

	private static bool Is8KResolution(int width, int height)
	{
		return width > 0 && height > 0 && (long)width * height > 3840L * 2160;
	}

	private string ResolveDecoderModeLabel()
	{
		if (_lastDecodedFrameIsD3D11)
		{
			return "D3D11VA";
		}
		if (_stats.IsHwAccel)
		{
			return "SW Fallback";
		}
		return "Software";
	}

	/// <summary>
	/// Logs when high-res plays on software. Previously threw and blocked 4K when D3D11VA
	/// was unavailable; NV12 GPU upload path now makes high-res SW viable (slower).
	/// </summary>
	private unsafe void LogSoftwareFallbackIfNeeded(AVCodecParameters* codecpar, bool hwActive)
	{
		if (hwActive)
		{
			return;
		}
		int width = (_videoCodecContext != null && _videoCodecContext->width > 0) ? _videoCodecContext->width : codecpar->width;
		int height = (_videoCodecContext != null && _videoCodecContext->height > 0) ? _videoCodecContext->height : codecpar->height;
		if (!IsHighResolution(width, height))
		{
			return;
		}
		string codecName = ffmpeg.avcodec_get_name(codecpar->codec_id);
		string reason = EnableHwAccel ? "D3D11VA unavailable" : "forced SW (F4)";
		SeekLog($"[SW_FALLBACK] {width}x{height} {codecName} — software decode ({reason}); may be slower than HW");
	}

	private unsafe bool ProbeVideoHwD3D11Decode(double seekMs, bool isHighRes)
	{
		if (_videoCodecContext == null || _videoCodecContext->hw_device_ctx == null)
		{
			return true;
		}
		AVPacket* probePkt = ffmpeg.av_packet_alloc();
		AVFrame* probeFrame = ffmpeg.av_frame_alloc();
		bool decodedD3D11 = false;
		int packetsTried = 0;
		int lastFormat = -1;
		try
		{
			double targetMs = (seekMs >= 0.0) ? seekMs : 0.0;
			int width = _videoCodecContext->width;
			int height = _videoCodecContext->height;
			bool is8K = Is8KResolution(width, height);
			bool probeFromStart = isHighRes && targetMs > 1.0;
			if (probeFromStart)
			{
				SeekFormatContext(_formatContext, _videoStreamIndex, 0.0);
				ffmpeg.avcodec_flush_buffers(_videoCodecContext);
			}
			int maxPackets = is8K ? 64 : (isHighRes ? 120 : 80);
			for (int i = 0; i < maxPackets && !decodedD3D11; i++)
			{
				if (ffmpeg.av_read_frame(_formatContext, probePkt) < 0)
				{
					break;
				}
				if (probePkt->stream_index != _videoStreamIndex)
				{
					ffmpeg.av_packet_unref(probePkt);
					continue;
				}
				packetsTried++;
				if (ffmpeg.avcodec_send_packet(_videoCodecContext, probePkt) == 0 && ffmpeg.avcodec_receive_frame(_videoCodecContext, probeFrame) == 0)
				{
					lastFormat = probeFrame->format;
					decodedD3D11 = probeFrame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11;
				}
				ffmpeg.av_packet_unref(probePkt);
			}
			// ALWAYS reseek after probe — previously targetMs==0 skipped reseek and left demux mid-file,
			// which produced firstVideo PTS seconds ahead of audio and a permanent multi-second Offset.
			SeekFormatContext(_formatContext, _videoStreamIndex, targetMs);
			ffmpeg.avcodec_flush_buffers(_videoCodecContext);
			if (!decodedD3D11)
			{
				SeekLog($"[HW_PROBE_DETAIL] packets={packetsTried}/{maxPackets} lastFormat={lastFormat} targetMs={targetMs:F0} fromStart={probeFromStart} reseekt");
			}
		}
		finally
		{
			ffmpeg.av_packet_free(&probePkt);
			ffmpeg.av_frame_free(&probeFrame);
		}
		return decodedD3D11;
	}

	private unsafe bool ReopenVideoCodecWithoutHw(AVCodecParameters* codecpar, AVCodec** codec)
	{
		if (_videoCodecContext != null)
		{
			ClearVideoHwDevice(_videoCodecContext);
			AVCodecContext* oldCtx = _videoCodecContext;
			ffmpeg.avcodec_free_context(&oldCtx);
			_videoCodecContext = null;
		}
		bool useHwDecode;
		AVCodec* swCodec = ResolveVideoDecoder(codecpar, enableHw: false, out useHwDecode);
		if (swCodec == null)
		{
			return false;
		}
		_videoCodecContext = ffmpeg.avcodec_alloc_context3(swCodec);
		ffmpeg.avcodec_parameters_to_context(_videoCodecContext, codecpar);
		ConfigureVideoCodecThreads(_videoCodecContext, codecpar->codec_id, codecpar->width, codecpar->height);
		*codec = swCodec;
		return true;
	}

	/// <summary>After HW→SW reopen, put demux+codec back at the intended start so A/V share PTS origin.</summary>
	private unsafe void ResyncDemuxAfterVideoCodecChange(double targetMs)
	{
		if (_formatContext == null || _videoStreamIndex < 0)
		{
			return;
		}
		double t = targetMs >= 0.0 ? targetMs : 0.0;
		SeekFormatContext(_formatContext, _videoStreamIndex, t);
		if (_videoCodecContext != null)
		{
			ffmpeg.avcodec_flush_buffers(_videoCodecContext);
		}
		if (_audioCodecContext != null)
		{
			ffmpeg.avcodec_flush_buffers(_audioCodecContext);
		}
		if (_audioFormatContext != null && _audioStreamIndex >= 0)
		{
			SeekFormatContext(_audioFormatContext, _audioStreamIndex, t);
		}
		SeekLog($"[DEMUX_RESYNC] after codec change targetMs={t:F0}");
	}

	private unsafe static bool IsHardwarePixelFormat(AVPixelFormat format)
	{
		AVPixFmtDescriptor* desc = ffmpeg.av_pix_fmt_desc_get(format);
		return desc != null && (desc->flags & ffmpeg.AV_PIX_FMT_FLAG_HWACCEL) != 0;
	}

	private unsafe static AVPixelFormat GetSoftwarePixelFormat(AVCodecContext* s, AVPixelFormat* fmt)
	{
		for (AVPixelFormat* ptr = fmt; *ptr != AVPixelFormat.AV_PIX_FMT_NONE; ptr++)
		{
			if (*ptr == s->sw_pix_fmt)
			{
				return *ptr;
			}
			if (*ptr == AVPixelFormat.AV_PIX_FMT_YUV420P)
			{
				return *ptr;
			}
			if (*ptr == AVPixelFormat.AV_PIX_FMT_NV12)
			{
				return *ptr;
			}
			if (*ptr == AVPixelFormat.AV_PIX_FMT_YUV420P10LE)
			{
				return *ptr;
			}
			if (*ptr == AVPixelFormat.AV_PIX_FMT_YUV420P10BE)
			{
				return *ptr;
			}
			if (*ptr == AVPixelFormat.AV_PIX_FMT_P010LE)
			{
				return *ptr;
			}
		}
		return s->sw_pix_fmt;
	}

	private unsafe AVPixelFormat GetFormat(AVCodecContext* s, AVPixelFormat* fmt)
	{
		if (EnableHwAccel && s->hw_device_ctx != null)
		{
			for (AVPixelFormat* ptr = fmt; *ptr != AVPixelFormat.AV_PIX_FMT_NONE; ptr++)
			{
				if (*ptr == AVPixelFormat.AV_PIX_FMT_D3D11)
				{
					return *ptr;
				}
			}
			var offered = new List<int>(8);
			for (AVPixelFormat* ptr = fmt; *ptr != AVPixelFormat.AV_PIX_FMT_NONE; ptr++)
			{
				offered.Add((int)*ptr);
			}
			SeekLog($"[HW_FORMAT_FAIL] D3D11 not offered for {s->width}x{s->height} sw_pix_fmt={s->sw_pix_fmt} offered=[{string.Join(",", offered)}]");
		}
		return GetSoftwarePixelFormat(s, fmt);
	}

	private unsafe void FreeAudioFilterGraph()
	{
		_abufferCtx = null;
		_atempoCtx = null;
		_abuffersinkCtx = null;
		if (_audioFilterGraph != null)
		{
			AVFilterGraph* graph = _audioFilterGraph;
			ffmpeg.avfilter_graph_free(&graph);
			_audioFilterGraph = null;
		}
	}

	private unsafe void InitAudioFilterGraph()
	{
		if (_audioStreamIndex == -1 || _audioCodecContext == null)
		{
			return;
		}
		FreeAudioFilterGraph();
		if (_filteredAudioFrame == null)
		{
			_filteredAudioFrame = GetFrame();
		}
		_audioFilterGraph = ffmpeg.avfilter_graph_alloc();
		AVFilter* filt = ffmpeg.avfilter_get_by_name("abuffer");
		ffmpeg.avfilter_get_by_name("atempo");
		AVFilter* filt2 = ffmpeg.avfilter_get_by_name("abuffersink");
		AVRational time_base = ((_audioFormatContext != null) ? _audioFormatContext : _formatContext)->streams[_audioStreamIndex]->time_base;
		AVChannelLayout ch_layout = _audioCodecContext->ch_layout;
		byte* ptr = stackalloc byte[128];
		ffmpeg.av_channel_layout_describe(&ch_layout, ptr, 128uL);
		string value = Marshal.PtrToStringAnsi((nint)ptr) ?? "stereo";
		string text = ffmpeg.av_get_sample_fmt_name(_audioCodecContext->sample_fmt);
		if (text == null)
		{
			text = "s16";
		}
		string args = $"time_base={time_base.num}/{time_base.den}:sample_rate={_audioCodecContext->sample_rate}:sample_fmt={text}:channel_layout={value}";
		AVFilterContext* ptr2 = null;
		if (ffmpeg.avfilter_graph_create_filter(&ptr2, filt, "in", args, null, _audioFilterGraph) < 0)
		{
			FreeAudioFilterGraph();
			return;
		}
		AVFilterContext* ptr3 = null;
		if (ffmpeg.avfilter_graph_create_filter(&ptr3, filt2, "out", null, null, _audioFilterGraph) < 0)
		{
			FreeAudioFilterGraph();
			return;
		}
		double num = Math.Max(0.25, Math.Min(4.0, _playbackSpeed));
		string value2 = ((num > 2.0) ? ("atempo=2.0,atempo@fatempo=" + (num / 2.0).ToString(CultureInfo.InvariantCulture)) : ((!(num < 0.5)) ? ("atempo@fatempo=" + num.ToString(CultureInfo.InvariantCulture)) : ("atempo=0.5,atempo@fatempo=" + (num / 0.5).ToString(CultureInfo.InvariantCulture))));
		string filters = $"equalizer@feq=f=1000:width_type=h:width=200:g={AudioVocalGain.ToString(CultureInfo.InvariantCulture)},volume@fvol=volume={AudioVolumeLevel.ToString(CultureInfo.InvariantCulture)},{value2}";
		AVFilterInOut* ptr4 = ffmpeg.avfilter_inout_alloc();
		AVFilterInOut* ptr5 = ffmpeg.avfilter_inout_alloc();
		ptr4->name = ffmpeg.av_strdup("in");
		ptr4->filter_ctx = ptr2;
		ptr4->pad_idx = 0;
		ptr4->next = null;
		ptr5->name = ffmpeg.av_strdup("out");
		ptr5->filter_ctx = ptr3;
		ptr5->pad_idx = 0;
		ptr5->next = null;
		if (ffmpeg.avfilter_graph_parse_ptr(_audioFilterGraph, filters, &ptr5, &ptr4, null) < 0)
		{
			ffmpeg.avfilter_inout_free(&ptr5);
			ffmpeg.avfilter_inout_free(&ptr4);
			FreeAudioFilterGraph();
			return;
		}
		if (ffmpeg.avfilter_graph_config(_audioFilterGraph, null) < 0)
		{
			ffmpeg.avfilter_inout_free(&ptr5);
			ffmpeg.avfilter_inout_free(&ptr4);
			FreeAudioFilterGraph();
			return;
		}
		ffmpeg.avfilter_inout_free(&ptr5);
		ffmpeg.avfilter_inout_free(&ptr4);
		_abufferCtx = ptr2;
		_abuffersinkCtx = ptr3;
		_atempoCtx = ffmpeg.avfilter_graph_get_filter(_audioFilterGraph, "fatempo");
	}

	public void Play()
	{
		lock (_lock)
		{
			if (!_isPaused)
			{
				return;
			}
			_isPaused = false;
			// Keep stream offset fixed — do NOT rewrite offset from displayed frame (that fakes AvDiff=0).
			_masterClockStopwatch.Restart();
			Monitor.PulseAll(_lock);
		}
	}

	private double GetAvStartOffsetMs()
	{
		lock (_lock)
		{
			return _avStartOffsetMs;
		}
	}

	private void SetAvStartOffsetMs(double value)
	{
		lock (_lock)
		{
			_avStartOffsetMs = value;
		}
	}

	private void SyncPlaybackClockToPts(double ptsMs)
	{
		lock (_lock)
		{
			_currentPlaybackPtsTime = ptsMs;
			_masterClockStopwatch.Restart();
		}
	}

	private void BeginSeek(double requestedTargetMs)
	{
		_seekRequestedMs = requestedTargetMs;
		_postSeekTargetMs = requestedTargetMs;
		_seekLandPtsMs = -1.0;
		_seekPhase = SeekPhase.Active;
		_seekVideoLanded = _videoStreamIndex == -1;
		_seekAudioLanded = _audioStreamIndex == -1;
		_isSeekingVideo = _videoStreamIndex != -1;
		_isSeekingAudio = _audioStreamIndex != -1;
		_syncAvOffsetFromStreamStart = false;
		SetAvStartOffsetMs(0.0);
		_emaAvDiffMs = 0.0;
		ResetAudioOutputClock(-1.0);
		_awaitingPostSeekVideoDisplay = HasVideo;
		_suppressAudioOutput = true;
		_masterClockStopwatch.Reset();
		SeekLog($"[SEEK_BEGIN] target={requestedTargetMs:F0} video={!_seekVideoLanded} audio={!_seekAudioLanded}");
	}

	private void TryCompleteSeek()
	{
		double landPts;
		double requestedMs;
		lock (_lock)
		{
			if (_seekPhase != SeekPhase.Active || !_seekVideoLanded || !_seekAudioLanded)
			{
				return;
			}
			requestedMs = _seekRequestedMs;
			landPts = _seekLandPtsMs >= 0.0 ? _seekLandPtsMs : requestedMs;

			_seekPhase = SeekPhase.None;
			_seekRequestedMs = -1.0;
			_seekLandPtsMs = -1.0;

			_currentPlaybackPtsTime = landPts;
			_avStartOffsetMs = 0.0;
			if (IsAudioOnlyPlayback)
			{
				_baseAudioPtsMs = landPts;
				_totalOutputSamples = 0L;
				_lastValidAudioPtsTime = landPts;
				ResetAudioOutputClock(landPts);
			}
			if (!_isPaused)
			{
				_masterClockStopwatch.Restart();
			}
		}
		SeekLog($"[SEEK_DONE] land={landPts:F0} requested={requestedMs:F0} videoPts={_lastDisplayedVideoPtsMs:F0} awaitVideo={_awaitingPostSeekVideoDisplay}");
		SeekPerformed?.Invoke();
	}

	private void TryStartPlaybackClock()
	{
		if (_seekPhase == SeekPhase.Active || IsAudioOnlyPlayback)
		{
			return;
		}
		if (_masterClockStopwatch.IsRunning)
		{
			return;
		}
		if (_audioStreamIndex == -1)
		{
			if (_firstVideoPtsMs >= 0.0)
			{
				SyncPlaybackClockToPts(_firstVideoPtsMs);
			}
			return;
		}
		// AV1/webm: video often lands before audio PTS is ready — start on first stream available.
		if (_firstAudioPtsMs >= 0.0)
		{
			SyncPlaybackClockToPts(_firstAudioPtsMs);
			return;
		}
		if (_firstVideoPtsMs >= 0.0)
		{
			SyncPlaybackClockToPts(_firstVideoPtsMs);
		}
	}

	private void EnsurePlaybackClockStarted()
	{
		if (_seekPhase == SeekPhase.Active || IsAudioOnlyPlayback)
		{
			return;
		}
		TryStartPlaybackClock();
		if (_masterClockStopwatch.IsRunning)
		{
			return;
		}
		if (_decodedVideoQueue.TryPeek(out DecodedVideoFrame? queuedHead))
		{
			SyncPlaybackClockToPts(queuedHead.PtsTime - GetAvStartOffsetMs());
		}
	}

	/// <summary>
	/// Audio master only. Do not invent a faster clock — that made video free-run at V.DecodeFPS
	/// (e.g. 32fps for 24fps content → 2min file ends at ~1.5min).
	/// </summary>
	private double GetVideoPacingClockPts()
	{
		return GetMasterClockPts();
	}

	private void MarkVideoFrameDisplayed(double ptsMs)
	{
		_lastDisplayedVideoPtsMs = ptsMs;
		if (_awaitingPostSeekVideoDisplay)
		{
			ReleasePostSeekAudioOutput(ptsMs);
			return;
		}
		if (_seekPhase == SeekPhase.Active || _audioStreamIndex == -1 || _isSeekingAudio)
		{
			return;
		}
		// Stats only — never walk offset here (that fakes sync).
		double avDiffMs = ComputeAvDiffMs(ptsMs);
		_emaAvDiffMs = (_emaAvDiffMs == 0.0)
			? avDiffMs
			: (_emaAvDiffMs * 0.80 + avDiffMs * 0.20);
	}

	private unsafe void UpdateAvStartOffsetFromFirstFrames()
	{
		if (!_syncAvOffsetFromStreamStart)
		{
			return;
		}
		if (_firstAudioPtsMs < 0.0 || _firstVideoPtsMs < 0.0)
		{
			return;
		}
		// Audio is the master clock. Container-normalized PTS share one timeline → offset 0 for
		// muxed files. Any multi-second firstV-firstA was a bug (made video free-run at decode FPS).
		double raw = _firstVideoPtsMs - _firstAudioPtsMs;
		bool dualAudio = _audioFormatContext != null;
		double offset = 0.0;
		if (dualAudio)
		{
			// Separate audio URL only: allow a small start skew, never multi-second.
			offset = Math.Clamp(raw, -200.0, 200.0);
		}
		else if (Math.Abs(raw) > 50.0 && Math.Abs(raw) <= 200.0)
		{
			// Tiny residual only (encoder delay); large values discarded.
			offset = raw;
		}
		if (Math.Abs(raw) > 200.0)
		{
			SeekLog($"[AV_OFFSET_ZERO] rawFirstDelta={raw:F0}ms ignored (audio master, shared timeline)");
		}
		SetAvStartOffsetMs(offset);
		_syncAvOffsetFromStreamStart = false;
		SeekLog($"[AV_OFFSET_SET] firstV={_firstVideoPtsMs:F0} firstA={_firstAudioPtsMs:F0} offset={offset:F0} dualAudio={dualAudio}");
	}

	public void Pause()
	{
		lock (_lock)
		{
			if (_isPaused)
			{
				return;
			}
			_currentPlaybackPtsTime = CapturePlaybackPtsMs();
			_isPaused = true;
			_masterClockStopwatch.Reset();
		}
	}

	public void Stop()
	{
		_isInterruptRequested = true;
		_isRunning = false;
		_videoPacketAvailableEvent.Set();
		_audioPacketAvailableEvent.Set();
		lock (_lock)
		{
			_isPaused = false;
			Monitor.PulseAll(_lock);
		}
	}

	private void ThreadFinished()
	{
		if (Interlocked.Decrement(ref _activeThreads) == 0)
		{
			Cleanup();
		}
	}

	/// <summary>
	/// Releases any native FFmpeg resources left from a prior Open/playback session
	/// before allocating new format/codec contexts.
	/// </summary>
	private unsafe void EnsureReleasedBeforeOpen(bool threadsIdle)
	{
		bool hasNativeResources = _formatContext != null
			|| _audioFormatContext != null
			|| _videoCodecContext != null
			|| _audioCodecContext != null;
		if (!hasNativeResources)
		{
			return;
		}
		if (!threadsIdle)
		{
			Logger.Error("Open aborted: prior FFmpeg contexts are still allocated while worker threads are active.");
			throw new InvalidOperationException("Decoder is still shutting down; cannot open a new file yet.");
		}

		Interlocked.Exchange(ref _isCleanedUp, 0);
		Cleanup();
		_readThread = null;
		_videoThread = null;
		_audioThread = null;
		Interlocked.Exchange(ref _activeThreads, 0);
		_videoPacketAvailableEvent.Reset();
		_audioPacketAvailableEvent.Reset();
	}

	/// <summary>
	/// Waits for worker threads to complete (or timeout). Used in Dispose and before re-Open
	/// to ensure Cleanup can run safely without races.
	/// </summary>
	private bool WaitForThreadsToFinish(int timeoutMs)
	{
		int waited = 0;
		while ((Interlocked.CompareExchange(ref _activeThreads, 0, 0) > 0 || Interlocked.CompareExchange(ref _isOpening, 0, 0) == 1) && waited < timeoutMs)
		{
			Thread.Sleep(10);
			waited += 10;
		}
		bool finished = Interlocked.CompareExchange(ref _activeThreads, 0, 0) == 0
			&& Interlocked.CompareExchange(ref _isOpening, 0, 0) == 0;
		if (!finished)
		{
			Logger.Warn($"Decoder thread wait timed out after {timeoutMs}ms (activeThreads={_activeThreads}).");
		}
		return finished;
	}

	private bool AreWorkerThreadsAlive()
	{
		return (_readThread?.IsAlive ?? false)
			|| (_videoThread?.IsAlive ?? false)
			|| (_audioThread?.IsAlive ?? false);
	}

	private void JoinWorkerThreads(int timeoutMs)
	{
		Thread?[] threads = { _readThread, _videoThread, _audioThread };
		int aliveCount = 0;
		foreach (Thread? thread in threads)
		{
			if (thread != null && thread.IsAlive)
			{
				aliveCount++;
			}
		}
		if (aliveCount == 0)
		{
			return;
		}
		int perThreadTimeout = Math.Max(500, timeoutMs / Math.Max(1, aliveCount));
		foreach (Thread? thread in threads)
		{
			if (thread != null && thread.IsAlive && !thread.Join(perThreadTimeout))
			{
				Logger.Warn($"Decoder worker thread '{thread.Name}' did not exit within {perThreadTimeout}ms.");
			}
		}
	}

	private void ForceCleanupIfNeeded()
	{
		if (Interlocked.CompareExchange(ref _isCleanedUp, 0, 0) == 1)
		{
			return;
		}
		Interlocked.Exchange(ref _isCleanedUp, 0);
		try
		{
			Cleanup();
		}
		catch (Exception ex)
		{
			Logger.Error("ForceCleanupIfNeeded failed", ex);
		}
	}

	private void DisposeSyncEvents()
	{
		try
		{
			_videoPacketAvailableEvent.Dispose();
			_audioPacketAvailableEvent.Dispose();
		}
		catch (Exception ex)
		{
			Logger.Error("Failed to dispose decoder sync events", ex);
		}
	}

	private void DeferredCleanupWorker()
	{
		try
		{
			_readThread?.Join(TimeSpan.FromSeconds(30));
			_videoThread?.Join(TimeSpan.FromSeconds(30));
			_audioThread?.Join(TimeSpan.FromSeconds(30));
			Interlocked.Exchange(ref _activeThreads, 0);
			ForceCleanupIfNeeded();
			_readThread = null;
			_videoThread = null;
			_audioThread = null;
			DisposeSyncEvents();
		}
		catch (Exception ex)
		{
			Logger.Error("Deferred decoder cleanup failed", ex);
		}
	}

	private void ScheduleDeferredCleanup()
	{
		new Thread(DeferredCleanupWorker)
		{
			IsBackground = true,
			Name = "FFmpegDeferredCleanup"
		}.Start();
	}

	public unsafe void Seek(double ratio)
	{
		double num = ((_formatContext != null) ? ((double)_formatContext->duration / 1000000.0) : 0.0);
		lock (_lock)
		{
			_seekTargetMs = ratio * num * 1000.0;
			Interlocked.Increment(ref _seekGeneration);
			// Generation helps cancel in-flight work from previous rapid seeks.
			// See _activeSeekGen checks in land logic.
			_isFinished = false;
			Monitor.PulseAll(_lock);
		}
	}

	public void SetSpeed(double speed)
	{
		lock (_lock)
		{
			if (_playbackSpeed != speed)
			{
				_playbackSpeed = speed;
				_speedChanged = true;
				_rebuildAudioFilters = true;
				_baseAudioPtsMs = -1.0;
				Monitor.PulseAll(_lock);
			}
		}
	}

	public void SetAudioFilters(double? volume = null, double? vocal = null)
	{
		lock (_lock)
		{
			if (volume.HasValue)
			{
				AudioVolumeLevel = volume.Value;
			}
			if (vocal.HasValue)
			{
				AudioVocalGain = vocal.Value;
			}
			_rebuildAudioFilters = true;
		}
	}

	public void SetVideoFilters(double? brightness = null, double? contrast = null, double? saturation = null)
	{
		lock (_lock)
		{
			if (brightness.HasValue)
			{
				VideoBrightness = brightness.Value;
			}
			if (contrast.HasValue)
			{
				VideoContrast = contrast.Value;
			}
			if (saturation.HasValue)
			{
				VideoSaturation = saturation.Value;
			}
			_videoFiltersChanged = true;
		}
	}

	private unsafe void ReadLoop()
	{
		_lastReaderFpsCalcTime = DateTime.UtcNow;
		while (_isRunning)
		{
			DateTime readerNow = DateTime.UtcNow;
			if ((readerNow - _lastReaderFpsCalcTime).TotalMilliseconds >= 1000.0)
			{
				_stats.ReaderFps = _packetsReadThisSecond;
				_packetsReadThisSecond = 0;
				_lastReaderFpsCalcTime = readerNow;
			}
			double num = -1.0;
			int thisSeekGen = 0;
			lock (_lock)
			{
				if (_isFinished && _seekTargetMs < 0.0)
				{
					Monitor.Wait(_lock, 50);
					continue;
				}
				if (_seekTargetMs >= 0.0)
				{
					num = _seekTargetMs;
					thisSeekGen = _seekGeneration; // snapshot for this seek attempt (rapid seek cancel)
				}
				else
				{
					if (_isPaused && _videoDecodeWarmup == 0)
					{
						Monitor.Wait(_lock, 50);
						continue;
					}
					int videoPacketLimit = GetVideoPacketQueueLimit();
					int audioPacketLimit = GetAudioPacketQueueLimit();
					bool flag = _videoStreamIndex != -1 && _videoPacketQueue.Count >= videoPacketLimit;
					bool flag2 = _audioStreamIndex != -1 && _audioPacketQueue.Count >= audioPacketLimit;
					if (_isPreBuffering)
					{
						bool videoReady = _videoStreamIndex == -1 || _videoEof || _videoPacketQueue.Count >= GetVideoPrebufferTarget();
						bool audioReady = _audioStreamIndex == -1 || _audioPacketQueue.Count >= GetAudioPrebufferTarget();
						if (videoReady && audioReady)
						{
							_isPreBuffering = false;
						}
					}
					if (_audioFormatContext == null)
					{
						while (_isRunning && _seekTargetMs < 0.0 && flag && flag2)
						{
							Monitor.Wait(_lock, 20);
							flag = _videoStreamIndex != -1 && _videoPacketQueue.Count >= videoPacketLimit;
							flag2 = _audioStreamIndex != -1 && _audioPacketQueue.Count >= audioPacketLimit;
						}
					}
					else
					{
						while (_isRunning && _seekTargetMs < 0.0 && flag && flag2)
						{
							Monitor.Wait(_lock, 20);
							flag = _videoStreamIndex != -1 && _videoPacketQueue.Count >= videoPacketLimit;
							flag2 = _audioStreamIndex != -1 && _audioPacketQueue.Count >= audioPacketLimit;
						}
					}
				}
			}

			if (num >= 0.0)
			{
				int num3 = SeekFormatContext(_formatContext, _videoStreamIndex, num);
				if (_audioFormatContext != null)
				{
					int num4 = SeekFormatContext(_audioFormatContext, _audioStreamIndex, num);
					if (num4 < 0)
					{
						SeekLog($"[SEEK_FAIL_AUDIO] target={num:F0} ret={num4}");
					}
				}
				if (num3 < 0)
				{
					SeekLog($"[SEEK_FAIL_VIDEO] target={num:F0} ret={num3}");
				}
				_activeSeekGen = thisSeekGen; // current active gen for land checks (rapid seek protection)
				lock (_lock)
				{
					IntPtr result;
					while (_videoPacketQueue.TryDequeue(out result))
					{
						ReturnPacket((AVPacket*)result);
					}
					// OPTIMIZATION: small forward seek -> PTS-based partial preserve of decoded frames.
					// Avoids wasting work when user nudges the bar a little.
					// Packet queues still fully cleared (compressed data not reusable).
					double currentPos = _currentPlaybackPtsTime;
					bool smallForwardSeek = (num > currentPos) && (num - currentPos < 5000.0);
					if (smallForwardSeek)
					{
						var kept = new List<DecodedVideoFrame>();
						DecodedVideoFrame f;
						while (_decodedVideoQueue.TryDequeue(out f))
						{
							if (f.PtsTime >= num - 1000.0) // keep from ~1s before new target
							{
								kept.Add(f);
							}
							else
							{
								f.Dispose();
							}
						}
						foreach (var k in kept)
						{
							_decodedVideoQueue.Enqueue(k);
						}
					}
					else
					{
						DecodedVideoFrame result2;
						while (_decodedVideoQueue.TryDequeue(out result2))
						{
							result2.Dispose();
						}
					}
					IntPtr result3;
					while (_audioPacketQueue.TryDequeue(out result3))
					{
						ReturnPacket((AVPacket*)result3);
					}
					Interlocked.Exchange(ref _needsVideoFlush, 1);
					if (_audioStreamIndex != -1)
					{
						Interlocked.Exchange(ref _needsAudioFlush, 1);
					}
					_lastValidPtsTime = 0.0;
					_lastValidAudioPtsTime = 0.0;
					_isFirstVideoFrame = true;
					_isFirstAudioFrame = true;
					_isPreBuffering = true;
					// OPTIMIZATION: temporarily lower pre-buffering after seek so playback resumes faster.
					// User already waited for the seek; don't make them wait extra for full buffer.
					_postSeekReducedPrebufferUntil = DateTime.UtcNow.AddSeconds(1.5);
					_lastDecodedFrameIsD3D11 = false;
					_firstAudioPtsMs = -1.0;
					_firstVideoPtsMs = -1.0;
					_avStartOffsetMs = 0.0;
					_syncAvOffsetFromStreamStart = false;
					ResetFpsMeasurement(resetTargetFromStream: true);
					_lastDisplayedVideoPtsMs = -1.0;
					_baseAudioPtsMs = -1.0;
					_totalOutputSamples = 0L;
					ResetAudioOutputClock(-1.0);
					BeginSeek(num);
					if (_seekTargetMs == num)
					{
						_seekTargetMs = -1.0;
					}
					_isFinished = false;
					_notifiedPlaybackFinished = false;
					_videoEof = false;
					this.SeekInitiated?.Invoke();
					Monitor.PulseAll(_lock);
				}
			}
			else
			{
				if (_isFinished)
				{
					continue;
				}
				int videoPacketLimit = GetVideoPacketQueueLimit();
				int audioPacketLimit = GetAudioPacketQueueLimit();
				bool flag6 = _videoStreamIndex != -1 && _videoPacketQueue.Count >= videoPacketLimit;
				bool flag7 = _audioStreamIndex != -1 && _audioPacketQueue.Count >= audioPacketLimit;
				bool muxedQueuesSaturated = _audioFormatContext == null && flag6 && flag7;
				int num5 = ffmpeg.AVERROR_EOF;
				if (!_videoEof && (_audioFormatContext != null || !muxedQueuesSaturated))
				{
					num5 = ffmpeg.av_read_frame(_formatContext, _packet);
					if (num5 < 0)
					{
						ffmpeg.av_packet_unref(_packet);
						if (num5 != ffmpeg.AVERROR_EOF)
						{
							_isFinished = true;
							break;
						}
						_videoEof = true;
						if (_audioFormatContext == null)
						{
							_isFinished = true;
							continue;
						}
					}
				}
				if (!_videoEof && num5 >= 0)
				{
					if (_packet->stream_index == _videoStreamIndex)
					{
						AVPacket* packet = GetPacket();
						ffmpeg.av_packet_ref(packet, _packet);
						_videoPacketQueue.Enqueue((nint)packet);
						Interlocked.Add(ref _videoPacketQueueSizeBytes, packet->size);
						_videoPacketAvailableEvent.Set();
						Interlocked.Increment(ref _packetsReadThisSecond);
					}
					else if (_packet->stream_index == _audioStreamIndex && _audioStreamIndex != -1 && _audioFormatContext == null)
					{
						AVPacket* packet2 = GetPacket();
						ffmpeg.av_packet_ref(packet2, _packet);
						_audioPacketQueue.Enqueue((nint)packet2);
						Interlocked.Add(ref _audioPacketQueueSizeBytes, packet2->size);
						_audioPacketAvailableEvent.Set();
						Interlocked.Increment(ref _packetsReadThisSecond);
					}
					ffmpeg.av_packet_unref(_packet);
				}
				if (_audioFormatContext == null || _audioStreamIndex == -1 || flag7)
				{
					continue;
				}
				AVPacket* packet3 = GetPacket();
				int num6 = ffmpeg.av_read_frame(_audioFormatContext, packet3);
				if (num6 >= 0 && packet3->stream_index == _audioStreamIndex)
				{
					_audioPacketQueue.Enqueue((nint)packet3);
					Interlocked.Add(ref _audioPacketQueueSizeBytes, packet3->size);
					_audioPacketAvailableEvent.Set();
					Interlocked.Increment(ref _packetsReadThisSecond);
					lock (_lock)
					{
						Monitor.PulseAll(_lock);
					}
					continue;
				}
				ReturnPacket(packet3);
				if (num6 == ffmpeg.AVERROR_EOF && _videoEof)
				{
					_isFinished = true;
				}
				else if (num6 < 0 && num6 != ffmpeg.AVERROR_EOF)
				{
					_isFinished = true;
				}
			}
		}
		ThreadFinished();
	}

	private unsafe void AudioDecodeLoop()
	{
		try
		{
			_lastAudioFpsCalcTime = DateTime.UtcNow;
			while (_isRunning)
			{
				DateTime audioNow = DateTime.UtcNow;
				if ((audioNow - _lastAudioFpsCalcTime).TotalMilliseconds >= 1000.0)
				{
					_stats.AudioDecodeFps = _audioFramesDecodedThisSecond;
					_audioFramesDecodedThisSecond = 0;
					_lastAudioFpsCalcTime = audioNow;
				}
				if (_isPaused && _seekPhase == SeekPhase.None && _seekTargetMs < 0.0)
				{
					Thread.Sleep(10);
					continue;
				}
				if (Interlocked.Exchange(ref _needsAudioFlush, 0) == 1)
				{
					ffmpeg.avcodec_flush_buffers(_audioCodecContext);
					_rebuildAudioFilters = true;
					_baseAudioPtsMs = -1.0;
					if (_swrContext != null)
					{
						SwrContext* swrContext = _swrContext;
						ffmpeg.swr_free(&swrContext);
						_swrContext = null;
					}
					FreeAudioFilterGraph();
					SeekLog($"[SEEK_AUDIO_FLUSH] requested={_seekRequestedMs:F0} lastAudioPts={_lastValidAudioPtsTime:F0}");
				}
				// Hold audio packets until first video frame is shown (start/seek). Continuous
				// playback never starves or stutters audio to "wait for" SW video.
				if (_suppressAudioOutput && HasVideo && _seekPhase == SeekPhase.None)
				{
					Thread.Sleep(5);
					continue;
				}
				if (GetAudioBufferedDurationMs != null)
				{
					double audioBufferTargetMs = GetAudioDecodeBufferTargetMs();
					while (GetAudioBufferedDurationMs() > audioBufferTargetMs && _isRunning && _needsAudioFlush == 0)
					{
						Thread.Sleep(5);
					}
				}
				if (!_audioPacketQueue.TryDequeue(out var result))
				{
					_audioPacketAvailableEvent.Reset();
					if (!_audioPacketQueue.TryDequeue(out result))
					{
						if (_isFinished && !_isPaused && !HasVideo && !_notifiedPlaybackFinished && (GetAudioBufferedDurationMs == null || GetAudioBufferedDurationMs() < 50.0))
						{
							_notifiedPlaybackFinished = true;
							this.PlaybackFinished?.Invoke();
						}
						_audioPacketAvailableEvent.Wait(2);
						continue;
					}
				}
				AVPacket* ptr = (AVPacket*)result;
				Interlocked.Add(ref _audioPacketQueueSizeBytes, -ptr->size);
				int num = ffmpeg.avcodec_send_packet(_audioCodecContext, ptr);
				ReturnPacket(ptr);
				lock (_lock)
				{
					Monitor.PulseAll(_lock);
				}
				if (num < 0)
				{
					continue;
				}
				while (ffmpeg.avcodec_receive_frame(_audioCodecContext, _audioFrame) >= 0)
				{
					if (_audioFrame->nb_samples <= 0 || _audioFrame->extended_data == null || _audioFrame->format < 0)
					{
						continue;
					}
					int num2 = _audioFrame->ch_layout.nb_channels;
					if (num2 == 0)
					{
						num2 = _audioCodecContext->ch_layout.nb_channels;
					}
					if (num2 == 0)
					{
						num2 = AudioChannels;
					}
					bool num3 = ffmpeg.av_sample_fmt_is_planar((AVSampleFormat)_audioFrame->format) != 0;
					bool flag = false;
					if (num3)
					{
						for (int i = 0; i < num2; i++)
						{
							if (_audioFrame->extended_data[i] == null)
							{
								flag = true;
								break;
							}
						}
					}
					else if (*_audioFrame->extended_data == null)
					{
						flag = true;
					}
					if (flag)
					{
						continue;
					}
					if (_rebuildAudioFilters)
					{
						InitAudioFilterGraph();
						_rebuildAudioFilters = false;
						_baseAudioPtsMs = -1.0;
					}
					if (_audioFilterGraph != null && _abufferCtx != null && _abuffersinkCtx != null)
					{
						long num4 = _audioFrame->best_effort_timestamp;
						if (num4 == ffmpeg.AV_NOPTS_VALUE)
						{
							num4 = _audioFrame->pts;
						}
						if (num4 == ffmpeg.AV_NOPTS_VALUE)
						{
							num4 = _audioFrame->pkt_dts;
						}
						if (num4 != ffmpeg.AV_NOPTS_VALUE)
						{
							AVFormatContext* formatContext = ((_audioFormatContext != null) ? _audioFormatContext : _formatContext);
							double normalizedPtsMs = GetNormalizedPtsMs(num4, formatContext, _audioStreamIndex);
							if (_baseAudioPtsMs < 0.0)
							{
								_baseAudioPtsMs = normalizedPtsMs;
								_totalOutputSamples = 0L;
							}
						}
						if (ffmpeg.av_buffersrc_add_frame(_abufferCtx, _audioFrame) < 0)
						{
							continue;
						}
						while (true)
						{
							int num5 = ffmpeg.av_buffersink_get_frame(_abuffersinkCtx, _filteredAudioFrame);
							if (num5 == ffmpeg.AVERROR(ffmpeg.EAGAIN) || num5 == ffmpeg.AVERROR_EOF || num5 < 0)
							{
								break;
							}
							_totalOutputSamples += _filteredAudioFrame->nb_samples;
							if (_baseAudioPtsMs >= 0.0 && _filteredAudioFrame->sample_rate > 0)
							{
								_lastValidAudioPtsTime = _baseAudioPtsMs + (double)_totalOutputSamples * _playbackSpeed * 1000.0 / (double)_filteredAudioFrame->sample_rate;
								if (_isFirstAudioFrame )
								{
									_firstAudioPtsMs = _lastValidAudioPtsTime;
									_isFirstAudioFrame = false;
									UpdateAvStartOffsetFromFirstFrames();
									TryStartPlaybackClock();
								}
							}
							Stopwatch audioDecodeTimer = Stopwatch.StartNew();
							ProcessAndConvertAudioFrame(_filteredAudioFrame);
							audioDecodeTimer.Stop();
							_lastAudioDecodeTimeMs = audioDecodeTimer.Elapsed.TotalMilliseconds;
							_audioFramesDecodedThisSecond++;
							ffmpeg.av_frame_unref(_filteredAudioFrame);
						}
						continue;
					}
					long num6 = _audioFrame->best_effort_timestamp;
					if (num6 == ffmpeg.AV_NOPTS_VALUE)
					{
						num6 = _audioFrame->pts;
					}
					if (num6 == ffmpeg.AV_NOPTS_VALUE)
					{
						num6 = _audioFrame->pkt_dts;
					}
					if (num6 != ffmpeg.AV_NOPTS_VALUE)
					{
						AVFormatContext* formatContext2 = ((_audioFormatContext != null) ? _audioFormatContext : _formatContext);
						double normalizedAudioPtsMs = GetNormalizedPtsMs(num6, formatContext2, _audioStreamIndex);
						_lastValidAudioPtsTime = normalizedAudioPtsMs;
						if (_isFirstAudioFrame)
						{
							_firstAudioPtsMs = _lastValidAudioPtsTime;
							_isFirstAudioFrame = false;
							UpdateAvStartOffsetFromFirstFrames();
							TryStartPlaybackClock();
						}
					}
					else if (_audioCodecContext->sample_rate > 0)
					{
						_lastValidAudioPtsTime += 1000.0 * (double)_audioFrame->nb_samples / (double)_audioCodecContext->sample_rate;
					}
					Stopwatch audioDecodeTimer2 = Stopwatch.StartNew();
					ProcessAndConvertAudioFrame(_audioFrame);
					audioDecodeTimer2.Stop();
					_lastAudioDecodeTimeMs = audioDecodeTimer2.Elapsed.TotalMilliseconds;
					_audioFramesDecodedThisSecond++;
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Error("AudioDecodeLoop unhandled exception", ex);
		}
		ThreadFinished();
	}

	private unsafe void ProcessAndConvertAudioFrame(AVFrame* frameToConvert)
	{
		if (_swrContext == null)
		{
			AVChannelLayout aVChannelLayout = default(AVChannelLayout);
			ffmpeg.av_channel_layout_default(&aVChannelLayout, AudioChannels);
			AVChannelLayout ch_layout = frameToConvert->ch_layout;
			SwrContext* ptr = null;
			if (ffmpeg.swr_alloc_set_opts2(&ptr, &aVChannelLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, AudioSampleRate, &ch_layout, (AVSampleFormat)frameToConvert->format, frameToConvert->sample_rate, 0, null) < 0 || ptr == null)
			{
				return;
			}
			_swrContext = ptr;
			if (ffmpeg.swr_init(_swrContext) < 0)
			{
				fixed (SwrContext** s = &_swrContext)
				{
					ffmpeg.swr_free(s);
				}
				_swrContext = null;
				return;
			}
		}
		if (_swrContext == null)
		{
			return;
		}
		if (_isSeekingAudio)
		{
			if (_seekPhase == SeekPhase.Active)
			{
				if (_videoStreamIndex != -1)
				{
					if (_seekLandPtsMs < 0.0)
					{
						return;
					}
					if (_lastValidAudioPtsTime < _seekLandPtsMs - SeekAudioLeadToleranceMs)
					{
						return;
					}
				}
				_isSeekingAudio = false;
				bool doLand = (_activeSeekGen == 0 || _activeSeekGen == _seekGeneration);
				// RAPID SEEK PROTECTION (same as video)
				lock (_lock)
				{
					if (doLand && _seekLandPtsMs < 0.0)
					{
						// For pure audio (MP3 etc.), prefer the user's requested target time
						// if the decoded frame PTS is unreliable or far off. This helps
						// the UI "point and seek" land closer to what the user clicked.
						_seekLandPtsMs = _seekRequestedMs >= 0 ? _seekRequestedMs : _lastValidAudioPtsTime;
					}
					if (doLand)
					{
						_seekAudioLanded = true;
					}
				}
				if (doLand)
				{
					TryCompleteSeek();
				}
				if (_seekPhase == SeekPhase.Active)
				{
					return;
				}
			}
			else
			{
				_isSeekingAudio = false;
			}
		}
		if (_seekPhase == SeekPhase.Active)
		{
			return;
		}
		if (_suppressAudioOutput)
		{
			return;
		}
		if (_isPaused)
		{
			return;
		}
		int num = ffmpeg.swr_get_out_samples(_swrContext, frameToConvert->nb_samples);
		int num2 = ffmpeg.av_samples_get_buffer_size(null, AudioChannels, num, AVSampleFormat.AV_SAMPLE_FMT_S16, 1);
		if (num2 < 0 || num2 > _audioMaxBufferSize)
		{
			return;
		}
		byte* audioBufferPointer = (byte*)_audioBufferPointer;
		byte** @out = &audioBufferPointer;
		int num3 = ffmpeg.swr_convert(_swrContext, @out, num, frameToConvert->extended_data, frameToConvert->nb_samples);
		if (num3 > 0)
		{
			int num4 = ffmpeg.av_samples_get_buffer_size(null, AudioChannels, num3, AVSampleFormat.AV_SAMPLE_FMT_S16, 1);
			if (num4 > 0 && num4 <= _audioMaxBufferSize)
			{
				byte[] array = _audioBufferPool.Rent(num4);
				try
				{
					Marshal.Copy(_audioBufferPointer, array, 0, num4);
					this.AudioDataAvailable?.Invoke(array, num4, _lastValidAudioPtsTime);
				}
				finally
				{
					_audioBufferPool.Return(array);
				}
				// Timeline follows audio master for both A/V and audio-only (never video decode PTS).
				if (_seekPhase == SeekPhase.None && !_suppressAudioOutput)
				{
					double num6 = GetMasterClockPts() / 1000.0;
					if (num6 < 0.0)
					{
						num6 = 0.0;
					}
					double num7 = DurationSeconds;
					if (num7 <= 0.0 && _formatContext != null)
					{
						num7 = (double)_formatContext->duration / 1000000.0;
					}
					if (num7 > 0.05)
					{
						this.PositionChanged?.Invoke(Math.Clamp(num6 / num7, 0.0, 1.0));
					}
					this.TimeUpdated?.Invoke(TimeSpan.FromSeconds(num6), TimeSpan.FromSeconds(num7));
				}
			}
		}
		if (_isFirstAudioFrame)
		{
			_isFirstAudioFrame = false;
		}
	}

	private static double SnapToStandardFps(double fps)
	{
		if (fps <= 0.0)
		{
			return 24.0;
		}
		if (fps > 59.0 && fps < 61.0)
		{
			return NtscDoubleFps;
		}
		if (fps > 58.0 && fps < 62.0)
		{
			return 60.0;
		}
		if (fps > 47.5 && fps < 52.5)
		{
			return 50.0;
		}
		if (fps > 29.4 && fps < 30.1)
		{
			return NtscVideoFps;
		}
		if (fps > 29.0 && fps < 30.5)
		{
			return 30.0;
		}
		if (fps > 23.5 && fps < 24.1)
		{
			return NtscFilmFps;
		}
		if (fps > 23.5 && fps < 24.5)
		{
			return 24.0;
		}
		return fps;
	}

	private static double RationalToFps(AVRational rational)
	{
		if (rational.num <= 0 || rational.den <= 0)
		{
			return 0.0;
		}
		return (double)rational.num / rational.den;
	}

	private static double SnapRationalFps(AVRational rational)
	{
		if (rational.num <= 0 || rational.den <= 0)
		{
			return 0.0;
		}
		if (rational.num == 24000 && rational.den == 1001)
		{
			return NtscFilmFps;
		}
		if (rational.num == 30000 && rational.den == 1001)
		{
			return NtscVideoFps;
		}
		if (rational.num == 60000 && rational.den == 1001)
		{
			return NtscDoubleFps;
		}
		return SnapToStandardFps(RationalToFps(rational));
	}

	private unsafe double ResolveStreamTargetFps()
	{
		AVStream* stream = _formatContext->streams[_videoStreamIndex];
		double avgFps = SnapRationalFps(stream->avg_frame_rate);
		double rFps = SnapRationalFps(stream->r_frame_rate);
		double codecFps = 0.0;
		if (_videoCodecContext != null && _videoCodecContext->framerate.num > 0 && _videoCodecContext->framerate.den > 0)
		{
			codecFps = SnapRationalFps(_videoCodecContext->framerate);
		}
		if (TryGetCodecparFps(stream->codecpar, out double parFps))
		{
			codecFps = Math.Max(codecFps, parFps);
		}

		double fps = 0.0;
		if (avgFps >= 23.0 && avgFps <= 240.0)
		{
			fps = avgFps;
		}
		else
		{
			foreach (double candidate in new[] { rFps, codecFps })
			{
				if (candidate >= 23.0 && candidate <= 240.0)
				{
					fps = (fps <= 0.0) ? candidate : Math.Min(fps, candidate);
				}
			}
		}
		if (fps <= 0.0)
		{
			fps = 24.0;
		}
		return fps;
	}

	private unsafe static bool TryGetCodecparFps(AVCodecParameters* codecpar, out double fps)
	{
		fps = 0.0;
		if (codecpar == null || codecpar->framerate.num <= 0 || codecpar->framerate.den <= 0)
		{
			return false;
		}
		fps = SnapRationalFps(codecpar->framerate);
		return fps >= 23.0 && fps <= 240.0;
	}

	private unsafe void ResetFpsMeasurement(bool resetTargetFromStream)
	{
		_fpsMeasureLastPtsMs = -1.0;
		if (resetTargetFromStream && HasVideo && _formatContext != null && _videoStreamIndex >= 0)
		{
			_streamFpsBaseline = ResolveStreamTargetFps();
			_stats.TargetFps = _streamFpsBaseline;
		}
	}

	private void RefineTargetFpsFromPts(double ptsMs)
	{
		if (_fpsMeasureLastPtsMs >= 0.0)
		{
			double ptsDelta = ptsMs - _fpsMeasureLastPtsMs;
			if (ptsDelta > 4.0 && ptsDelta < 200.0)
			{
				double instantFps = 1000.0 / ptsDelta;
				if (instantFps >= 23.0 && instantFps <= 240.0)
				{
					double current = _stats.TargetFps;
					if (current < 23.0 || current > 240.0)
					{
						current = _streamFpsBaseline > 0.0 ? _streamFpsBaseline : instantFps;
					}
					double smoothed = current + FpsRefineSmoothingAlpha * (instantFps - current);
					double baseline = _streamFpsBaseline > 0.0 ? _streamFpsBaseline : current;
					smoothed = Math.Clamp(smoothed, baseline * 0.85, baseline * 1.15);
					_stats.TargetFps = SnapToStandardFps(smoothed);
				}
			}
		}
		_fpsMeasureLastPtsMs = ptsMs;
	}

	private unsafe void VideoDecodeLoop()
	{
		double num = (double)_formatContext->duration / 1000000.0;
		TimeSpan arg = TimeSpan.FromSeconds(num);
		ResetFpsMeasurement(resetTargetFromStream: true);
		_stats.LateFrames = 0;
		_framesDecodedThisSecond = 0;
		_totalDecodeTimeMs = 0.0;
		_decodeTimeSamples = 0;
		_lastFpsCalcTime = DateTime.UtcNow;
		Stopwatch stopwatch = new Stopwatch();
		Stopwatch stopwatch2 = new Stopwatch();
		int srcRange = default(int);
		int dstRange = default(int);
		int num7 = default(int);
		int num8 = default(int);
		int num9 = default(int);
		while (_isRunning)
		{
			if (_isPaused && _videoDecodeWarmup == 0 && _seekPhase == SeekPhase.None && _seekTargetMs < 0.0)
			{
				Thread.Sleep(10);
				continue;
			}
			if (Interlocked.Exchange(ref _needsVideoFlush, 0) == 1)
			{
				ffmpeg.avcodec_flush_buffers(_videoCodecContext);
				_isSeekingVideo = true;
			}
			if (!_videoPacketQueue.TryDequeue(out var result))
			{
				// Only signal end-of-playback when demux is done, no more packets to decode,
				// decoded frames have been drained (displayed), and audio output is nearly empty.
				// Firing on packet-empty alone was premature: auto-next cut the last frames/audio
				// and raced with PlayFile setup (stuck _isOpeningFile / missed playlist advance).
				// End only when demux is done, video drained, AND audio packets+output drained.
				// Finishing on video-empty alone cut 2min files short when video free-ran at decode FPS.
				if (_isFinished
					&& !_notifiedPlaybackFinished
					&& _decodedVideoQueue.IsEmpty
					&& _audioPacketQueue.IsEmpty
					&& (GetAudioBufferedDurationMs == null || GetAudioBufferedDurationMs() < 50.0))
				{
					_notifiedPlaybackFinished = true;
					this.PlaybackFinished?.Invoke();
				}
				Thread.Sleep(2);
				continue;
			}
			AVPacket* ptr = (AVPacket*)result;
			int num3 = ffmpeg.avcodec_send_packet(_videoCodecContext, ptr);
			ReturnPacket(ptr);
			lock (_lock)
			{
				Monitor.PulseAll(_lock);
			}
			if (num3 < 0)
			{
				continue;
			}
			while (true)
			{
				stopwatch2.Restart();
				if (ffmpeg.avcodec_receive_frame(_videoCodecContext, _videoFrame) < 0)
				{
					break;
				}
				stopwatch2.Stop();
				_lastVideoDecodeTimeMs = stopwatch2.Elapsed.TotalMilliseconds;
				bool flag = _videoFrame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11;
				_lastDecodedFrameIsD3D11 = flag;
				_isSoftwareVideoDecode = !flag;
				_stats.IsRealHwAccel = flag;
				if (!flag && _videoCodecContext->hw_device_ctx != null && IsHighResolution(_videoCodecContext->width, _videoCodecContext->height))
				{
					// One-shot log; NV12 path below may still display. BGRA remains blocked for high-res+hw_ctx.
					if (_loggedSwBgraFallbackFormat != _videoFrame->format)
					{
						_loggedSwBgraFallbackFormat = _videoFrame->format;
						SeekLog($"[HW_RUNTIME_FAIL] expected D3D11 got format={_videoFrame->format} at {_videoCodecContext->width}x{_videoCodecContext->height} — attempting SW convert");
					}
				}
				IntPtr texturePtr = IntPtr.Zero;
				int sliceIndexOrStride = 0;
				if (flag)
				{
					texturePtr = new IntPtr(_videoFrame->data[0u]);
					sliceIndexOrStride = (int)new IntPtr(_videoFrame->data[1u]).ToInt64();
				}
				AVFrame* ptr2 = _videoFrame;
				AVFrame* ptr3 = null;
				if (!flag && IsHardwarePixelFormat((AVPixelFormat)_videoFrame->format))
				{
					ptr3 = GetFrame();
					if (ffmpeg.av_hwframe_transfer_data(ptr3, _videoFrame, 0) != 0)
					{
						ReturnFrame(ptr3);
						ptr3 = null;
						continue;
					}
					ptr3->pts = _videoFrame->pts;
					ptr3->best_effort_timestamp = _videoFrame->best_effort_timestamp;
					ptr3->pkt_dts = _videoFrame->pkt_dts;
					ptr2 = ptr3;
				}
				if (!flag && (ptr2->width <= 0 || ptr2->height <= 0))
				{
					if (ptr3 != null)
					{
						ReturnFrame(ptr3);
					}
					continue;
				}
				long num4 = _videoFrame->best_effort_timestamp;
				if (num4 == ffmpeg.AV_NOPTS_VALUE)
				{
					num4 = _videoFrame->pts;
				}
				if (num4 == ffmpeg.AV_NOPTS_VALUE)
				{
					num4 = _videoFrame->pkt_dts;
				}
				double num5 = 0.0;
				if (num4 != ffmpeg.AV_NOPTS_VALUE)
				{
					num5 = (_lastValidPtsTime = GetNormalizedPtsMs(num4, _formatContext, _videoStreamIndex));
				}
				else
				{
					double num6 = 33.3;
					if (_videoCodecContext->framerate.num > 0 && _videoCodecContext->framerate.den > 0)
					{
						num6 = 1000.0 * ffmpeg.av_q2d(ffmpeg.av_inv_q(_videoCodecContext->framerate));
					}
					num5 = (_lastValidPtsTime += num6);
				}
				_width = ptr2->width;
				_height = ptr2->height;
				RefineTargetFpsFromPts(num5);
				if (_isSeekingVideo)
				{
					_isSeekingVideo = false;
					bool doLand = (_activeSeekGen == 0 || _activeSeekGen == _seekGeneration);
					// RAPID SEEK PROTECTION: only land for the latest seek gen.
					// Older seeks' frames are ignored so we don't complete a stale seek.
					lock (_lock)
					{
						if (_seekPhase == SeekPhase.Active && _seekLandPtsMs < 0.0 && doLand)
						{
							_seekLandPtsMs = num5;
						}
						if (doLand)
						{
							_seekVideoLanded = true;
						}
					}
					if (doLand)
					{
						SeekLog($"[SEEK_VIDEO_LAND] pts={num5:F0} requested={_seekRequestedMs:F0}");
						TryCompleteSeek();
					}
				}
				lock (_lock)
				{
					if (_speedChanged)
					{
						if (stopwatch.IsRunning)
						{
							stopwatch.Restart();
						}
						_speedChanged = false;
					}
				}
				_ = _isPreBuffering;
				DecodedVideoFrame decodedVideoFrame = new DecodedVideoFrame
				{
					PtsTime = num5,
					Width = _width,
					Height = _height,
					IsD3D11 = flag
				};
				if (flag)
				{
					decodedVideoFrame.TexturePtr = texturePtr;
					decodedVideoFrame.SliceIndexOrStride = sliceIndexOrStride;
					decodedVideoFrame.AvFrame = ffmpeg.av_frame_clone(_videoFrame);
					ReturnFrame(_videoFrame);
					_videoFrame = GetFrame();
				}
				else
				{
					bool usedGpuNv12 = TryFillGpuNv12DecodedFrame(ptr2, decodedVideoFrame);
					if (usedGpuNv12)
					{
						if (ptr3 != null)
						{
							ReturnFrame(ptr3);
						}
						ReturnFrame(_videoFrame);
						_videoFrame = GetFrame();
					}
					else
					{
					if (IsHighResolution(_width, _height) && _videoCodecContext->hw_device_ctx != null)
					{
						SeekLog($"[SW_BGRA_BLOCKED] {_width}x{_height} format={ptr2->format} — 4K/8K CPU BGRA not allowed (use GPU YUV or D3D11VA)");
						decodedVideoFrame.Dispose();
						if (ptr3 != null)
						{
							ReturnFrame(ptr3);
						}
						ReturnFrame(_videoFrame);
						_videoFrame = GetFrame();
						continue;
					}
					if (ptr2->format != _loggedSwBgraFallbackFormat)
					{
						_loggedSwBgraFallbackFormat = ptr2->format;
						SeekLog($"[SW_BGRA_FALLBACK] {_width}x{_height} format={ptr2->format} — GPU NV12 path unavailable");
					}
					decodedVideoFrame.BufferLayout = SwVideoBufferLayout.Bgra;
					int bgraBytes = _width * _height * 4;
					if (_swsBgraBufferSize < bgraBytes)
					{
						FreeSwsBgraPool();
						for (int i = 0; i < SwsBgraPoolSize; i++)
						{
							_swsBgraPointers[i] = (IntPtr)NativeMemory.Alloc((nuint)bgraBytes);
						}
						_swsBgraBufferSize = bgraBytes;
						if (_swsContext != null)
						{
							ffmpeg.sws_freeContext(_swsContext);
							_swsContext = null;
							_swsSrcFormat = AVPixelFormat.AV_PIX_FMT_NONE;
						}
					}
					AVPixelFormat srcPixelFormat = (AVPixelFormat)ptr2->format;
					if (_swsContext == null || _swsSrcFormat != srcPixelFormat)
					{
						if (_swsContext != null)
						{
							ffmpeg.sws_freeContext(_swsContext);
							_swsContext = null;
						}
						int swsFlags = GetSwsFlags(ptr2->width, ptr2->height, _width, _height);
						_swsContext = ffmpeg.sws_getContext(ptr2->width, ptr2->height, srcPixelFormat, _width, _height, AVPixelFormat.AV_PIX_FMT_BGRA, swsFlags, null, null, null);
						_swsSrcFormat = srcPixelFormat;
						// Force re-apply of any brightness/contrast/saturation on freshly created context
						_videoFiltersChanged = true;
					}
					if (_swsContext == null)
					{
						decodedVideoFrame.Dispose();
						if (ptr3 != null)
						{
							ReturnFrame(ptr3);
						}
						ReturnFrame(_videoFrame);
						_videoFrame = GetFrame();
						continue;
					}
					if (_videoFiltersChanged)
					{
						int* ptr4 = null;
						int* ptr5 = null;
						if (ffmpeg.sws_getColorspaceDetails(_swsContext, &ptr4, &srcRange, &ptr5, &dstRange, &num7, &num8, &num9) >= 0 && ptr4 != null && ptr5 != null)
						{
							int_array4 inv_table = default(int_array4);
							inv_table[0u] = *ptr4;
							inv_table[1u] = ptr4[1];
							inv_table[2u] = ptr4[2];
							inv_table[3u] = ptr4[3];
							int_array4 table = default(int_array4);
							table[0u] = *ptr5;
							table[1u] = ptr5[1];
							table[2u] = ptr5[2];
							table[3u] = ptr5[3];
							ffmpeg.sws_setColorspaceDetails(_swsContext, in inv_table, srcRange, in table, dstRange, (int)(VideoBrightness * 255.0 * 65536.0), (int)(VideoContrast * 65536.0), (int)(VideoSaturation * 65536.0));
						}
						_videoFiltersChanged = false;
					}

					// Rotate pool and prepare destination using pre-allocated arrays (no 'new' per frame)
					_swsBgraBufferIndex = (_swsBgraBufferIndex + 1) % SwsBgraPoolSize;
					var bgraPtr = _swsBgraPointers[_swsBgraBufferIndex];
					decodedVideoFrame.BgraPointer = bgraPtr;
					decodedVideoFrame.Width = _width;
					decodedVideoFrame.Height = _height;
					decodedVideoFrame.SliceIndexOrStride = _width * 4;

					if (_swsDstData == null)
					{
						_swsDstData = new byte*[8];
						// strides remain zero except [0] which we overwrite every frame
					}

					_swsDstData[0] = (byte*)bgraPtr;
					_swsDstStrides[0] = _width * 4;

					// Use cached arrays to eliminate per-frame allocation in hot path
					var swsTimer = Stopwatch.StartNew();
					ffmpeg.sws_scale(dst: _swsDstData, dstStride: _swsDstStrides, c: _swsContext, srcSlice: ptr2->data, srcStride: ptr2->linesize, srcSliceY: 0, srcSliceH: ptr2->height);
					swsTimer.Stop();
					if (ptr3 != null)
					{
						ReturnFrame(ptr3);
					}

					double swsMs = swsTimer.Elapsed.TotalMilliseconds;
					_totalDecodeTimeMs += swsMs;
					_decodeTimeSamples++;
					_stats.SwsConvertTimeMs = swsMs;
					ReturnFrame(_videoFrame);
					_videoFrame = GetFrame();
					}
				}
				int maxQueueSize = GetDecodedFrameQueueLimit();
				if (!flag)
				{
					// SW jitter buffer — large enough to absorb CPU spikes; pool is 16 slots.
					maxQueueSize = Math.Min(maxQueueSize, GetSoftwareDecodedFrameQueueCap());
				}
				if (_decodedVideoQueue.Count >= maxQueueSize && _isRunning && !_isSeekingVideo)
				{
					Stopwatch poolWaitTimer = Stopwatch.StartNew();
					while (_decodedVideoQueue.Count >= maxQueueSize && _isRunning && !_isSeekingVideo)
					{
						Thread.Sleep(5);
					}
					poolWaitTimer.Stop();
					_lastPoolWaitMs = poolWaitTimer.Elapsed.TotalMilliseconds;
				}
				if (_isRunning && !_isSeekingVideo)
				{
					_decodedVideoQueue.Enqueue(decodedVideoFrame);
				}
				else
				{
					decodedVideoFrame.Dispose();
				}
				if (_isFirstVideoFrame)
				{
					_isFirstVideoFrame = false;
					_firstVideoPtsMs = num5;
					UpdateAvStartOffsetFromFirstFrames();
					TryStartPlaybackClock();
				}
				_framesDecodedThisSecond++;
				DateTime utcNow = DateTime.UtcNow;
				if ((utcNow - _lastFpsCalcTime).TotalMilliseconds >= 1000.0)
				{
					_stats.ActualFps = _framesDecodedThisSecond;
					_stats.VideoDecodeFps = _framesDecodedThisSecond;
					if (_decodeTimeSamples > 0)
					{
						_stats.AvgDecodeTimeMs = _totalDecodeTimeMs / (double)_decodeTimeSamples;
					}
					_framesDecodedThisSecond = 0;
					_totalDecodeTimeMs = 0.0;
					_decodeTimeSamples = 0;
					_lastFpsCalcTime = utcNow;
				}
				// UI timeline follows audio master (not video decode PTS — that raced to EOF at V.DecodeFPS).
				if (_seekPhase == SeekPhase.None && !_suppressAudioOutput && _audioStreamIndex == -1)
				{
					double num10 = num5 / 1000.0;
					double obj = num > 0 ? num10 / num : 0;
					this.PositionChanged?.Invoke(obj);
					this.TimeUpdated?.Invoke(TimeSpan.FromSeconds(num10), arg);
				}
			}
		}
		ThreadFinished();
	}

	public bool TryPeekNextQueuedFramePts(out double ptsMs)
	{
		if (_decodedVideoQueue.TryPeek(out DecodedVideoFrame? frame))
		{
			ptsMs = frame.PtsTime;
			return true;
		}
		ptsMs = 0.0;
		return false;
	}

	/// <summary>
	/// Returns the first decoded frame after a completed seek, bypassing A/V pacing.
	/// Safe to call while paused so the renderer can update the still image.
	/// </summary>
	public DecodedVideoFrame? TryPullPostSeekDisplayFrame()
	{
		if (_seekPhase == SeekPhase.Active || !_awaitingPostSeekVideoDisplay)
		{
			return null;
		}
		if (_decodedVideoQueue.TryDequeue(out DecodedVideoFrame? landFrame))
		{
			MarkVideoFrameDisplayed(landFrame.PtsTime);
			_awaitingPostSeekVideoDisplay = false;
			_postSeekTargetMs = -1.0;
			return landFrame;
		}
		return null;
	}

	public DecodedVideoFrame? PullVideoFrame(double masterClockPts)
	{
		DecodedVideoFrame? postSeekFrame = TryPullPostSeekDisplayFrame();
		if (postSeekFrame != null)
		{
			return postSeekFrame;
		}
		if (_seekPhase == SeekPhase.Active)
		{
			return null;
		}

		EnsurePlaybackClockStarted();

		// === AUDIO IS THE ONLY MASTER ===
		// Video may only present when its PTS is due on the audio playhead.
		// Never present at V.DecodeFPS — that burns a 24fps stream at 32fps and ends 2min files at ~1.5min.
		masterClockPts = GetMasterClockPts();
		double streamOffset = GetAvStartOffsetMs(); // ~0 for muxed files
		double targetVideoPts = masterClockPts + streamOffset;

		double frameIntervalMs = (_stats.TargetFps > 1.0) ? (1000.0 / _stats.TargetFps) : 40.0;
		// Hold early frames strictly; drop late only when we still have a spare frame.
		double lateThresholdMs = Math.Max(80.0, frameIntervalMs * 2.5);
		double earlyThresholdMs = Math.Max(20.0, frameIntervalMs * 0.75);

		// First paint: optional SW prebuffer, then release audio. Do not free-run.
		if (_lastDisplayedVideoPtsMs < 0.0)
		{
			if (_isSoftwareVideoDecode && _decodedVideoQueue.Count < SwStartupMinDecodedFrames)
			{
				return null;
			}
			// Wait for audio clock if we already have audio (avoid stopwatch free-run).
			if (_audioStreamIndex != -1 && _firstAudioPtsMs < 0.0 && !_suppressAudioOutput)
			{
				// Audio not clocked yet — still OK to show first frame and let audio catch.
			}
			if (_decodedVideoQueue.TryDequeue(out DecodedVideoFrame? bootstrapFrame))
			{
				MarkVideoFrameDisplayed(bootstrapFrame.PtsTime);
				// Only seed stopwatch fallback; live pace uses audio playhead once available.
				if (_audioStreamIndex == -1 || double.IsNaN(GetAudioPlayheadPts()))
				{
					SyncPlaybackClockToPts(bootstrapFrame.PtsTime - streamOffset);
				}
				return bootstrapFrame;
			}
			return null;
		}

		// Drop late frames (behind audio) while keeping at least one candidate.
		int maxDrops = 12;
		while (maxDrops-- > 0
			&& _decodedVideoQueue.Count > 1
			&& _decodedVideoQueue.TryPeek(out DecodedVideoFrame? lateCandidate)
			&& lateCandidate.PtsTime < targetVideoPts - lateThresholdMs
			&& _decodedVideoQueue.TryDequeue(out DecodedVideoFrame? lateFrame))
		{
			Interlocked.Increment(ref _droppedFrameCount);
			Interlocked.Increment(ref _lateFrameCount);
			lateFrame.Dispose();
		}

		if (!_decodedVideoQueue.TryPeek(out DecodedVideoFrame? head))
		{
			return null;
		}

		double pts = head.PtsTime;

		// EARLY: video ahead of audio → wait (this is how duration stays correct).
		if (pts > targetVideoPts + earlyThresholdMs)
		{
			return null;
		}

		// Due or slightly late: show this frame.
		if (_decodedVideoQueue.TryDequeue(out DecodedVideoFrame? presentFrame))
		{
			MarkVideoFrameDisplayed(presentFrame.PtsTime);
			return presentFrame;
		}
		return null;
	}

	private unsafe void Cleanup()
	{
		if (Interlocked.Exchange(ref _isCleanedUp, 1) != 1)
		{
			// Drain queues before releasing codec/format contexts (packets/frames must not outlive them).
			while (_videoPacketQueue.TryDequeue(out IntPtr queuedVideoPacket))
			{
				AVPacket* pkt = (AVPacket*)queuedVideoPacket;
				ffmpeg.av_packet_free(&pkt);
			}
			while (_audioPacketQueue.TryDequeue(out IntPtr queuedAudioPacket))
			{
				AVPacket* pkt = (AVPacket*)queuedAudioPacket;
				ffmpeg.av_packet_free(&pkt);
			}
			while (_decodedVideoQueue.TryDequeue(out DecodedVideoFrame? decodedFrame))
			{
				decodedFrame.Dispose();
			}
			_videoPacketQueueSizeBytes = 0;
			_audioPacketQueueSizeBytes = 0;

			// Use centralized helper for SW BGRA pool (prevents duplication and ensures reset)
			FreeSwsBgraPool();
			FreeSwNv12Pool();

			if (_audioBufferHandle.IsAllocated)
			{
				_audioBufferHandle.Free();
				_audioBufferHandle = default(GCHandle);
			}
			_audioBufferPointer = IntPtr.Zero;
			_audioBuffer = null;

			if (_swsContext != null)
			{
				ffmpeg.sws_freeContext(_swsContext);
				_swsContext = null;
				_swsSrcFormat = AVPixelFormat.AV_PIX_FMT_NONE;
			}
			if (_swsNv12Context != null)
			{
				ffmpeg.sws_freeContext(_swsNv12Context);
				_swsNv12Context = null;
				_swsNv12SrcFormat = AVPixelFormat.AV_PIX_FMT_NONE;
			}
			if (_swrContext != null)
			{
				SwrContext* swrContext = _swrContext;
				ffmpeg.swr_free(&swrContext);
				_swrContext = null;
			}

			// Reset key state to prevent dangling references on reuse
			_swsBgraBufferIndex = 0;
			_swsSrcFormat = AVPixelFormat.AV_PIX_FMT_NONE;
			FreeAudioFilterGraph();
			if (_filteredAudioFrame != null)
			{
				AVFrame* filteredAudioFrame = _filteredAudioFrame;
				ffmpeg.av_frame_free(&filteredAudioFrame);
				_filteredAudioFrame = null;
			}
			if (_audioFrame != null)
			{
				AVFrame* audioFrame = _audioFrame;
				ffmpeg.av_frame_free(&audioFrame);
				_audioFrame = null;
			}
			if (_videoFrame != null)
			{
				AVFrame* videoFrame = _videoFrame;
				ffmpeg.av_frame_free(&videoFrame);
				_videoFrame = null;
			}
			if (_previousD3D11Frame != null)
			{
				AVFrame* previousD3D11Frame = _previousD3D11Frame;
				ffmpeg.av_frame_free(&previousD3D11Frame);
				_previousD3D11Frame = null;
			}
			if (_packet != null)
			{
				AVPacket* packet = _packet;
				ffmpeg.av_packet_free(&packet);
				_packet = null;
			}

			while (_packetPool.TryTake(out IntPtr pooledPacket))
			{
				AVPacket* pkt = (AVPacket*)pooledPacket;
				ffmpeg.av_packet_free(&pkt);
			}
			while (_framePool.TryTake(out IntPtr pooledFrame))
			{
				AVFrame* frame = (AVFrame*)pooledFrame;
				ffmpeg.av_frame_free(&frame);
			}

			if (_videoCodecContext != null)
			{
				ClearVideoHwDevice(_videoCodecContext);
				AVCodecContext* videoCtx = _videoCodecContext;
				ffmpeg.avcodec_free_context(&videoCtx);
				_videoCodecContext = null;
			}
			if (_audioCodecContext != null)
			{
				AVCodecContext* audioCtx = _audioCodecContext;
				ffmpeg.avcodec_free_context(&audioCtx);
				_audioCodecContext = null;
			}
			if (_audioFormatContext != null)
			{
				AVFormatContext* audioFmt = _audioFormatContext;
				ffmpeg.avformat_close_input(&audioFmt);
				_audioFormatContext = null;
			}
			if (_formatContext != null)
			{
				AVFormatContext* fmt = _formatContext;
				ffmpeg.avformat_close_input(&fmt);
				_formatContext = null;
			}

			_videoStreamIndex = -1;
			_audioStreamIndex = -1;
			_getFormatCallback = null;
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposeState, 1) == 1)
		{
			return;
		}
		_isDisposed = true;
		try
		{
			Stop();
		}
		catch (Exception ex)
		{
			Logger.Error("Stop failed during decoder dispose", ex);
		}

		JoinWorkerThreads(8000);
		if (AreWorkerThreadsAlive())
		{
			Logger.Warn("Dispose: worker threads still alive after first join; retrying.");
			Stop();
			JoinWorkerThreads(4000);
		}

		if (!AreWorkerThreadsAlive())
		{
			Interlocked.Exchange(ref _activeThreads, 0);
			ForceCleanupIfNeeded();
			_readThread = null;
			_videoThread = null;
			_audioThread = null;
			DisposeSyncEvents();
		}
		else
		{
			Logger.Warn("Dispose: worker threads still alive; deferring FFmpeg cleanup until background join completes.");
			ScheduleDeferredCleanup();
		}

		GC.SuppressFinalize(this);
	}
}
