using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Diagnostics;
using FFmpeg.AutoGen;

namespace JonPlayer
{
    public struct DecoderStats
    {
        public string VideoInfo;
        public string AudioInfo;
        public double TargetFps;
        public double ActualFps;
        public double AvgDecodeTimeMs;
        public double SyncDelayMs;
        public double VideoPts;
        public double AudioPts;
        public int LateFrames;
        public int ThreadCount;
        public int DroppedFrames;
        public int PacketQueueSize;
        public int AudioQueueSize;
        public bool IsHwAccel;
        public long Bitrate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct AVD3D11VADeviceContext
    {
        public IntPtr device;
        public IntPtr device_context;
        public IntPtr video_device;
        public IntPtr video_context;
        public void* lock_ctx;
        public IntPtr lock_func;
        public IntPtr unlock_func;
    }

    public unsafe class FFmpegMediaDecoder : IDisposable
    {
        public static bool EnableHwAccel = true;
        private static readonly System.Buffers.ArrayPool<byte> _audioBufferPool = System.Buffers.ArrayPool<byte>.Shared;
        private static readonly string _seekLogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "seek_debug.log");
        private static void SeekLog(string msg) { try { File.AppendAllText(_seekLogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); } catch { } }
        private AVFormatContext* _formatContext;
        private AVFormatContext* _audioFormatContext; 
        
        // Video
        private AVCodecContext* _videoCodecContext;
        private int _videoStreamIndex = -1;
        private int _width;
        private int _height;
        private SwsContext* _swsContext;
        
        private double _lastValidPtsTime = 0.0;
        private double _baseAudioPtsMs = -1.0;
        private long _totalOutputSamples = 0;
        private double _lastValidAudioPtsTime = 0.0;
        private byte[]? _bgraBuffer;
        private GCHandle _bgraBufferHandle;
        private IntPtr _bgraBufferPointer;

        // Audio
        private AVCodecContext* _audioCodecContext;
        private int _audioStreamIndex = -1;
        private SwrContext* _swrContext;
        private byte[]? _audioBuffer;
        private GCHandle _audioBufferHandle;
        private IntPtr _audioBufferPointer;
        private int _audioMaxBufferSize;

        public int AudioSampleRate { get; private set; }
        public int AudioChannels { get; private set; }

        private AVFilterGraph* _audioFilterGraph;
        private AVFilterContext* _abufferCtx;
        private AVFilterContext* _atempoCtx;
        private AVFilterContext* _abuffersinkCtx;
        private AVFrame* _filteredAudioFrame;

        private AVFrame* _audioFrame;
        private AVFrame* _videoFrame;
        private AVPacket* _packet;
        
        private AVFrame* _previousD3D11Frame;

        private Thread? _readThread;
        private Thread? _videoThread;
        private Thread? _audioThread;
        private System.Collections.Concurrent.ConcurrentQueue<IntPtr> _videoPacketQueue = new System.Collections.Concurrent.ConcurrentQueue<IntPtr>();
        private System.Collections.Concurrent.ConcurrentQueue<IntPtr> _audioPacketQueue = new System.Collections.Concurrent.ConcurrentQueue<IntPtr>();

        private System.Collections.Concurrent.ConcurrentBag<IntPtr> _packetPool = new System.Collections.Concurrent.ConcurrentBag<IntPtr>();
        private System.Collections.Concurrent.ConcurrentBag<IntPtr> _framePool = new System.Collections.Concurrent.ConcurrentBag<IntPtr>();

        // [DASH A/V Sync Offset]
        private double _avStartOffsetMs = 0.0;
        private volatile bool _isFirstVideoFrame = true;
        private volatile bool _isFirstAudioFrame = true;
        private volatile bool _isPreBuffering = false;
        private double _firstAudioPtsMs = -1.0;
        private double _firstVideoPtsMs = -1.0;

        private AVPacket* GetPacket()
        {
            if (_packetPool.TryTake(out IntPtr p))
            {
                var pkt = (AVPacket*)p;
                ffmpeg.av_packet_unref(pkt);
                return pkt;
            }
            return ffmpeg.av_packet_alloc();
        }

        private void ReturnPacket(AVPacket* pkt)
        {
            ffmpeg.av_packet_unref(pkt);
            _packetPool.Add((IntPtr)pkt);
        }

        private AVFrame* GetFrame()
        {
            if (_framePool.TryTake(out IntPtr p))
            {
                var frame = (AVFrame*)p;
                ffmpeg.av_frame_unref(frame);
                return frame;
            }
            return ffmpeg.av_frame_alloc();
        }

        private void ReturnFrame(AVFrame* frame)
        {
            ffmpeg.av_frame_unref(frame);
            _framePool.Add((IntPtr)frame);
        }
        
        private volatile bool _isRunning;
        private volatile bool _isPaused;

        private readonly object _lock = new object();
        public bool IsRunning => _isRunning;

        public event Action<IntPtr, int, int, int, bool>? FrameDecoded;
        public event Action<byte[], int>? AudioDataAvailable;
        public event Action<double>? RotationDetected;
        public event Action<double>? PositionChanged; 
        public event Action<TimeSpan, TimeSpan>? TimeUpdated; 
        public event Action? PlaybackFinished;
        public event Action? SeekPerformed;
        public event Action? SeekInitiated;

        public Func<double>? GetAudioBufferedDurationMs { get; set; }
    public Func<double>? GetAudioHardwareLatencyMs { get; set; }

        public double DurationSeconds => _formatContext != null ? _formatContext->duration / (double)ffmpeg.AV_TIME_BASE : 0;

        private static double GetStreamStartOffsetMs(AVFormatContext* formatContext, int streamIndex)
        {
            if (formatContext == null || streamIndex < 0 || streamIndex >= formatContext->nb_streams) return 0.0;

            AVStream* stream = formatContext->streams[streamIndex];
            if (stream != null && stream->start_time != ffmpeg.AV_NOPTS_VALUE)
            {
                return stream->start_time * ffmpeg.av_q2d(stream->time_base) * 1000.0;
            }

            if (formatContext->start_time != ffmpeg.AV_NOPTS_VALUE)
            {
                return formatContext->start_time * 1000.0 / ffmpeg.AV_TIME_BASE;
            }

            return 0.0;
        }

        private static double GetNormalizedPtsMs(long pts, AVFormatContext* formatContext, int streamIndex)
        {
            if (formatContext == null || streamIndex < 0 || streamIndex >= formatContext->nb_streams || pts == ffmpeg.AV_NOPTS_VALUE) return 0.0;

            AVStream* stream = formatContext->streams[streamIndex];
            if (stream == null) return 0.0;
            return pts * ffmpeg.av_q2d(stream->time_base) * 1000.0 - GetStreamStartOffsetMs(formatContext, streamIndex);
        }

        private static long GetSeekTimestamp(AVFormatContext* formatContext, double targetMs)
        {
            long startTime = formatContext != null && formatContext->start_time != ffmpeg.AV_NOPTS_VALUE ? formatContext->start_time : 0;
            return startTime + (long)(targetMs / 1000.0 * ffmpeg.AV_TIME_BASE);
        }

        private static long GetStreamSeekTimestamp(AVFormatContext* formatContext, int streamIndex, double targetMs)
        {
            if (formatContext == null || streamIndex < 0 || streamIndex >= formatContext->nb_streams) return ffmpeg.AV_NOPTS_VALUE;

            AVStream* stream = formatContext->streams[streamIndex];
            if (stream == null) return ffmpeg.AV_NOPTS_VALUE;

            double rawPtsMs = targetMs + GetStreamStartOffsetMs(formatContext, streamIndex);
            return (long)(rawPtsMs / 1000.0 / ffmpeg.av_q2d(stream->time_base));
        }

        private static int SeekFormatContext(AVFormatContext* formatContext, int streamIndex, double targetMs)
        {
            long streamTarget = GetStreamSeekTimestamp(formatContext, streamIndex, targetMs);
            if (streamTarget != ffmpeg.AV_NOPTS_VALUE)
            {
                int streamSeekRet = ffmpeg.av_seek_frame(formatContext, streamIndex, streamTarget, ffmpeg.AVSEEK_FLAG_BACKWARD);
                if (streamSeekRet >= 0) return streamSeekRet;

                streamSeekRet = ffmpeg.avformat_seek_file(formatContext, streamIndex, long.MinValue, streamTarget, streamTarget, ffmpeg.AVSEEK_FLAG_BACKWARD);
                if (streamSeekRet >= 0) return streamSeekRet;
            }

            long seekTarget = GetSeekTimestamp(formatContext, targetMs);
            return ffmpeg.avformat_seek_file(formatContext, -1, long.MinValue, seekTarget, seekTarget, ffmpeg.AVSEEK_FLAG_BACKWARD);
        }

        public int Width => _width;
        public int Height => _height;

        private string? _currentPath;
        private string? _separateAudioUrl; 
        private double _seekTargetMs = -1;
        private volatile bool _isFinished;
        private volatile bool _notifiedPlaybackFinished;
        private volatile bool _videoEof;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate AVPixelFormat AVPixelFormat_get_format_func(AVCodecContext* s, AVPixelFormat* fmt);
        private AVPixelFormat_get_format_func? _getFormatCallback;

        private double _playbackSpeed = 1.0;
        public double PlaybackSpeed => _playbackSpeed;
        private volatile bool _speedChanged = false;
        private int _needsVideoFlush = 0;
        private int _needsAudioFlush = 0;
        private volatile bool _isSeekingVideo = false;
        private volatile bool _isSeekingAudio = false;
        private double _seekTargetPtsTime = -1;
        private bool _isDisposed;
        private int _isOpening;
        
        public double AudioVolumeLevel { get; private set; } = 1.0;
        public double AudioVocalGain { get; private set; } = 0.0;
        public double VideoBrightness { get; private set; } = 0.0;
        public double VideoContrast { get; private set; } = 1.0;
        public double VideoSaturation { get; private set; } = 1.0;
        private volatile bool _videoFiltersChanged = false;
        private volatile bool _rebuildAudioFilters = false;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private unsafe delegate int InterruptCallbackDelegate(void* opaque);
        private InterruptCallbackDelegate? _interruptCallback;
        private volatile bool _isInterruptRequested = false;

        private unsafe int InterruptCallback(void* opaque)
        {
            return _isInterruptRequested ? 1 : 0;
        }

        private DecoderStats _stats;
        private int _framesDecodedThisSecond;
        private DateTime _lastFpsCalcTime;
        private double _totalDecodeTimeMs;
        private int _decodeTimeSamples;

        public bool IsPlaying => _isRunning && !_isPaused;
        public bool IsFinished => _isFinished;
        public bool HasVideo => _videoStreamIndex != -1;

        public DecoderStats GetStats()
        {
            _stats.PacketQueueSize = _videoPacketQueue.Count;
            _stats.AudioQueueSize = _audioPacketQueue.Count;
            return _stats;
        }

        private IntPtr _d3d11DevicePtr;
        private IntPtr _d3d11ContextPtr;

        static FFmpegMediaDecoder()
        {
            ffmpeg.RootPath = AppContext.BaseDirectory;
        }

        public void SetD3D11Device(IntPtr devicePtr, IntPtr contextPtr)
        {
            _d3d11DevicePtr = devicePtr;
            _d3d11ContextPtr = contextPtr;
        }

        public void Open(string path, string? audioUrl = null, double initialSeekRatio = 0.0)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(FFmpegMediaDecoder));
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
            _seekTargetMs = -1;
            _seekTargetPtsTime = -1;
            _isSeekingVideo = false;
            _isSeekingAudio = false;

            try
            {
                _formatContext = ffmpeg.avformat_alloc_context();
                _interruptCallback = new InterruptCallbackDelegate(InterruptCallback);
                _formatContext->interrupt_callback.callback = new FFmpeg.AutoGen.AVIOInterruptCB_callback_func { Pointer = Marshal.GetFunctionPointerForDelegate(_interruptCallback) };
                _formatContext->interrupt_callback.opaque = null;

                _formatContext->probesize = 5000000;
                _formatContext->max_analyze_duration = 2 * FFmpeg.AutoGen.ffmpeg.AV_TIME_BASE;

                var dictPtr = (AVDictionary**)0;
                AVDictionary* options = null;
                if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
                {
                    ffmpeg.av_dict_set(&options, "reconnect", "1", 0);
                    ffmpeg.av_dict_set(&options, "reconnect_streamed", "1", 0);
                    ffmpeg.av_dict_set(&options, "reconnect_delay_max", "5", 0);
                    ffmpeg.av_dict_set(&options, "reconnect_on_network_error", "1", 0);
                    ffmpeg.av_dict_set(&options, "reconnect_on_http_error", "4xx,5xx", 0);
                    ffmpeg.av_dict_set(&options, "rw_timeout", "10000000", 0);
                    ffmpeg.av_dict_set(&options, "seekable", "1", 0);
                    ffmpeg.av_dict_set(&options, "user_agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36", 0);
                }

                fixed (AVFormatContext** pFormatContext = &_formatContext)
                {
                    if (ffmpeg.avformat_open_input(pFormatContext, path, null, &options) < 0)
                    {
                        _formatContext = null;
                        if (options != null) ffmpeg.av_dict_free(&options);
                        throw new Exception("Could not open file");
                    }
                }
                if (options != null) ffmpeg.av_dict_free(&options);

                if (ffmpeg.avformat_find_stream_info(_formatContext, null) < 0)
                    throw new Exception("Could not find stream info");

                if (!path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    long startSeekTarget = GetSeekTimestamp(_formatContext, 0);
                    ffmpeg.avformat_seek_file(_formatContext, -1, long.MinValue, startSeekTarget, startSeekTarget, ffmpeg.AVSEEK_FLAG_BACKWARD);
                }

                for (int i = 0; i < _formatContext->nb_streams; i++)
                {
                    if (_formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && _videoStreamIndex == -1)
                    {
                        if ((_formatContext->streams[i]->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0)
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
                    throw new Exception("Could not find any video or audio stream");

                double initialSeekTargetMs = -1.0;
                if (initialSeekRatio > 0.0 && _formatContext->duration > 0)
                {
                    initialSeekTargetMs = Math.Clamp(initialSeekRatio, 0.0, 1.0) * _formatContext->duration * 1000.0 / ffmpeg.AV_TIME_BASE;
                    int primarySeekStreamIndex = _videoStreamIndex != -1 ? _videoStreamIndex : _audioStreamIndex;
                    int initialSeekRet = SeekFormatContext(_formatContext, primarySeekStreamIndex, initialSeekTargetMs);
                    if (initialSeekRet < 0) SeekLog($"[OPEN_SEEK_FAIL] target={initialSeekTargetMs:F0} ret={initialSeekRet}");
                }

                if (_videoStreamIndex != -1)
                {
                    var stream = _formatContext->streams[_videoStreamIndex];
                    double rotation = 0;
                    bool rotationFound = false;

                    var codecpar = _formatContext->streams[_videoStreamIndex]->codecpar;
                    var displayMatrixData = ffmpeg.av_packet_side_data_get(codecpar->coded_side_data, codecpar->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
                    
                    if (displayMatrixData != null && displayMatrixData->size >= 9 * sizeof(int))
                    {
                        var pMatrix = (FFmpeg.AutoGen.int_array9*)displayMatrixData->data;
                        double theta = ffmpeg.av_display_rotation_get(in *pMatrix);
                        if (!double.IsNaN(theta))
                        {
                            rotation = -theta; 
                            rotationFound = true;
                        }
                    }

                    if (!rotationFound)
                    {
                        var entry = ffmpeg.av_dict_get(stream->metadata, "rotate", null, 0);
                        if (entry != null && entry->value != null)
                        {
                            string? rotateStr = Marshal.PtrToStringUTF8((IntPtr)entry->value);
                            if (double.TryParse(rotateStr, out double fallbackRotation))
                            {
                                rotation = fallbackRotation;
                                rotationFound = true;
                            }
                        }
                    }

                    if (rotationFound && rotation != 0)
                    {
                        RotationDetected?.Invoke(rotation);
                    }

                    var videoCodecPar = _formatContext->streams[_videoStreamIndex]->codecpar;
                    var videoCodec = ffmpeg.avcodec_find_decoder(videoCodecPar->codec_id);
                    _videoCodecContext = ffmpeg.avcodec_alloc_context3(videoCodec);
                    ffmpeg.avcodec_parameters_to_context(_videoCodecContext, videoCodecPar);
                    _videoCodecContext->thread_count = 0; 
                    _videoCodecContext->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;

                    _getFormatCallback = GetFormat;
                    _videoCodecContext->get_format = new AVCodecContext_get_format_func { Pointer = Marshal.GetFunctionPointerForDelegate(_getFormatCallback) };

                    AVBufferRef* hwDeviceCtx = null;
                    if (EnableHwAccel && _d3d11DevicePtr != IntPtr.Zero)
                    {
                        hwDeviceCtx = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
                        if (hwDeviceCtx != null)
                        {
                            var deviceCtx = (AVHWDeviceContext*)hwDeviceCtx->data;
                            var d3d11DeviceCtx = (AVD3D11VADeviceContext*)deviceCtx->hwctx;
                            System.Runtime.InteropServices.Marshal.AddRef(_d3d11DevicePtr);
                            d3d11DeviceCtx->device = _d3d11DevicePtr;
                            d3d11DeviceCtx->device_context = IntPtr.Zero;

                            if (ffmpeg.av_hwdevice_ctx_init(hwDeviceCtx) == 0)
                            {
                                _videoCodecContext->hw_device_ctx = ffmpeg.av_buffer_ref(hwDeviceCtx);
                            }
                            ffmpeg.av_buffer_unref(&hwDeviceCtx);
                        }
                    }

                    if (EnableHwAccel && _videoCodecContext->hw_device_ctx == null)
                    {
                        if (ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0) == 0)
                        {
                            _videoCodecContext->hw_device_ctx = ffmpeg.av_buffer_ref(hwDeviceCtx);
                            ffmpeg.av_buffer_unref(&hwDeviceCtx);
                        }
                    }

                    if (ffmpeg.avcodec_open2(_videoCodecContext, videoCodec, null) < 0)
                        throw new Exception("Could not open video codec");

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
                        ThreadCount = _videoCodecContext->thread_count == 0 ? Environment.ProcessorCount : _videoCodecContext->thread_count,
                        Bitrate = _formatContext->bit_rate,
                        IsHwAccel = _videoCodecContext->hw_device_ctx != null
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
                    AVDictionary* audioOptions = null;
                    ffmpeg.av_dict_set(&audioOptions, "reconnect", "1", 0);
                    ffmpeg.av_dict_set(&audioOptions, "reconnect_streamed", "1", 0);
                    ffmpeg.av_dict_set(&audioOptions, "reconnect_delay_max", "5", 0);
                    ffmpeg.av_dict_set(&audioOptions, "rw_timeout", "10000000", 0);
                    ffmpeg.av_dict_set(&audioOptions, "seekable", "1", 0);
                    ffmpeg.av_dict_set(&audioOptions, "user_agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36", 0);

                    fixed (AVFormatContext** pAudioFmtCtx = &_audioFormatContext)
                    {
                        if (ffmpeg.avformat_open_input(pAudioFmtCtx, _separateAudioUrl, null, &audioOptions) < 0)
                        {
                            _audioFormatContext = null;
                            if (audioOptions != null) ffmpeg.av_dict_free(&audioOptions);
                        }
                    }
                    if (audioOptions != null) ffmpeg.av_dict_free(&audioOptions);

                    if (_audioFormatContext != null)
                    {
                        ffmpeg.avformat_find_stream_info(_audioFormatContext, null);
                        for (int i = 0; i < _audioFormatContext->nb_streams; i++)
                        {
                            if (_audioFormatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                            {
                                _audioStreamIndex = i;
                                break;
                            }
                        }

                        if (initialSeekTargetMs >= 0.0)
                        {
                            int initialAudioSeekRet = SeekFormatContext(_audioFormatContext, _audioStreamIndex, initialSeekTargetMs);
                            if (initialAudioSeekRet < 0) SeekLog($"[OPEN_SEEK_FAIL_AUDIO] target={initialSeekTargetMs:F0} ret={initialAudioSeekRet}");
                        }
                    }
                }

                if (_audioStreamIndex != -1)
                {
                    var audioFmtCtx = _audioFormatContext != null ? _audioFormatContext : _formatContext;
                    var audioCodecPar = audioFmtCtx->streams[_audioStreamIndex]->codecpar;
                    var audioCodec = ffmpeg.avcodec_find_decoder(audioCodecPar->codec_id);
                    _audioCodecContext = ffmpeg.avcodec_alloc_context3(audioCodec);
                    ffmpeg.avcodec_parameters_to_context(_audioCodecContext, audioCodecPar);
                    if (ffmpeg.avcodec_open2(_audioCodecContext, audioCodec, null) < 0)
                        throw new Exception("Could not open audio codec");

                    AudioSampleRate = _audioCodecContext->sample_rate > 0 ? _audioCodecContext->sample_rate : 48000;
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

                while (_videoPacketQueue.TryDequeue(out IntPtr p)) ReturnPacket((AVPacket*)p);
                while (_audioPacketQueue.TryDequeue(out IntPtr p)) ReturnPacket((AVPacket*)p);

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
                    _audioThread = new Thread(AudioDecodeLoop) { IsBackground = true, Name = "FFmpegAudioThread" };
                    _audioThread.Start();
                }

                Interlocked.Increment(ref _activeThreads);
                _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "FFmpegReadThread" };
                _readThread.Start();
                if (HasVideo)
                {
                    Interlocked.Increment(ref _activeThreads);
                    _videoThread = new Thread(VideoDecodeLoop) { IsBackground = true, Name = "FFmpegVideoThread" };
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

        private AVPixelFormat GetFormat(AVCodecContext* s, AVPixelFormat* fmt)
        {
            var ptr = fmt;
            while (*ptr != AVPixelFormat.AV_PIX_FMT_NONE)
            {
                if (*ptr == AVPixelFormat.AV_PIX_FMT_D3D11)
                    return *ptr;
                ptr++;
            }
            
            ptr = fmt;
            while (*ptr != AVPixelFormat.AV_PIX_FMT_NONE)
            {
                if (*ptr == s->sw_pix_fmt) return *ptr;
                if (*ptr == AVPixelFormat.AV_PIX_FMT_YUV420P) return *ptr;
                if (*ptr == AVPixelFormat.AV_PIX_FMT_NV12) return *ptr;
                ptr++;
            }

            return s->sw_pix_fmt;
        }

        private void InitAudioFilterGraph()
        {
            if (_audioStreamIndex == -1 || _audioCodecContext == null) return;

            if (_audioFilterGraph != null)
            {
                fixed (AVFilterGraph** pGraph = &_audioFilterGraph)
                {
                    ffmpeg.avfilter_graph_free(pGraph);
                }
            }
            if (_filteredAudioFrame == null)
            {
                _filteredAudioFrame = GetFrame();
            }

            _audioFilterGraph = ffmpeg.avfilter_graph_alloc();

            var abuffer = ffmpeg.avfilter_get_by_name("abuffer");
            var atempo = ffmpeg.avfilter_get_by_name("atempo");
            var abuffersink = ffmpeg.avfilter_get_by_name("abuffersink");

            var audioFmtCtxForTb = _audioFormatContext != null ? _audioFormatContext : _formatContext;
            AVRational timeBase = audioFmtCtxForTb->streams[_audioStreamIndex]->time_base;

            AVChannelLayout chLayout = _audioCodecContext->ch_layout;
            byte* layoutDesc = stackalloc byte[128];
            ffmpeg.av_channel_layout_describe(&chLayout, layoutDesc, 128);
            string chLayoutStr = System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)layoutDesc) ?? "stereo";

            string sampleFmtStr = ffmpeg.av_get_sample_fmt_name(_audioCodecContext->sample_fmt);
            if (sampleFmtStr == null) sampleFmtStr = "s16";

            string args = $"time_base={timeBase.num}/{timeBase.den}:sample_rate={_audioCodecContext->sample_rate}:sample_fmt={sampleFmtStr}:channel_layout={chLayoutStr}";

            AVFilterContext* abufferCtx = null;
            int ret1 = ffmpeg.avfilter_graph_create_filter(&abufferCtx, abuffer, "in", args, null, _audioFilterGraph);
            if (ret1 < 0) return;

            AVFilterContext* abuffersinkCtx = null;
            int ret2 = ffmpeg.avfilter_graph_create_filter(&abuffersinkCtx, abuffersink, "out", null, null, _audioFilterGraph);
            if (ret2 < 0) return;
            
            double speed = Math.Max(0.25, Math.Min(4.0, _playbackSpeed));
            string atempoChain;
            if (speed > 2.0)
                atempoChain = $"atempo=2.0,atempo@fatempo={(speed / 2.0).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            else if (speed < 0.5)
                atempoChain = $"atempo=0.5,atempo@fatempo={(speed / 0.5).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            else
                atempoChain = $"atempo@fatempo={speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

            string filterDesc = $"equalizer@feq=f=1000:width_type=h:width=200:g={AudioVocalGain.ToString(System.Globalization.CultureInfo.InvariantCulture)},volume@fvol=volume={AudioVolumeLevel.ToString(System.Globalization.CultureInfo.InvariantCulture)},{atempoChain}";

            AVFilterInOut* outputs = ffmpeg.avfilter_inout_alloc();
            AVFilterInOut* inputs = ffmpeg.avfilter_inout_alloc();

            outputs->name = ffmpeg.av_strdup("in");
            outputs->filter_ctx = abufferCtx;
            outputs->pad_idx = 0;
            outputs->next = null;

            inputs->name = ffmpeg.av_strdup("out");
            inputs->filter_ctx = abuffersinkCtx;
            inputs->pad_idx = 0;
            inputs->next = null;

            int parseRet = ffmpeg.avfilter_graph_parse_ptr(_audioFilterGraph, filterDesc, &inputs, &outputs, null);
            if (parseRet < 0)
            {
                ffmpeg.avfilter_inout_free(&inputs);
                ffmpeg.avfilter_inout_free(&outputs);
                fixed (AVFilterGraph** pGraph = &_audioFilterGraph) ffmpeg.avfilter_graph_free(pGraph);
                return;
            }
            int configRet = ffmpeg.avfilter_graph_config(_audioFilterGraph, null);
            if (configRet < 0)
            {
                ffmpeg.avfilter_inout_free(&inputs);
                ffmpeg.avfilter_inout_free(&outputs);
                fixed (AVFilterGraph** pGraph = &_audioFilterGraph) ffmpeg.avfilter_graph_free(pGraph);
                return;
            }

            ffmpeg.avfilter_inout_free(&inputs);
            ffmpeg.avfilter_inout_free(&outputs);

            _abufferCtx = abufferCtx;
            _abuffersinkCtx = abuffersinkCtx;

            _atempoCtx = ffmpeg.avfilter_graph_get_filter(_audioFilterGraph, "fatempo");
        }

        public void Play()
        {
            lock (_lock)
            {
                _isPaused = false;
                Monitor.PulseAll(_lock);
            }
        }

        public void Pause()
        {
            lock (_lock)
            {
                _isPaused = true;
            }
        }

        private int _activeThreads = 0;

        public void Stop()
        {
            _isInterruptRequested = true;
            if (!_isRunning) return;
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

        public void Seek(double ratio)
        {
            double durationInSeconds = _formatContext != null ? _formatContext->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            lock (_lock)
            {
                _isSeekingVideo = true;
                _isSeekingAudio = true;
                _seekTargetPtsTime = ratio * durationInSeconds * 1000.0;
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
                if (volume.HasValue) AudioVolumeLevel = volume.Value;
                if (vocal.HasValue) AudioVocalGain = vocal.Value;

                _rebuildAudioFilters = true;
            }
        }

        public void SetVideoFilters(double? brightness = null, double? contrast = null, double? saturation = null)
        {
            lock (_lock)
            {
                if (brightness.HasValue) VideoBrightness = brightness.Value;
                if (contrast.HasValue) VideoContrast = contrast.Value;
                if (saturation.HasValue) VideoSaturation = saturation.Value;
                _videoFiltersChanged = true;
            }
        }

        private void ReadLoop()
        {
            while (_isRunning)
            {
                double currentSeekTargetMs = -1;
                lock (_lock)
                {
                    if (_isFinished)
                    {
                        if (_seekTargetMs < 0)
                        {
                            Monitor.Wait(_lock, 50);
                            continue;
                        }
                    }

                    if (_seekTargetMs >= 0)
                    {
                        currentSeekTargetMs = _seekTargetMs;
                    }
                    else
                    {
                        if (_isPaused)
                        {
                            Monitor.Wait(_lock, 50);
                            continue;
                        }

                        bool videoFull = _videoStreamIndex != -1 && _videoPacketQueue.Count > 120;
                        bool audioFull = _audioStreamIndex != -1 && _audioPacketQueue.Count > 300;

                        if (_isPreBuffering)
                        {
                            bool videoReady = _videoStreamIndex == -1 || _videoEof || _videoPacketQueue.Count >= 60;
                            bool audioReady = _audioStreamIndex == -1 || _videoEof || _audioPacketQueue.Count >= 100;
                            bool videoMaxed = _videoStreamIndex != -1 && _videoPacketQueue.Count >= 120;
                            bool audioMaxed = _audioStreamIndex != -1 && _audioPacketQueue.Count >= 300;
                            if ((videoReady && audioReady) || videoMaxed || audioMaxed)
                            {
                                _isPreBuffering = false;
                            }
                        }

                        if (_audioFormatContext == null)
                        {
                            while (_isRunning && _seekTargetMs < 0 && (videoFull || audioFull))
                            {
                                Monitor.Wait(_lock, 20);
                                videoFull = _videoStreamIndex != -1 && _videoPacketQueue.Count > 120;
                                audioFull = _audioStreamIndex != -1 && _audioPacketQueue.Count > 300;
                            }
                        }
                        else
                        {
                            while (_isRunning && _seekTargetMs < 0 && (videoFull && audioFull))
                            {
                                Monitor.Wait(_lock, 20);
                                videoFull = _videoStreamIndex != -1 && _videoPacketQueue.Count > 120;
                                audioFull = _audioStreamIndex != -1 && _audioPacketQueue.Count > 300;
                            }
                        }
                    }
                } 

                if (currentSeekTargetMs >= 0)
                {
                    int videoSeekRet = SeekFormatContext(_formatContext, _videoStreamIndex, currentSeekTargetMs);
                    if (_audioFormatContext != null)
                    {
                        int audioSeekRet = SeekFormatContext(_audioFormatContext, _audioStreamIndex, currentSeekTargetMs);
                        if (audioSeekRet < 0) SeekLog($"[SEEK_FAIL_AUDIO] target={currentSeekTargetMs:F0} ret={audioSeekRet}");
                    }

                    if (videoSeekRet < 0) SeekLog($"[SEEK_FAIL_VIDEO] target={currentSeekTargetMs:F0} ret={videoSeekRet}");

                    lock (_lock)
                    {
                        while (_videoPacketQueue.TryDequeue(out IntPtr p)) ReturnPacket((AVPacket*)p);
                        while (_audioPacketQueue.TryDequeue(out IntPtr p)) ReturnPacket((AVPacket*)p);

                        Interlocked.Exchange(ref _needsVideoFlush, 1);
                        if (_audioStreamIndex != -1) Interlocked.Exchange(ref _needsAudioFlush, 1);

                        _lastValidPtsTime = 0;
                        _lastValidAudioPtsTime = 0;
                        
                        _isFirstVideoFrame = true;
                        _isFirstAudioFrame = true;
                        _isPreBuffering = true;
                        _firstAudioPtsMs = -1.0;
                        _firstVideoPtsMs = -1.0;
                        _avStartOffsetMs = 0.0;
                        
                        if (_seekTargetMs == currentSeekTargetMs) _seekTargetMs = -1;
                        
                        _isFinished = false;
                        _notifiedPlaybackFinished = false;
                        _videoEof = false;
                        SeekInitiated?.Invoke();
                        Monitor.PulseAll(_lock);
                    }
                    continue;
                }

                if (_isFinished) continue;

                bool videoFullCheck = _videoStreamIndex != -1 && _videoPacketQueue.Count > 120;
                bool audioFullCheck = _audioStreamIndex != -1 && _audioPacketQueue.Count > 300;

                int readRes = ffmpeg.AVERROR_EOF;
                if (!_videoEof && (_audioFormatContext == null || !videoFullCheck))
                {
                    readRes = ffmpeg.av_read_frame(_formatContext, _packet);
                    if (readRes < 0)
                    {
                        ffmpeg.av_packet_unref(_packet);
                        if (readRes == ffmpeg.AVERROR_EOF)
                        {
                            _videoEof = true;
                            if (_audioFormatContext == null)
                            {
                                _isFinished = true;
                                continue;
                            }
                        }
                        else
                        {
                            _isFinished = true;
                            break;
                        }
                    }
                }

                if (!_videoEof && readRes >= 0)
                {
                    if (_packet->stream_index == _videoStreamIndex)
                    {
                        var newPkt = GetPacket();
                        ffmpeg.av_packet_ref(newPkt, _packet);
                        _videoPacketQueue.Enqueue((IntPtr)newPkt);
                        lock (_lock) { Monitor.PulseAll(_lock); }
                    }
                    else if (_packet->stream_index == _audioStreamIndex && _audioStreamIndex != -1 && _audioFormatContext == null)
                    {
                        var newPkt = GetPacket();
                        ffmpeg.av_packet_ref(newPkt, _packet);
                        _audioPacketQueue.Enqueue((IntPtr)newPkt);
                        lock (_lock) { Monitor.PulseAll(_lock); }
                    }
                    ffmpeg.av_packet_unref(_packet);
                }

                if (_audioFormatContext != null && _audioStreamIndex != -1 && !audioFullCheck)
                {
                    var audioPkt = GetPacket();
                    int audioReadRes = ffmpeg.av_read_frame(_audioFormatContext, audioPkt);
                    if (audioReadRes >= 0 && audioPkt->stream_index == _audioStreamIndex)
                    {
                        _audioPacketQueue.Enqueue((IntPtr)audioPkt);
                        lock (_lock) { Monitor.PulseAll(_lock); }
                    }
                    else
                    {
                        ReturnPacket(audioPkt);
                        if (audioReadRes == ffmpeg.AVERROR_EOF && _videoEof) _isFinished = true;
                        else if (audioReadRes < 0 && audioReadRes != ffmpeg.AVERROR_EOF) _isFinished = true;
                    }
                }
            }
            ThreadFinished();
        }

        private void AudioDecodeLoop()
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

                        // seek 후 swr 내부 버퍼 잔류 샘플 제거
                        if (_swrContext != null)
                        {
                            var s = _swrContext;
                            ffmpeg.swr_free(&s);
                            _swrContext = null;
                        }

                        // filter graph 내부 버퍼도 즉시 해제
                        if (_audioFilterGraph != null)
                        {
                            var g = _audioFilterGraph;
                            ffmpeg.avfilter_graph_free(&g);
                            _audioFilterGraph = null;
                            _abufferCtx = null;
                            _atempoCtx = null;
                            _abuffersinkCtx = null;
                        }
                        SeekLog($"[SEEK_AUDIO_FLUSH] seekTarget={_seekTargetPtsTime:F0} lastAudioPts={_lastValidAudioPtsTime:F0}");
                    }

                    if (GetAudioBufferedDurationMs != null)
                    {
                        while (GetAudioBufferedDurationMs() > 300 && _isRunning && _needsAudioFlush == 0)
                        {
                            Thread.Sleep(5);
                        }
                    }

                    if (_isPreBuffering && !_isFirstAudioFrame)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    if (!_audioPacketQueue.TryDequeue(out IntPtr pktPtr))
                    {
                        if (_isFinished && !HasVideo && !_notifiedPlaybackFinished)
                        {
                            if (GetAudioBufferedDurationMs == null || GetAudioBufferedDurationMs() < 50)
                            {
                                _notifiedPlaybackFinished = true;
                                PlaybackFinished?.Invoke();
                            }
                        }
                        Thread.Sleep(2);
                        continue;
                    }

                    var audioPacket = (AVPacket*)pktPtr;
                    int sendRes = ffmpeg.avcodec_send_packet(_audioCodecContext, audioPacket);
                    ReturnPacket(audioPacket);
                    lock (_lock) { Monitor.PulseAll(_lock); }

                    if (sendRes >= 0)
                    {
                        while (true)
                        {
                            if (ffmpeg.avcodec_receive_frame(_audioCodecContext, _audioFrame) < 0) break;

                            if (_audioFrame->nb_samples <= 0 || _audioFrame->extended_data == null || (int)_audioFrame->format < 0) continue;

                            AVChannelLayout in_ch_layout = _audioFrame->ch_layout;
                            int frameChannels = in_ch_layout.nb_channels;
                            
                            if (frameChannels == 0) frameChannels = _audioCodecContext->ch_layout.nb_channels;
                            if (frameChannels == 0) frameChannels = AudioChannels;

                            bool isPlanar = ffmpeg.av_sample_fmt_is_planar((AVSampleFormat)_audioFrame->format) != 0;
                            bool isCorrupt = false;
                            if (isPlanar)
                            {
                                for (int i = 0; i < frameChannels; i++)
                                {
                                    if (_audioFrame->extended_data[i] == null)
                                    {
                                        isCorrupt = true;
                                        break;
                                    }
                                }
                            }
                            else
                            {
                                if (_audioFrame->extended_data[0] == null) isCorrupt = true;
                            }
                            
                            if (isCorrupt) continue;

                            if (_rebuildAudioFilters)
                            {
                                InitAudioFilterGraph();
                                _rebuildAudioFilters = false;
                                _baseAudioPtsMs = -1.0;
                            }

                            if (_audioFilterGraph != null && _abufferCtx != null && _abuffersinkCtx != null)
                            {
                                long inPts = _audioFrame->best_effort_timestamp;
                                if (inPts == ffmpeg.AV_NOPTS_VALUE) inPts = _audioFrame->pts;
                                if (inPts == ffmpeg.AV_NOPTS_VALUE) inPts = _audioFrame->pkt_dts;
                                if (inPts != ffmpeg.AV_NOPTS_VALUE)
                                {
                                    var audioFmtCtx = _audioFormatContext != null ? _audioFormatContext : _formatContext;
                                    double ptsMs = GetNormalizedPtsMs(inPts, audioFmtCtx, _audioStreamIndex);
                                    if (_baseAudioPtsMs < 0) 
                                    {
                                        _baseAudioPtsMs = ptsMs;
                                        _totalOutputSamples = 0;
                                    }
                                }

                                if (ffmpeg.av_buffersrc_add_frame(_abufferCtx, _audioFrame) >= 0)
                                {
                                    while (true)
                                    {
                                        int sinkRet = ffmpeg.av_buffersink_get_frame(_abuffersinkCtx, _filteredAudioFrame);
                                        if (sinkRet == ffmpeg.AVERROR(ffmpeg.EAGAIN) || sinkRet == ffmpeg.AVERROR_EOF)
                                            break;
                                        if (sinkRet < 0)
                                            break;

                                        _totalOutputSamples += _filteredAudioFrame->nb_samples;
                                        if (_baseAudioPtsMs >= 0 && _filteredAudioFrame->sample_rate > 0)
                                        {
                                            _lastValidAudioPtsTime = _baseAudioPtsMs + ((double)_totalOutputSamples * _playbackSpeed * 1000.0 / _filteredAudioFrame->sample_rate);
                                            
                                            if (_isFirstAudioFrame && _lastValidAudioPtsTime > 0)
                                            {
                                                _firstAudioPtsMs = _lastValidAudioPtsTime;
                                                _isFirstAudioFrame = false;
                                                if (!_isFirstVideoFrame && _firstVideoPtsMs >= 0)
                                                {
                                                    _avStartOffsetMs = _firstVideoPtsMs - _firstAudioPtsMs;
                                                }
                                            }
                                        }

                                        ProcessAndConvertAudioFrame(_filteredAudioFrame);
                                        ffmpeg.av_frame_unref(_filteredAudioFrame);
                                    }
                                }
                                continue;
                            }
                            else
                            {
                                long inPts = _audioFrame->best_effort_timestamp;
                                if (inPts == ffmpeg.AV_NOPTS_VALUE) inPts = _audioFrame->pts;
                                if (inPts == ffmpeg.AV_NOPTS_VALUE) inPts = _audioFrame->pkt_dts;
                                if (inPts != ffmpeg.AV_NOPTS_VALUE)
                                {
                                    var audioFmtCtx = _audioFormatContext != null ? _audioFormatContext : _formatContext;
                                    _lastValidAudioPtsTime = GetNormalizedPtsMs(inPts, audioFmtCtx, _audioStreamIndex);
                                    
                                    if (_isFirstAudioFrame && _lastValidAudioPtsTime > 0)
                                    {
                                        _firstAudioPtsMs = _lastValidAudioPtsTime;
                                        _isFirstAudioFrame = false;
                                        if (!_isFirstVideoFrame && _firstVideoPtsMs >= 0)
                                        {
                                            _avStartOffsetMs = _firstVideoPtsMs - _firstAudioPtsMs;
                                        }
                                    }
                                }
                                else if (_audioCodecContext->sample_rate > 0)
                                {
                                    _lastValidAudioPtsTime += 1000.0 * _audioFrame->nb_samples / _audioCodecContext->sample_rate;
                                }

                                ProcessAndConvertAudioFrame(_audioFrame);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Audio Decode Error: {ex.Message}");
            }
            ThreadFinished();
        }

        private void ProcessAndConvertAudioFrame(AVFrame* frameToConvert)
        {
            if (_swrContext == null)
            {
                AVChannelLayout out_ch_layout;
                ffmpeg.av_channel_layout_default(&out_ch_layout, AudioChannels);
                
                AVChannelLayout in_ch_layout = frameToConvert->ch_layout;
                
                SwrContext* swrCtx = null;
                int ret = ffmpeg.swr_alloc_set_opts2(
                    &swrCtx,
                    &out_ch_layout, AVSampleFormat.AV_SAMPLE_FMT_S16, AudioSampleRate,
                    &in_ch_layout, (AVSampleFormat)frameToConvert->format, frameToConvert->sample_rate,
                    0, null
                );
                
                if (ret < 0 || swrCtx == null) return;
                
                _swrContext = swrCtx;
                if (ffmpeg.swr_init(_swrContext) < 0)
                {
                    fixed (SwrContext** pSwrContext = &_swrContext)
                    {
                        ffmpeg.swr_free(pSwrContext);
                    }
                    _swrContext = null;
                    return;
                }
            }
            
            if (_swrContext == null) return;
            
            SeekLog($"[AUDIO_SKIP] isSeekingAudio={_isSeekingAudio} audioPts={_lastValidAudioPtsTime:F0} target={_seekTargetPtsTime:F0}");
            if (_isSeekingAudio)
            {
                if (_lastValidAudioPtsTime < _seekTargetPtsTime - 50)
                {
                    return;
                }
                
                _isSeekingAudio = false;
                if (!HasVideo)
                {
                    SeekPerformed?.Invoke();
                }
            }
            int outSamples = ffmpeg.swr_get_out_samples(_swrContext, frameToConvert->nb_samples);
            int requiredBufferSize = ffmpeg.av_samples_get_buffer_size(null, AudioChannels, outSamples, AVSampleFormat.AV_SAMPLE_FMT_S16, 1);
            
            if (requiredBufferSize < 0 || requiredBufferSize > _audioMaxBufferSize) return;

            byte* pOutData = (byte*)_audioBufferPointer;
            byte** outDataPtr = &pOutData;
            
            int numSamplesConverted = ffmpeg.swr_convert(
                _swrContext, 
                outDataPtr, outSamples, 
                frameToConvert->extended_data, frameToConvert->nb_samples);
                
            if (numSamplesConverted > 0)
            {
                int bufferSize = ffmpeg.av_samples_get_buffer_size(null, AudioChannels, numSamplesConverted, AVSampleFormat.AV_SAMPLE_FMT_S16, 1);
                if (bufferSize > 0 && bufferSize <= _audioMaxBufferSize)
                {
                    byte[] managedBuffer = _audioBufferPool.Rent(bufferSize);
                    try
                    {
                        Marshal.Copy(_audioBufferPointer, managedBuffer, 0, bufferSize);
                        AudioDataAvailable?.Invoke(managedBuffer, bufferSize);
                    }
                    finally
                    {
                        _audioBufferPool.Return(managedBuffer);
                    }
                    
                    if (!HasVideo)
                    {
                        double bufferedMs = GetAudioBufferedDurationMs != null ? GetAudioBufferedDurationMs() : 0;
                        double currentTimeSeconds = (_lastValidAudioPtsTime - bufferedMs) / 1000.0;
                        if (currentTimeSeconds < 0) currentTimeSeconds = 0;
                        
                        double durationInSeconds = _formatContext->duration / (double)ffmpeg.AV_TIME_BASE;
                        double ratio = currentTimeSeconds / durationInSeconds;
                        PositionChanged?.Invoke(ratio);
                        TimeUpdated?.Invoke(TimeSpan.FromSeconds(currentTimeSeconds), TimeSpan.FromSeconds(durationInSeconds));
                    }
                }
            }
            if (_isFirstAudioFrame) _isFirstAudioFrame = false;
        }

        private void VideoDecodeLoop()
        {
            double durationInSeconds = _formatContext->duration / (double)ffmpeg.AV_TIME_BASE;
            var totalTime = TimeSpan.FromSeconds(durationInSeconds);

            double fps = ffmpeg.av_q2d(_formatContext->streams[_videoStreamIndex]->avg_frame_rate);
            if (fps <= 0 || fps > 1000) fps = ffmpeg.av_q2d(_formatContext->streams[_videoStreamIndex]->r_frame_rate);
            if (fps <= 0 || fps > 1000) fps = 30.0;

            _stats.TargetFps = fps;
            _stats.LateFrames = 0;
            _framesDecodedThisSecond = 0;
            _totalDecodeTimeMs = 0;
            _decodeTimeSamples = 0;
            _lastFpsCalcTime = DateTime.UtcNow;

            var stopwatch = new Stopwatch();
            var decodeTimer = new Stopwatch();
            double currentPlaybackPtsTime = 0;


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

                if (!_videoPacketQueue.TryDequeue(out IntPtr pktPtr))
                {
                    if (_isFinished && !_notifiedPlaybackFinished)
                    {
                        _notifiedPlaybackFinished = true;
                        PlaybackFinished?.Invoke();
                    }
                    Thread.Sleep(2);
                    continue;
                }

                var videoPacket = (AVPacket*)pktPtr;
                int sendRes = ffmpeg.avcodec_send_packet(_videoCodecContext, videoPacket);
                ReturnPacket(videoPacket);
                lock (_lock) { Monitor.PulseAll(_lock); }

                if (sendRes >= 0)
                {
                    while (true)
                    {
                        decodeTimer.Restart();
                        if (ffmpeg.avcodec_receive_frame(_videoCodecContext, _videoFrame) < 0) break;
                        decodeTimer.Stop();

                        bool isD3D11Frame = _videoFrame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11;

                        IntPtr texturePtr = IntPtr.Zero;
                        int sliceIndex = 0;

                        if (isD3D11Frame)
                        {
                            texturePtr = new IntPtr(_videoFrame->data[0]);
                            sliceIndex = (int)(new IntPtr(_videoFrame->data[1]).ToInt64());
                        }

                        AVFrame* processedFrame = _videoFrame;
                        AVFrame* swFrame = null;

                        if (!isD3D11Frame && (_videoFrame->format == (int)AVPixelFormat.AV_PIX_FMT_DXVA2_VLD ||
                            _videoFrame->format == (int)AVPixelFormat.AV_PIX_FMT_CUDA))
                        {
                            swFrame = GetFrame();
                            if (ffmpeg.av_hwframe_transfer_data(swFrame, _videoFrame, 0) == 0)
                            {
                                swFrame->pts = _videoFrame->pts;
                                swFrame->best_effort_timestamp = _videoFrame->best_effort_timestamp;
                                swFrame->pkt_dts = _videoFrame->pkt_dts;
                                processedFrame = swFrame;
                            }
                            else
                            {
                                ReturnFrame(swFrame);
                                swFrame = null;
                                continue;
                            }
                        }

                        if (!isD3D11Frame && (processedFrame->width <= 0 || processedFrame->height <= 0))
                        {
                            if (swFrame != null) ReturnFrame(swFrame);
                            continue;
                        }

                        long pts = _videoFrame->best_effort_timestamp;
                        if (pts == ffmpeg.AV_NOPTS_VALUE) pts = _videoFrame->pts;
                        if (pts == ffmpeg.AV_NOPTS_VALUE) pts = _videoFrame->pkt_dts;
                        
                        double ptsTime = 0;
                        if (pts != ffmpeg.AV_NOPTS_VALUE)
                        {
                            ptsTime = GetNormalizedPtsMs(pts, _formatContext, _videoStreamIndex);
                            _lastValidPtsTime = ptsTime;
                        }
                        else
                        {
                            double frameDuration = 33.3; 
                            if (_videoCodecContext->framerate.num > 0 && _videoCodecContext->framerate.den > 0)
                                frameDuration = 1000.0 * ffmpeg.av_q2d(ffmpeg.av_inv_q(_videoCodecContext->framerate));
                            ptsTime = _lastValidPtsTime + frameDuration;
                            _lastValidPtsTime = ptsTime;
                        }

                        if (_width == 0 || _height == 0 || _width != processedFrame->width || _height != processedFrame->height)
                        {
                            if (_bgraBufferHandle.IsAllocated)
                            {
                                _bgraBufferHandle.Free();
                            }
                            _width = processedFrame->width;
                            _height = processedFrame->height;
                            _bgraBuffer = new byte[_width * _height * 4];
                            _bgraBufferHandle = System.Runtime.InteropServices.GCHandle.Alloc(_bgraBuffer, System.Runtime.InteropServices.GCHandleType.Pinned);
                            _bgraBufferPointer = _bgraBufferHandle.AddrOfPinnedObject();
                            
                            if (_swsContext != null)
                            {
                                ffmpeg.sws_freeContext(_swsContext);
                                _swsContext = null;
                            }
                        }

                        if (_isSeekingVideo)
                        {
                            if (ptsTime < _seekTargetPtsTime - 50)
                            {
                                if (swFrame != null) ReturnFrame(swFrame);
                                ReturnFrame(_videoFrame);
                                _videoFrame = GetFrame();
                                stopwatch.Reset();
                                continue;
                            }
                            
                            _isSeekingVideo = false;
                            SeekPerformed?.Invoke();
                            
                            currentPlaybackPtsTime = ptsTime;
                            stopwatch.Restart();
                        }

                        lock (_lock)
                        {
                            if (_speedChanged)
                            {
                                if (stopwatch.IsRunning)
                                {
                                    currentPlaybackPtsTime = ptsTime;
                                    stopwatch.Restart();
                                }
                                _speedChanged = false;
                            }
                        }

                        if (_isPreBuffering)
                        {
                            currentPlaybackPtsTime = ptsTime;
                        }

                        if (!stopwatch.IsRunning && !_isPreBuffering)
                        {
                            stopwatch.Restart();
                            currentPlaybackPtsTime = ptsTime;
                        }

                        double elapsed = stopwatch.ElapsedMilliseconds * _playbackSpeed;
                        double systemClock = currentPlaybackPtsTime + elapsed;
                        double masterClockPtsTime = systemClock;

                        if (_audioStreamIndex != -1 && GetAudioBufferedDurationMs != null && _lastValidAudioPtsTime > 0 && !_isSeekingAudio)
                        {
                            double bufferedMs = GetAudioBufferedDurationMs();
                            SeekLog($"[SYNC] isSeekingAudio={_isSeekingAudio} lastAudioPts={_lastValidAudioPtsTime:F0} buffered={bufferedMs:F0} systemClock={systemClock:F0}");
                            double hwLatency = GetAudioHardwareLatencyMs != null ? GetAudioHardwareLatencyMs() : 0.0;
                            double audioClock = _lastValidAudioPtsTime - ((bufferedMs + hwLatency) * _playbackSpeed);
                            double diff = audioClock - systemClock;

                            if (diff > 2000 || diff < -2000)
                            {
                                currentPlaybackPtsTime = audioClock;
                                stopwatch.Restart();
                                masterClockPtsTime = audioClock;
                            }
                            else if (diff > 10 || diff < -10)
                            {
                                currentPlaybackPtsTime += diff * 0.05;
                                masterClockPtsTime = currentPlaybackPtsTime + stopwatch.ElapsedMilliseconds * _playbackSpeed;
                            }
                        }

                        double delay = ptsTime - masterClockPtsTime;

                        if (delay < -30.0) _stats.LateFrames++;
                        if (delay < -100.0 && _playbackSpeed <= 2.0)
                        {
                            _stats.DroppedFrames++;
                            if (swFrame != null) ReturnFrame(swFrame);
                            ReturnFrame(_videoFrame);
                            _videoFrame = GetFrame();
                            continue;
                        }

                        if (delay > 2000 || delay < -2000) 
                        {
                            stopwatch.Restart();
                            currentPlaybackPtsTime = ptsTime;
                        }
                        else if (delay > 0)
                        {
                            double targetElapsedMs = (ptsTime - currentPlaybackPtsTime) / _playbackSpeed;
                            double currentElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
                            double physicalDelayMs = targetElapsedMs - currentElapsedMs;
                            
                            int sleepMs = (int)physicalDelayMs - 2;
                            if (sleepMs > 0)
                            {
                                lock (_lock)
                                {
                                    if (_isRunning) Monitor.Wait(_lock, sleepMs);
                                }
                            }
                            
                            while (_isRunning && stopwatch.Elapsed.TotalMilliseconds < targetElapsedMs)
                            {
                                double remaining = targetElapsedMs - stopwatch.Elapsed.TotalMilliseconds;
                                if (remaining > 1.0)
                                    Thread.Sleep(1);
                                else if (remaining > 0.1)
                                    Thread.Yield();
                                else
                                    Thread.SpinWait(10);
                            }
                        }

                        double finalMasterClockPtsTime = currentPlaybackPtsTime + stopwatch.ElapsedMilliseconds * _playbackSpeed;
                        _stats.SyncDelayMs = ptsTime - finalMasterClockPtsTime;
                        _stats.VideoPts = _lastValidPtsTime;
                        
                        double audioPts = 0;
                        if (_audioStreamIndex != -1 && GetAudioBufferedDurationMs != null && _lastValidAudioPtsTime > 0)
                        {
                            double hwLatency = GetAudioHardwareLatencyMs != null ? GetAudioHardwareLatencyMs() : 0.0;
                            audioPts = _lastValidAudioPtsTime - ((GetAudioBufferedDurationMs() + hwLatency) * _playbackSpeed);
                        }
                        _stats.AudioPts = audioPts;

                        if (isD3D11Frame)
                        {
                            FrameDecoded?.Invoke(texturePtr, _width, _height, sliceIndex, true);
                            
                            if (_previousD3D11Frame != null)
                            {
                                ReturnFrame(_previousD3D11Frame);
                            }
                            _previousD3D11Frame = _videoFrame;
                            _videoFrame = GetFrame();
                        }
                        else
                        {
                            decodeTimer.Start();
                            if (_swsContext == null)
                            {
                                _swsContext = ffmpeg.sws_getContext(
                                    processedFrame->width, processedFrame->height, (AVPixelFormat)processedFrame->format,
                                    _width, _height, AVPixelFormat.AV_PIX_FMT_BGRA,
                                    1, null, null, null
                                );
                            }

                            if (_swsContext == null)
                            {
                                if (swFrame != null) ReturnFrame(swFrame);
                                ReturnFrame(_videoFrame);
                                _videoFrame = GetFrame();
                                continue;
                            }

                            if (_videoFiltersChanged)
                            {
                                int* inv_table = null;
                                int srcRange;
                                int* table = null;
                                int dstRange;
                                int b, c, s;
                                if (ffmpeg.sws_getColorspaceDetails(_swsContext, &inv_table, &srcRange, &table, &dstRange, &b, &c, &s) >= 0 && inv_table != null && table != null)
                                {
                                    var inv = new FFmpeg.AutoGen.int_array4();
                                    inv[0] = inv_table[0]; inv[1] = inv_table[1]; inv[2] = inv_table[2]; inv[3] = inv_table[3];
                                    var tbl = new FFmpeg.AutoGen.int_array4();
                                    tbl[0] = table[0]; tbl[1] = table[1]; tbl[2] = table[2]; tbl[3] = table[3];

                                    ffmpeg.sws_setColorspaceDetails(_swsContext, inv, srcRange, tbl, dstRange,
                                        (int)(VideoBrightness * 255.0 * 65536.0),
                                        (int)(VideoContrast * 65536.0),
                                        (int)(VideoSaturation * 65536.0));
                                }
                                _videoFiltersChanged = false;
                            }
                            
                            byte*[] dstData = new byte*[8];
                            dstData[0] = (byte*)_bgraBufferPointer;
                            int[] dstLinesize = new int[8];
                            dstLinesize[0] = _width * 4;
                            
                            ffmpeg.sws_scale(_swsContext, processedFrame->data, processedFrame->linesize, 0, processedFrame->height, dstData, dstLinesize);
                            
                            if (swFrame != null) ReturnFrame(swFrame);
                            decodeTimer.Stop();

                            _totalDecodeTimeMs += decodeTimer.Elapsed.TotalMilliseconds;
                            _decodeTimeSamples++;

                            FrameDecoded?.Invoke(_bgraBufferPointer, _width, _height, _width * 4, false);
                            ReturnFrame(_videoFrame);
                            _videoFrame = GetFrame();
                        }

                        if (_isFirstVideoFrame) _isFirstVideoFrame = false;

                        _framesDecodedThisSecond++;
                        
                        var now2 = DateTime.UtcNow;
                        if ((now2 - _lastFpsCalcTime).TotalMilliseconds >= 1000)
                        {
                            _stats.ActualFps = _framesDecodedThisSecond;
                            if (_decodeTimeSamples > 0)
                                _stats.AvgDecodeTimeMs = _totalDecodeTimeMs / _decodeTimeSamples;
                            
                            _framesDecodedThisSecond = 0;
                            _totalDecodeTimeMs = 0;
                            _decodeTimeSamples = 0;
                            _lastFpsCalcTime = now2;
                        }

                        double currentTimeSeconds = ptsTime / 1000.0;
                        double ratio = currentTimeSeconds / durationInSeconds;

                        PositionChanged?.Invoke(ratio);
                        TimeUpdated?.Invoke(TimeSpan.FromSeconds(currentTimeSeconds), totalTime);
                    }
                }
            }
            ThreadFinished();
        }

        private int _isCleanedUp = 0;

        private void Cleanup()
        {
            if (Interlocked.Exchange(ref _isCleanedUp, 1) == 1) return;

            if (_bgraBufferHandle.IsAllocated)
            {
                _bgraBufferHandle.Free();
                _bgraBufferHandle = default;
            }

            if (_audioBufferHandle.IsAllocated)
            {
                _audioBufferHandle.Free();
                _audioBufferHandle = default;
            }

            if (_swsContext != null)
            {
                ffmpeg.sws_freeContext(_swsContext);
                _swsContext = null;
            }

            if (_swrContext != null)
            {
                var s = _swrContext;
                ffmpeg.swr_free(&s);
                _swrContext = null;
            }

            if (_audioFilterGraph != null)
            {
                var g = _audioFilterGraph;
                ffmpeg.avfilter_graph_free(&g);
                _audioFilterGraph = null;
                _abufferCtx = null;
                _atempoCtx = null;
                _abuffersinkCtx = null;
            }

            if (_filteredAudioFrame != null)
            {
                var f = _filteredAudioFrame;
                ReturnFrame(f);
                _filteredAudioFrame = null;
            }

            if (_audioFrame != null)
            {
                var f = _audioFrame;
                ReturnFrame(f);
                _audioFrame = null;
            }

            if (_videoFrame != null)
            {
                var f = _videoFrame;
                ReturnFrame(f);
                _videoFrame = null;
            }

            if (_previousD3D11Frame != null)
            {
                var f = _previousD3D11Frame;
                ReturnFrame(f);
                _previousD3D11Frame = null;
            }

            if (_packet != null)
            {
                var p = _packet;
                ReturnPacket(p);
                _packet = null;
            }

            if (_videoCodecContext != null)
            {
                var c = _videoCodecContext;
                ffmpeg.avcodec_free_context(&c);
                _videoCodecContext = null;
            }

            if (_audioCodecContext != null)
            {
                var c = _audioCodecContext;
                ffmpeg.avcodec_free_context(&c);
                _audioCodecContext = null;
            }

            if (_formatContext != null)
            {
                var f = _formatContext;
                ffmpeg.avformat_close_input(&f);
                _formatContext = null;
            }

            if (_audioFormatContext != null)
            {
                var f = _audioFormatContext;
                ffmpeg.avformat_close_input(&f);
                _audioFormatContext = null;
            }

            while (_videoPacketQueue.TryDequeue(out IntPtr p)) ReturnPacket((AVPacket*)p);
            while (_audioPacketQueue.TryDequeue(out IntPtr p)) ReturnPacket((AVPacket*)p);

            while (_packetPool.TryTake(out IntPtr pooledPkt))
            {
                var p = (AVPacket*)pooledPkt;
                ffmpeg.av_packet_free(&p);
            }
            while (_framePool.TryTake(out IntPtr pooledFrm))
            {
                var f = (AVFrame*)pooledFrm;
                ffmpeg.av_frame_free(&f);
            }
        }

        private int _disposeState = 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeState, 1) == 1) return;
            _isDisposed = true;

            try { Stop(); } catch (Exception ex) { Logger.Error("Unhandled exception caught in FFmpegMediaDecoder empty catch block", ex); }

            int waited = 0;
            const int maxWaitMs = 5000;
            while ((Interlocked.CompareExchange(ref _activeThreads, 0, 0) > 0 || Interlocked.CompareExchange(ref _isOpening, 0, 0) == 1) && waited < maxWaitMs)
            {
                Thread.Sleep(10);
                waited += 10;
            }

            if (Interlocked.CompareExchange(ref _activeThreads, 0, 0) > 0 || Interlocked.CompareExchange(ref _isOpening, 0, 0) == 1)
            {
                Logger.Warn("Decoder dispose timed out while FFmpeg work is still active; cleanup deferred until decoder work exits.");
                GC.SuppressFinalize(this);
                return;
            }

            try { Cleanup(); } catch (Exception ex) { Logger.Error("Unhandled exception caught in FFmpegMediaDecoder empty catch block", ex); }

            GC.SuppressFinalize(this);
        }
    }
}