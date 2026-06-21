using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;

namespace JonPlayer;

public class FFmpegMediaDecoder : IDisposable
{
	public class DecodedVideoFrame : IDisposable
	{
		public IntPtr TexturePtr;

		public byte[]? BgraBuffer;

		public GCHandle BgraHandle;

		public IntPtr BgraPointer;

		public int Width;

		public int Height;

		public int SliceIndexOrStride;

		public bool IsD3D11;

		public double PtsTime;

		public unsafe AVFrame* AvFrame;

		public unsafe void Dispose()
		{
			if (AvFrame != null)
			{
				fixed (AVFrame** frame = &AvFrame)
				{
					ffmpeg.av_frame_free(frame);
				}
				AvFrame = null;
			}
			if (BgraHandle.IsAllocated)
			{
				BgraHandle.Free();
			}
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

	private double _lastValidPtsTime;

	private double _baseAudioPtsMs = -1.0;

	private long _totalOutputSamples;

	private double _lastValidAudioPtsTime;

	private byte[]? _bgraBuffer;

	private GCHandle _bgraBufferHandle;

	private IntPtr _bgraBufferPointer;

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

	private ConcurrentQueue<IntPtr> _audioPacketQueue = new ConcurrentQueue<IntPtr>();

	private ConcurrentBag<IntPtr> _packetPool = new ConcurrentBag<IntPtr>();

	private ConcurrentBag<IntPtr> _framePool = new ConcurrentBag<IntPtr>();

	private ConcurrentQueue<DecodedVideoFrame> _decodedVideoQueue = new ConcurrentQueue<DecodedVideoFrame>();

	private const int MaxDecodedQueueSize = 5;

	private double _avStartOffsetMs;

	private volatile bool _isFirstVideoFrame = true;

	private volatile bool _isFirstAudioFrame = true;

	private volatile bool _isPreBuffering;

	private double _firstAudioPtsMs = -1.0;

	private double _firstVideoPtsMs = -1.0;

	private volatile bool _isRunning;

	private volatile bool _isPaused;

	private readonly object _lock = new object();

	private Stopwatch _masterClockStopwatch = new Stopwatch();

	private double _currentPlaybackPtsTime;

	private string? _currentPath;

	private string? _separateAudioUrl;

	private double _seekTargetMs = -1.0;

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

	private double _seekTargetPtsTime = -1.0;

	private bool _isDisposed;

	private int _isOpening;

	private volatile bool _videoFiltersChanged;

	private volatile bool _rebuildAudioFilters;

	private InterruptCallbackDelegate? _interruptCallback;

	private volatile bool _isInterruptRequested;

	private DecoderStats _stats;

	private int _framesDecodedThisSecond;

	private DateTime _lastFpsCalcTime;

	private double _totalDecodeTimeMs;

	private int _decodeTimeSamples;

	private IntPtr _d3d11DevicePtr;

	private IntPtr _d3d11ContextPtr;

	private int _activeThreads;

	private int _isCleanedUp;

	private int _disposeState;

	public int AudioSampleRate { get; private set; }

	public int AudioChannels { get; private set; }

	public bool IsRunning => _isRunning;

	public bool IsPaused => _isPaused;

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

	public event Action<IntPtr, int, int, int, bool>? FrameDecoded;

	public event Action<byte[], int>? AudioDataAvailable;

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
		_packetPool.Add((nint)pkt);
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
		_framePool.Add((nint)frame);
	}

	public double GetMasterClockPts()
	{
		if (_isPaused || !_masterClockStopwatch.IsRunning)
		{
			return _currentPlaybackPtsTime;
		}
		double num = (double)_masterClockStopwatch.ElapsedMilliseconds * _playbackSpeed;
		_ = _currentPlaybackPtsTime;
		double num2 = _currentPlaybackPtsTime + (double)_masterClockStopwatch.ElapsedMilliseconds * _playbackSpeed;
		if (_audioStreamIndex != -1 && GetAudioBufferedDurationMs != null && _lastValidAudioPtsTime > 0.0 && !_isSeekingAudio)
		{
			double num3 = GetAudioBufferedDurationMs();
			double num4 = ((GetAudioHardwareLatencyMs != null) ? GetAudioHardwareLatencyMs() : 0.0);
			double num5 = _lastValidAudioPtsTime - (num3 + num4) * _playbackSpeed;
			double num6 = num5 - num2;
			if (num6 > 2000.0 || num6 < -2000.0)
			{
				_currentPlaybackPtsTime = num5;
				_masterClockStopwatch.Restart();
				return num5;
			}
			if (num6 > 50.0 || num6 < -50.0)
			{
				_currentPlaybackPtsTime = num2 + num6 * 0.1;
				_masterClockStopwatch.Restart();
				return _currentPlaybackPtsTime;
			}
		}
		return num2;
	}

	private unsafe static double GetStreamStartOffsetMs(AVFormatContext* formatContext, int streamIndex)
	{
		if (formatContext == null || streamIndex < 0 || streamIndex >= formatContext->nb_streams)
		{
			return 0.0;
		}
		AVStream* ptr = formatContext->streams[streamIndex];
		if (ptr != null && ptr->start_time != ffmpeg.AV_NOPTS_VALUE)
		{
			return (double)ptr->start_time * ffmpeg.av_q2d(ptr->time_base) * 1000.0;
		}
		if (formatContext->start_time != ffmpeg.AV_NOPTS_VALUE)
		{
			return (double)formatContext->start_time * 1000.0 / 1000000.0;
		}
		return 0.0;
	}

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
		return (double)pts * ffmpeg.av_q2d(ptr->time_base) * 1000.0 - GetStreamStartOffsetMs(formatContext, streamIndex);
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

	private unsafe int InterruptCallback(void* opaque)
	{
		return _isInterruptRequested ? 1 : 0;
	}

	public DecoderStats GetStats()
	{
		_stats.PacketQueueSize = _videoPacketQueue.Count;
		_stats.AudioQueueSize = _audioPacketQueue.Count;
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
		_seekTargetMs = -1.0;
		_seekTargetPtsTime = -1.0;
		_isSeekingVideo = false;
		_isSeekingAudio = false;
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
				AVCodec* codec = ffmpeg.avcodec_find_decoder(codecpar2->codec_id);
				_videoCodecContext = ffmpeg.avcodec_alloc_context3(codec);
				ffmpeg.avcodec_parameters_to_context(_videoCodecContext, codecpar2);
				_videoCodecContext->thread_count = 0;
				_videoCodecContext->thread_type = 3;
				_getFormatCallback = GetFormat;
				_videoCodecContext->get_format = new AVCodecContext_get_format_func
				{
					Pointer = Marshal.GetFunctionPointerForDelegate(_getFormatCallback)
				};
				AVBufferRef* ptr6 = null;
				if (EnableHwAccel && _d3d11DevicePtr != IntPtr.Zero)
				{
					ptr6 = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
					if (ptr6 != null)
					{
						AVHWDeviceContext* data2 = (AVHWDeviceContext*)ptr6->data;
						AVD3D11VADeviceContext* hwctx = (AVD3D11VADeviceContext*)data2->hwctx;
						Marshal.AddRef(_d3d11DevicePtr);
						hwctx->device = (ID3D11Device*)_d3d11DevicePtr;
						hwctx->device_context = (ID3D11DeviceContext*)IntPtr.Zero;
						if (ffmpeg.av_hwdevice_ctx_init(ptr6) == 0)
						{
							_videoCodecContext->hw_device_ctx = ffmpeg.av_buffer_ref(ptr6);
						}
						ffmpeg.av_buffer_unref(&ptr6);
					}
				}
				if (EnableHwAccel && _videoCodecContext->hw_device_ctx == null && ffmpeg.av_hwdevice_ctx_create(&ptr6, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0) == 0)
				{
					_videoCodecContext->hw_device_ctx = ffmpeg.av_buffer_ref(ptr6);
					ffmpeg.av_buffer_unref(&ptr6);
				}
				if (ffmpeg.avcodec_open2(_videoCodecContext, codec, null) < 0)
				{
					throw new Exception("Could not open video codec");
				}
				_width = _videoCodecContext->width;
				_height = _videoCodecContext->height;
				if (_width > 0 && _height > 0)
				{
					_bgraBuffer = new byte[_width * _height * 4];
					_bgraBufferHandle = GCHandle.Alloc(_bgraBuffer, GCHandleType.Pinned);
					_bgraBufferPointer = _bgraBufferHandle.AddrOfPinnedObject();
				}
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
				Interlocked.Increment(ref _activeThreads);
				_videoThread = new Thread(VideoDecodeLoop)
				{
					IsBackground = true,
					Name = "FFmpegVideoThread"
				};
				_videoThread.Start();
			}
		}
		catch
		{
			Cleanup();
			throw;
		}
		finally
		{
			Interlocked.Exchange(ref _isOpening, 0);
		}
	}

	private unsafe AVPixelFormat GetFormat(AVCodecContext* s, AVPixelFormat* fmt)
	{
		for (AVPixelFormat* ptr = fmt; *ptr != AVPixelFormat.AV_PIX_FMT_NONE; ptr++)
		{
			if (*ptr == AVPixelFormat.AV_PIX_FMT_D3D11)
			{
				return *ptr;
			}
		}
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
		}
		return s->sw_pix_fmt;
	}

	private unsafe void InitAudioFilterGraph()
	{
		if (_audioStreamIndex == -1 || _audioCodecContext == null)
		{
			return;
		}
		if (_audioFilterGraph != null)
		{
			fixed (AVFilterGraph** graph = &_audioFilterGraph)
			{
				ffmpeg.avfilter_graph_free(graph);
			}
		}
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
			return;
		}
		AVFilterContext* ptr3 = null;
		if (ffmpeg.avfilter_graph_create_filter(&ptr3, filt2, "out", null, null, _audioFilterGraph) < 0)
		{
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
			fixed (AVFilterGraph** graph = &_audioFilterGraph)
			{
				ffmpeg.avfilter_graph_free(graph);
			}
		}
		else if (ffmpeg.avfilter_graph_config(_audioFilterGraph, null) < 0)
		{
			ffmpeg.avfilter_inout_free(&ptr5);
			ffmpeg.avfilter_inout_free(&ptr4);
			fixed (AVFilterGraph** graph = &_audioFilterGraph)
			{
				ffmpeg.avfilter_graph_free(graph);
			}
		}
		else
		{
			ffmpeg.avfilter_inout_free(&ptr5);
			ffmpeg.avfilter_inout_free(&ptr4);
			_abufferCtx = ptr2;
			_abuffersinkCtx = ptr3;
			_atempoCtx = ffmpeg.avfilter_graph_get_filter(_audioFilterGraph, "fatempo");
		}
	}

	public void Play()
	{
		lock (_lock)
		{
			_isPaused = false;
			if (_currentPlaybackPtsTime == 0.0 && _lastValidPtsTime > 0.0)
			{
				_currentPlaybackPtsTime = _lastValidPtsTime;
			}
			_masterClockStopwatch.Start();
			Monitor.PulseAll(_lock);
		}
	}

	public void Pause()
	{
		lock (_lock)
		{
			_isPaused = true;
			_masterClockStopwatch.Stop();
		}
	}

	public void Stop()
	{
		_isInterruptRequested = true;
		if (!_isRunning)
		{
			return;
		}
		_isRunning = false;
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

	public unsafe void Seek(double ratio)
	{
		double num = ((_formatContext != null) ? ((double)_formatContext->duration / 1000000.0) : 0.0);
		lock (_lock)
		{
			_isSeekingVideo = true;
			_isSeekingAudio = true;
			_seekTargetPtsTime = ratio * num * 1000.0;
			_seekTargetMs = _seekTargetPtsTime;
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
		while (_isRunning)
		{
			double num = -1.0;
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
				}
				else
				{
					if (_isPaused)
					{
						Monitor.Wait(_lock, 50);
						continue;
					}
					bool flag = _videoStreamIndex != -1 && _videoPacketQueue.Count > 300;
					bool flag2 = _audioStreamIndex != -1 && _audioPacketQueue.Count > 600;
					if (_isPreBuffering)
					{
						bool num2 = _videoStreamIndex == -1 || _videoEof || _videoPacketQueue.Count >= 150;
						bool flag3 = _audioStreamIndex == -1 || _videoEof || _audioPacketQueue.Count >= 300;
						bool flag4 = _videoStreamIndex != -1 && _videoPacketQueue.Count >= 300;
						bool flag5 = _audioStreamIndex != -1 && _audioPacketQueue.Count >= 600;
						if ((num2 && flag3) || flag4 || flag5)
						{
							_isPreBuffering = false;
						}
					}
					if (_audioFormatContext == null)
					{
						while (_isRunning && _seekTargetMs < 0.0 && (flag || flag2))
						{
							Monitor.Wait(_lock, 20);
							flag = _videoStreamIndex != -1 && _videoPacketQueue.Count > 300;
							flag2 = _audioStreamIndex != -1 && _audioPacketQueue.Count > 600;
						}
					}
					else
					{
						while (_isRunning && _seekTargetMs < 0.0 && flag && flag2)
						{
							Monitor.Wait(_lock, 20);
							flag = _videoStreamIndex != -1 && _videoPacketQueue.Count > 300;
							flag2 = _audioStreamIndex != -1 && _audioPacketQueue.Count > 600;
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
				lock (_lock)
				{
					IntPtr result;
					while (_videoPacketQueue.TryDequeue(out result))
					{
						ReturnPacket((AVPacket*)result);
					}
					DecodedVideoFrame result2;
					while (_decodedVideoQueue.TryDequeue(out result2))
					{
						result2.Dispose();
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
					_firstAudioPtsMs = -1.0;
					_firstVideoPtsMs = -1.0;
					_avStartOffsetMs = 0.0;
					_currentPlaybackPtsTime = num;
					_masterClockStopwatch.Restart();
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
				bool flag6 = _videoStreamIndex != -1 && _videoPacketQueue.Count > 300;
				bool flag7 = _audioStreamIndex != -1 && _audioPacketQueue.Count > 600;
				int num5 = ffmpeg.AVERROR_EOF;
				if (!_videoEof && (_audioFormatContext == null || !flag6))
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
						lock (_lock)
						{
							Monitor.PulseAll(_lock);
						}
					}
					else if (_packet->stream_index == _audioStreamIndex && _audioStreamIndex != -1 && _audioFormatContext == null)
					{
						AVPacket* packet2 = GetPacket();
						ffmpeg.av_packet_ref(packet2, _packet);
						_audioPacketQueue.Enqueue((nint)packet2);
						lock (_lock)
						{
							Monitor.PulseAll(_lock);
						}
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
			while (_isRunning)
			{
				if (_isPaused)
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
					if (_audioFilterGraph != null)
					{
						AVFilterGraph* audioFilterGraph = _audioFilterGraph;
						ffmpeg.avfilter_graph_free(&audioFilterGraph);
						_audioFilterGraph = null;
						_abufferCtx = null;
						_atempoCtx = null;
						_abuffersinkCtx = null;
					}
					SeekLog($"[SEEK_AUDIO_FLUSH] seekTarget={_seekTargetPtsTime:F0} lastAudioPts={_lastValidAudioPtsTime:F0}");
				}
				if (GetAudioBufferedDurationMs != null)
				{
					while (GetAudioBufferedDurationMs() > 300.0 && _isRunning && _needsAudioFlush == 0)
					{
						Thread.Sleep(5);
					}
				}
				if (_isPreBuffering && !_isFirstAudioFrame)
				{
					Thread.Sleep(10);
					continue;
				}
				if (!_audioPacketQueue.TryDequeue(out var result))
				{
					if (_isFinished && !HasVideo && !_notifiedPlaybackFinished && (GetAudioBufferedDurationMs == null || GetAudioBufferedDurationMs() < 50.0))
					{
						_notifiedPlaybackFinished = true;
						this.PlaybackFinished?.Invoke();
					}
					Thread.Sleep(2);
					continue;
				}
				AVPacket* ptr = (AVPacket*)result;
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
								if (_isFirstAudioFrame && _lastValidAudioPtsTime > 0.0)
								{
									_firstAudioPtsMs = _lastValidAudioPtsTime;
									_isFirstAudioFrame = false;
									if (!_isFirstVideoFrame && _firstVideoPtsMs >= 0.0)
									{
										_avStartOffsetMs = _firstVideoPtsMs - _firstAudioPtsMs;
									}
								}
							}
							ProcessAndConvertAudioFrame(_filteredAudioFrame);
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
						_lastValidAudioPtsTime = GetNormalizedPtsMs(num6, formatContext2, _audioStreamIndex);
						if (_isFirstAudioFrame && _lastValidAudioPtsTime > 0.0)
						{
							_firstAudioPtsMs = _lastValidAudioPtsTime;
							_isFirstAudioFrame = false;
							if (!_isFirstVideoFrame && _firstVideoPtsMs >= 0.0)
							{
								_avStartOffsetMs = _firstVideoPtsMs - _firstAudioPtsMs;
							}
						}
					}
					else if (_audioCodecContext->sample_rate > 0)
					{
						_lastValidAudioPtsTime += 1000.0 * (double)_audioFrame->nb_samples / (double)_audioCodecContext->sample_rate;
					}
					ProcessAndConvertAudioFrame(_audioFrame);
				}
			}
		}
		catch (Exception)
		{
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
		SeekLog($"[AUDIO_SKIP] isSeekingAudio={_isSeekingAudio} audioPts={_lastValidAudioPtsTime:F0} target={_seekTargetPtsTime:F0}");
		if (_isSeekingAudio)
		{
			if (_lastValidAudioPtsTime < _seekTargetPtsTime - 50.0)
			{
				return;
			}
			_isSeekingAudio = false;
			if (!HasVideo)
			{
				this.SeekPerformed?.Invoke();
			}
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
					this.AudioDataAvailable?.Invoke(array, num4);
				}
				finally
				{
					_audioBufferPool.Return(array);
				}
				if (!HasVideo)
				{
					double num5 = ((GetAudioBufferedDurationMs != null) ? GetAudioBufferedDurationMs() : 0.0);
					double num6 = (_lastValidAudioPtsTime - num5) / 1000.0;
					if (num6 < 0.0)
					{
						num6 = 0.0;
					}
					double num7 = (double)_formatContext->duration / 1000000.0;
					double obj = num6 / num7;
					this.PositionChanged?.Invoke(obj);
					this.TimeUpdated?.Invoke(TimeSpan.FromSeconds(num6), TimeSpan.FromSeconds(num7));
				}
			}
		}
		if (_isFirstAudioFrame)
		{
			_isFirstAudioFrame = false;
		}
	}

	private unsafe void VideoDecodeLoop()
	{
		double num = (double)_formatContext->duration / 1000000.0;
		TimeSpan arg = TimeSpan.FromSeconds(num);
		double num2 = ffmpeg.av_q2d(_formatContext->streams[_videoStreamIndex]->avg_frame_rate);
		if (num2 <= 0.0 || num2 > 1000.0)
		{
			num2 = ffmpeg.av_q2d(_formatContext->streams[_videoStreamIndex]->r_frame_rate);
		}
		if (num2 <= 0.0 || num2 > 1000.0)
		{
			num2 = 30.0;
		}
		_stats.TargetFps = num2;
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
			if (_isPaused)
			{
				Thread.Sleep(10);
				continue;
			}
			if (Interlocked.Exchange(ref _needsVideoFlush, 0) == 1)
			{
				ffmpeg.avcodec_flush_buffers(_videoCodecContext);
				_isSeekingVideo = true;
			}
			if (_isPreBuffering && !_isFirstVideoFrame)
			{
				Thread.Sleep(10);
				continue;
			}
			if (!_videoPacketQueue.TryDequeue(out var result))
			{
				if (_isFinished && !_notifiedPlaybackFinished)
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
				bool flag = _videoFrame->format == 171;
				IntPtr texturePtr = IntPtr.Zero;
				int sliceIndexOrStride = 0;
				if (flag)
				{
					texturePtr = new IntPtr(_videoFrame->data[0u]);
					sliceIndexOrStride = (int)new IntPtr(_videoFrame->data[1u]).ToInt64();
				}
				AVFrame* ptr2 = _videoFrame;
				AVFrame* ptr3 = null;
				if (!flag && (_videoFrame->format == 51 || _videoFrame->format == 117))
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
				if (_width == 0 || _height == 0 || _width != ptr2->width || _height != ptr2->height)
				{
					if (_bgraBufferHandle.IsAllocated)
					{
						_bgraBufferHandle.Free();
					}
					_width = ptr2->width;
					_height = ptr2->height;
					_bgraBuffer = new byte[_width * _height * 4];
					_bgraBufferHandle = GCHandle.Alloc(_bgraBuffer, GCHandleType.Pinned);
					_bgraBufferPointer = _bgraBufferHandle.AddrOfPinnedObject();
					if (_swsContext != null)
					{
						ffmpeg.sws_freeContext(_swsContext);
						_swsContext = null;
					}
				}
				if (_isSeekingVideo)
				{
					if (num5 < _seekTargetPtsTime - 50.0)
					{
						if (ptr3 != null)
						{
							ReturnFrame(ptr3);
						}
						ReturnFrame(_videoFrame);
						_videoFrame = GetFrame();
						stopwatch.Reset();
						continue;
					}
					_isSeekingVideo = false;
					this.SeekPerformed?.Invoke();
					stopwatch.Restart();
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
					stopwatch2.Start();
					if (_swsContext == null)
					{
						_swsContext = ffmpeg.sws_getContext(ptr2->width, ptr2->height, (AVPixelFormat)ptr2->format, _width, _height, AVPixelFormat.AV_PIX_FMT_BGRA, 1, null, null, null);
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
					decodedVideoFrame.BgraHandle = GCHandle.Alloc(decodedVideoFrame.BgraBuffer = new byte[_width * _height * 4], GCHandleType.Pinned);
					decodedVideoFrame.BgraPointer = decodedVideoFrame.BgraHandle.AddrOfPinnedObject();
					decodedVideoFrame.SliceIndexOrStride = _width * 4;
					ffmpeg.sws_scale(dst: new byte*[8]
					{
						(byte*)decodedVideoFrame.BgraPointer,
						default(byte*),
						default(byte*),
						default(byte*),
						default(byte*),
						default(byte*),
						default(byte*),
						default(byte*)
					}, dstStride: new int[8]
					{
						_width * 4,
						0,
						0,
						0,
						0,
						0,
						0,
						0
					}, c: _swsContext, srcSlice: ptr2->data, srcStride: ptr2->linesize, srcSliceY: 0, srcSliceH: ptr2->height);
					if (ptr3 != null)
					{
						ReturnFrame(ptr3);
					}
					stopwatch2.Stop();
					_totalDecodeTimeMs += stopwatch2.Elapsed.TotalMilliseconds;
					_decodeTimeSamples++;
					ReturnFrame(_videoFrame);
					_videoFrame = GetFrame();
				}
				while (_decodedVideoQueue.Count >= 5 && _isRunning && !_isSeekingVideo)
				{
					Thread.Sleep(5);
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
				}
				_framesDecodedThisSecond++;
				DateTime utcNow = DateTime.UtcNow;
				if ((utcNow - _lastFpsCalcTime).TotalMilliseconds >= 1000.0)
				{
					_stats.ActualFps = _framesDecodedThisSecond;
					if (_decodeTimeSamples > 0)
					{
						_stats.AvgDecodeTimeMs = _totalDecodeTimeMs / (double)_decodeTimeSamples;
					}
					_framesDecodedThisSecond = 0;
					_totalDecodeTimeMs = 0.0;
					_decodeTimeSamples = 0;
					_lastFpsCalcTime = utcNow;
				}
				double num10 = num5 / 1000.0;
				double obj = num10 / num;
				this.PositionChanged?.Invoke(obj);
				this.TimeUpdated?.Invoke(TimeSpan.FromSeconds(num10), arg);
			}
		}
		ThreadFinished();
	}

	public DecodedVideoFrame? PullVideoFrame(double masterClockPts)
	{
		DecodedVideoFrame decodedVideoFrame = null;
		DecodedVideoFrame result;
		DecodedVideoFrame result2;
		while (_decodedVideoQueue.TryPeek(out result) && result.PtsTime <= masterClockPts + 10.0 && _decodedVideoQueue.TryDequeue(out result2))
		{
			decodedVideoFrame?.Dispose();
			decodedVideoFrame = result2;
		}
		return decodedVideoFrame;
	}

	private unsafe void Cleanup()
	{
		if (Interlocked.Exchange(ref _isCleanedUp, 1) != 1)
		{
			if (_bgraBufferHandle.IsAllocated)
			{
				_bgraBufferHandle.Free();
				_bgraBufferHandle = default(GCHandle);
			}
			if (_audioBufferHandle.IsAllocated)
			{
				_audioBufferHandle.Free();
				_audioBufferHandle = default(GCHandle);
			}
			if (_swsContext != null)
			{
				ffmpeg.sws_freeContext(_swsContext);
				_swsContext = null;
			}
			if (_swrContext != null)
			{
				SwrContext* swrContext = _swrContext;
				ffmpeg.swr_free(&swrContext);
				_swrContext = null;
			}
			if (_audioFilterGraph != null)
			{
				AVFilterGraph* audioFilterGraph = _audioFilterGraph;
				ffmpeg.avfilter_graph_free(&audioFilterGraph);
				_audioFilterGraph = null;
				_abufferCtx = null;
				_atempoCtx = null;
				_abuffersinkCtx = null;
			}
			if (_filteredAudioFrame != null)
			{
				AVFrame* filteredAudioFrame = _filteredAudioFrame;
				ReturnFrame(filteredAudioFrame);
				_filteredAudioFrame = null;
			}
			if (_audioFrame != null)
			{
				AVFrame* audioFrame = _audioFrame;
				ReturnFrame(audioFrame);
				_audioFrame = null;
			}
			if (_videoFrame != null)
			{
				AVFrame* videoFrame = _videoFrame;
				ReturnFrame(videoFrame);
				_videoFrame = null;
			}
			if (_previousD3D11Frame != null)
			{
				AVFrame* previousD3D11Frame = _previousD3D11Frame;
				ReturnFrame(previousD3D11Frame);
				_previousD3D11Frame = null;
			}
			if (_packet != null)
			{
				AVPacket* packet = _packet;
				ReturnPacket(packet);
				_packet = null;
			}
			if (_videoCodecContext != null)
			{
				AVCodecContext* videoCodecContext = _videoCodecContext;
				ffmpeg.avcodec_free_context(&videoCodecContext);
				_videoCodecContext = null;
			}
			if (_audioCodecContext != null)
			{
				AVCodecContext* audioCodecContext = _audioCodecContext;
				ffmpeg.avcodec_free_context(&audioCodecContext);
				_audioCodecContext = null;
			}
			if (_formatContext != null)
			{
				AVFormatContext* formatContext = _formatContext;
				ffmpeg.avformat_close_input(&formatContext);
				_formatContext = null;
			}
			if (_audioFormatContext != null)
			{
				AVFormatContext* audioFormatContext = _audioFormatContext;
				ffmpeg.avformat_close_input(&audioFormatContext);
				_audioFormatContext = null;
			}
			IntPtr result;
			while (_videoPacketQueue.TryDequeue(out result))
			{
				ReturnPacket((AVPacket*)result);
			}
			DecodedVideoFrame result2;
			while (_decodedVideoQueue.TryDequeue(out result2))
			{
				result2.Dispose();
			}
			IntPtr result3;
			while (_audioPacketQueue.TryDequeue(out result3))
			{
				ReturnPacket((AVPacket*)result3);
			}
			IntPtr result4;
			while (_packetPool.TryTake(out result4))
			{
				AVPacket* ptr = (AVPacket*)result4;
				ffmpeg.av_packet_free(&ptr);
			}
			IntPtr result5;
			while (_framePool.TryTake(out result5))
			{
				AVFrame* ptr2 = (AVFrame*)result5;
				ffmpeg.av_frame_free(&ptr2);
			}
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
			Logger.Error("Unhandled exception caught in FFmpegMediaDecoder empty catch block", ex);
		}
		int num = 0;
		while ((Interlocked.CompareExchange(ref _activeThreads, 0, 0) > 0 || Interlocked.CompareExchange(ref _isOpening, 0, 0) == 1) && num < 5000)
		{
			Thread.Sleep(10);
			num += 10;
		}
		if (Interlocked.CompareExchange(ref _activeThreads, 0, 0) > 0 || Interlocked.CompareExchange(ref _isOpening, 0, 0) == 1)
		{
			Logger.Warn("Decoder dispose timed out while FFmpeg work is still active; cleanup deferred until decoder work exits.");
			GC.SuppressFinalize(this);
			return;
		}
		try
		{
			Cleanup();
		}
		catch (Exception ex2)
		{
			Logger.Error("Unhandled exception caught in FFmpegMediaDecoder empty catch block", ex2);
		}
		GC.SuppressFinalize(this);
	}
}


