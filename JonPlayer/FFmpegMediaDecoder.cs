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
        private AVFormatContext* _formatContext;
        
        // Video
        private AVCodecContext* _videoCodecContext;
        private int _videoStreamIndex = -1;
        private int _width;
        private int _height;
        private SwsContext* _swsContext;
        
        private double _lastValidPtsTime = 0.0;
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
        public event Action<double>? PositionChanged; // 0.0 to 1.0 ratio
        public event Action<TimeSpan, TimeSpan>? TimeUpdated; // Current, Total
        public event Action? PlaybackFinished;
        public event Action? SeekPerformed;
        public event Action? SeekInitiated;

        public Func<double>? GetAudioBufferedDurationMs { get; set; }

        public double DurationSeconds => _formatContext != null ? _formatContext->duration / (double)ffmpeg.AV_TIME_BASE : 0;

        public int Width => _width;
        public int Height => _height;

        private string? _currentPath;
        private double _seekTargetMs = -1;
        private volatile bool _isFinished;
        private volatile bool _notifiedPlaybackFinished;

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

        public void Open(string path)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(FFmpegMediaDecoder));
            Stop();

            _currentPath = path;
            
            _videoStreamIndex = -1;
            _audioStreamIndex = -1;
            _isInterruptRequested = false;

            try
            {
                _formatContext = ffmpeg.avformat_alloc_context();
                _interruptCallback = new InterruptCallbackDelegate(InterruptCallback);
                _formatContext->interrupt_callback.callback = new FFmpeg.AutoGen.AVIOInterruptCB_callback_func { Pointer = Marshal.GetFunctionPointerForDelegate(_interruptCallback) };
                _formatContext->interrupt_callback.opaque = null;

                fixed (AVFormatContext** pFormatContext = &_formatContext)
                {
                    if (ffmpeg.avformat_open_input(pFormatContext, path, null, null) < 0)
                        throw new Exception("Could not open file");
                }

                _formatContext->probesize = 5000000;
                _formatContext->max_analyze_duration = 2 * FFmpeg.AutoGen.ffmpeg.AV_TIME_BASE;

                if (ffmpeg.avformat_find_stream_info(_formatContext, null) < 0)
                    throw new Exception("Could not find stream info");

                ffmpeg.avformat_seek_file(_formatContext, -1, long.MinValue, 0, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);

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

                if (_videoStreamIndex != -1)
                {
                    // Check for rotation metadata
                    var stream = _formatContext->streams[_videoStreamIndex];
                    double rotation = 0;
                    bool rotationFound = false;

                    // Method 1: AV_PKT_DATA_DISPLAYMATRIX
                    var codecpar = _formatContext->streams[_videoStreamIndex]->codecpar;
                    var displayMatrixData = ffmpeg.av_packet_side_data_get(codecpar->coded_side_data, codecpar->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
                    
                    if (displayMatrixData != null && displayMatrixData->size >= 9 * sizeof(int))
                    {
                        var pMatrix = (FFmpeg.AutoGen.int_array9*)displayMatrixData->data;
                        double theta = ffmpeg.av_display_rotation_get(in *pMatrix);
                        if (!double.IsNaN(theta))
                        {
                            rotation = -theta; // FFmpeg is CCW, WPF is CW
                            rotationFound = true;
                        }
                    }

                    // Method 2: rotate metadata tag (fallback)
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

                    // --- Setup Video ---
                    var videoCodecPar = _formatContext->streams[_videoStreamIndex]->codecpar;
                    var videoCodec = ffmpeg.avcodec_find_decoder(videoCodecPar->codec_id);
                    _videoCodecContext = ffmpeg.avcodec_alloc_context3(videoCodec);
                    ffmpeg.avcodec_parameters_to_context(_videoCodecContext, videoCodecPar);
                    _videoCodecContext->thread_count = 0; 
                    _videoCodecContext->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;

                    _getFormatCallback = GetFormat;
                    _videoCodecContext->get_format = new AVCodecContext_get_format_func { Pointer = Marshal.GetFunctionPointerForDelegate(_getFormatCallback) };

                    AVBufferRef* hwDeviceCtx = null;
                    if (_d3d11DevicePtr != IntPtr.Zero)
                    {
                        hwDeviceCtx = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
                        if (hwDeviceCtx != null)
                        {
                            var deviceCtx = (AVHWDeviceContext*)hwDeviceCtx->data;
                            var d3d11DeviceCtx = (AVD3D11VADeviceContext*)deviceCtx->hwctx;
                            // FFmpeg will take ownership of these COM pointers and call Release() on them
                            // when the hwdevice_ctx is freed. Therefore, we MUST AddRef them here to prevent
                            // premature destruction of our D3D11 device and context.
                            System.Runtime.InteropServices.Marshal.AddRef(_d3d11DevicePtr);
                            d3d11DeviceCtx->device = _d3d11DevicePtr;
                            
                            System.Runtime.InteropServices.Marshal.AddRef(_d3d11ContextPtr);
                            d3d11DeviceCtx->device_context = _d3d11ContextPtr;

                            if (ffmpeg.av_hwdevice_ctx_init(hwDeviceCtx) == 0)
                            {
                                _videoCodecContext->hw_device_ctx = ffmpeg.av_buffer_ref(hwDeviceCtx);
                            }
                            ffmpeg.av_buffer_unref(&hwDeviceCtx);
                        }
                    }

                    if (_videoCodecContext->hw_device_ctx == null)
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

                // --- Setup Audio ---
                if (_audioStreamIndex != -1)
                {
                    var audioCodecPar = _formatContext->streams[_audioStreamIndex]->codecpar;
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

                _videoPacketQueue.Clear();
                _audioPacketQueue.Clear();

                _audioFrame = GetFrame();
                _videoFrame = GetFrame();
                _packet = GetPacket();
                _isRunning = true;
                _isPaused = true;
                _isFinished = false;

                _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "FFmpegReadThread" };
                _readThread.Start();
                if (HasVideo)
                {
                    _videoThread = new Thread(VideoDecodeLoop) { IsBackground = true, Name = "FFmpegVideoThread" };
                    _videoThread.Start();
                }
                
                if (_audioStreamIndex != -1)
                {
                    _audioThread = new Thread(AudioDecodeLoop) { IsBackground = true, Name = "FFmpegAudioThread" };
                    _audioThread.Start();
                }
            }
            catch
            {
                Cleanup();
                throw;
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
            
            // SW Fallback
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

            AVRational timeBase = _formatContext->streams[_audioStreamIndex]->time_base;

            AVChannelLayout chLayout = _audioCodecContext->ch_layout;
            byte* layoutDesc = stackalloc byte[128];
            ffmpeg.av_channel_layout_describe(&chLayout, layoutDesc, 128);
            string chLayoutStr = System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)layoutDesc) ?? "stereo";

            string sampleFmtStr = ffmpeg.av_get_sample_fmt_name(_audioCodecContext->sample_fmt);
            if (sampleFmtStr == null) sampleFmtStr = "s16";

            string args = $"time_base={timeBase.num}/{timeBase.den}:sample_rate={_audioCodecContext->sample_rate}:sample_fmt={sampleFmtStr}:channel_layout={chLayoutStr}";

            AVFilterContext* abufferCtx = null;
            int ret1 = ffmpeg.avfilter_graph_create_filter(&abufferCtx, abuffer, "in", args, null, _audioFilterGraph);
            if (ret1 < 0) 
            {
                System.Diagnostics.Debug.WriteLine($"Failed to create abuffer filter. Error: {ret1}. Args: {args}");
                fixed (AVFilterGraph** pGraph = &_audioFilterGraph) ffmpeg.avfilter_graph_free(pGraph);
                return;
            }

            AVFilterContext* abuffersinkCtx = null;
            int ret2 = ffmpeg.avfilter_graph_create_filter(&abuffersinkCtx, abuffersink, "out", null, null, _audioFilterGraph);
            if (ret2 < 0) 
            {
                fixed (AVFilterGraph** pGraph = &_audioFilterGraph) ffmpeg.avfilter_graph_free(pGraph);
                return;
            }

            string filterDesc = $"atempo={_playbackSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

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

            if (ffmpeg.avfilter_graph_parse_ptr(_audioFilterGraph, filterDesc, &inputs, &outputs, null) < 0)
            {
                ffmpeg.avfilter_inout_free(&inputs);
                ffmpeg.avfilter_inout_free(&outputs);
                fixed (AVFilterGraph** pGraph = &_audioFilterGraph) ffmpeg.avfilter_graph_free(pGraph);
                return;
            }
            if (ffmpeg.avfilter_graph_config(_audioFilterGraph, null) < 0)
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
            
            // Find atempo filter
            _atempoCtx = ffmpeg.avfilter_graph_get_filter(_audioFilterGraph, "Parsed_atempo_0");
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

            if (_readThread != null && _readThread.IsAlive)
            {
                if (!_readThread.Join(1000))
                {
                    System.Diagnostics.Debug.WriteLine("ReadThread join timed out.");
                }
            }
            if (_videoThread != null && _videoThread.IsAlive)
            {
                if (!_videoThread.Join(1000))
                {
                    System.Diagnostics.Debug.WriteLine("VideoThread join timed out.");
                }
            }
            if (_audioThread != null && _audioThread.IsAlive)
            {
                if (!_audioThread.Join(1000))
                {
                    System.Diagnostics.Debug.WriteLine("AudioThread join timed out.");
                }
            }

            while (_videoPacketQueue.TryDequeue(out IntPtr p))
            {
                var pt = (AVPacket*)p;
                ReturnPacket(pt);
            }
            while (_audioPacketQueue.TryDequeue(out IntPtr p))
            {
                var pt = (AVPacket*)p;
                ReturnPacket(pt);
            }

            Cleanup();
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

                    if (_audioFilterGraph != null && _atempoCtx != null)
                    {
                        string speedStr = speed.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        ffmpeg.avfilter_graph_send_command(_audioFilterGraph, "Parsed_atempo_0", "tempo", speedStr, null, 0, 0);
                    }
                    Monitor.PulseAll(_lock);
                }
            }
        }

        private void ReadLoop()
        {
            while (_isRunning)
            {
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

                    if (_isPaused)
                    {
                        Monitor.Wait(_lock, 50);
                        continue;
                    }

                    while (_isRunning && ((_videoStreamIndex != -1 && _videoPacketQueue.Count > 60) || (_audioStreamIndex != -1 && _audioPacketQueue.Count > 100)))
                    {
                        if (_seekTargetMs >= 0) break;
                        Monitor.Wait(_lock, 50);
                    }
                    
                    if (_seekTargetMs >= 0)
                    {
                        long seekTarget = (long)(_seekTargetMs / 1000.0 * ffmpeg.AV_TIME_BASE);
                        ffmpeg.avformat_seek_file(_formatContext, -1, long.MinValue, seekTarget, seekTarget, ffmpeg.AVSEEK_FLAG_BACKWARD);
                        
                        while (_videoPacketQueue.TryDequeue(out IntPtr p))
                        {
                            var pt = (AVPacket*)p;
                            ReturnPacket(pt);
                        }
                        while (_audioPacketQueue.TryDequeue(out IntPtr p))
                        {
                            var pt = (AVPacket*)p;
                            ReturnPacket(pt);
                        }

                        Interlocked.Exchange(ref _needsVideoFlush, 1);
                        if (_audioStreamIndex != -1) Interlocked.Exchange(ref _needsAudioFlush, 1);

                        _lastValidPtsTime = 0;
                        _lastValidAudioPtsTime = 0;
                        _seekTargetMs = -1;
                        _isFinished = false;
                        _notifiedPlaybackFinished = false;
                        SeekInitiated?.Invoke();
                    }
                }

                if (_isFinished) continue;

                int readRes = ffmpeg.av_read_frame(_formatContext, _packet);
                if (readRes < 0)
                {
                    if (readRes == ffmpeg.AVERROR_EOF)
                    {
                        _isFinished = true;
                        ffmpeg.av_packet_unref(_packet);
                        continue;
                    }
                    ffmpeg.av_packet_unref(_packet);
                    _isFinished = true;
                    break;
                }

                if (_packet->stream_index == _videoStreamIndex)
                {
                    var newPkt = GetPacket();
                    ffmpeg.av_packet_ref(newPkt, _packet);
                    _videoPacketQueue.Enqueue((IntPtr)newPkt);
                    lock (_lock) { Monitor.PulseAll(_lock); }
                }
                else if (_packet->stream_index == _audioStreamIndex && _audioStreamIndex != -1)
                {
                    var newPkt = GetPacket();
                    ffmpeg.av_packet_ref(newPkt, _packet);
                    _audioPacketQueue.Enqueue((IntPtr)newPkt);
                    lock (_lock) { Monitor.PulseAll(_lock); }
                }
                
                ffmpeg.av_packet_unref(_packet);
            }
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
                        _isSeekingAudio = true;
                    }

                    if (GetAudioBufferedDurationMs != null)
                    {
                        while (GetAudioBufferedDurationMs() > 2000 && _isRunning && _needsAudioFlush == 0)
                        {
                            Thread.Sleep(5);
                        }
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

                            if (_audioFilterGraph != null && _abufferCtx != null && _abuffersinkCtx != null)
                            {
                                if (ffmpeg.av_buffersrc_add_frame(_abufferCtx, _audioFrame) >= 0)
                                {
                                    while (true)
                                    {
                                        int sinkRet = ffmpeg.av_buffersink_get_frame(_abuffersinkCtx, _filteredAudioFrame);
                                        if (sinkRet == ffmpeg.AVERROR(ffmpeg.EAGAIN) || sinkRet == ffmpeg.AVERROR_EOF)
                                            break;
                                        if (sinkRet < 0)
                                            break;

                                        ProcessAndConvertAudioFrame(_filteredAudioFrame);
                                        ffmpeg.av_frame_unref(_filteredAudioFrame);
                                    }
                                }
                                continue;
                            }
                            else
                            {
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

            var audioTimeBase = _formatContext->streams[_audioStreamIndex]->time_base;
            long pts = frameToConvert->best_effort_timestamp;
            if (pts == ffmpeg.AV_NOPTS_VALUE) pts = frameToConvert->pts;
            if (pts == ffmpeg.AV_NOPTS_VALUE) pts = frameToConvert->pkt_dts;
            
            double audioPtsTime = 0;
            if (pts != ffmpeg.AV_NOPTS_VALUE)
            {
                audioPtsTime = pts * ffmpeg.av_q2d(audioTimeBase) * 1000.0;
                _lastValidAudioPtsTime = audioPtsTime;
            }
            else
            {
                double frameDur = 1000.0 * frameToConvert->nb_samples / AudioSampleRate;
                audioPtsTime = _lastValidAudioPtsTime + frameDur;
                _lastValidAudioPtsTime = audioPtsTime;
            }
            
            if (_isSeekingAudio)
            {
                if (audioPtsTime < _seekTargetPtsTime - 50)
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
                    byte[] managedBuffer = new byte[bufferSize];
                    System.Runtime.InteropServices.Marshal.Copy(_audioBufferPointer, managedBuffer, 0, bufferSize);
                    AudioDataAvailable?.Invoke(managedBuffer, bufferSize);
                    
                    if (!HasVideo)
                    {
                        double bufferedMs = GetAudioBufferedDurationMs != null ? GetAudioBufferedDurationMs() : 0;
                        double currentTimeSeconds = (audioPtsTime - bufferedMs) / 1000.0;
                        if (currentTimeSeconds < 0) currentTimeSeconds = 0;
                        
                        double durationInSeconds = _formatContext->duration / (double)ffmpeg.AV_TIME_BASE;
                        double ratio = currentTimeSeconds / durationInSeconds;
                        PositionChanged?.Invoke(ratio);
                        TimeUpdated?.Invoke(TimeSpan.FromSeconds(currentTimeSeconds), TimeSpan.FromSeconds(durationInSeconds));
                    }
                }
            }
        }

        private void VideoDecodeLoop()
        {
            var videoTimeBase = _formatContext->streams[_videoStreamIndex]->time_base;
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
            double targetSeekMs = -1;

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
                            ptsTime = pts * ffmpeg.av_q2d(videoTimeBase) * 1000.0;
                            _lastValidPtsTime = ptsTime;
                        }
                        else
                        {
                            double frameDuration = 33.3; // fallback 30fps
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
                            if (ptsTime < _seekTargetPtsTime - 50)
                            {
                                if (swFrame != null) ReturnFrame(swFrame);
                                ReturnFrame(_videoFrame);
                                _videoFrame = GetFrame();
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

                        if (!stopwatch.IsRunning)
                        {
                            stopwatch.Restart();
                            currentPlaybackPtsTime = ptsTime;
                        }

                        double elapsed = stopwatch.ElapsedMilliseconds * _playbackSpeed;
                        double systemClock = currentPlaybackPtsTime + elapsed;
                        double masterClockPtsTime = systemClock;

                        if (_audioStreamIndex != -1 && GetAudioBufferedDurationMs != null && _lastValidAudioPtsTime > 0)
                        {
                            double bufferedMs = GetAudioBufferedDurationMs();
                            double hwLatency = 100.0; // NAudio WaveOutEvent DesiredLatency
                            double audioClock = _lastValidAudioPtsTime - (bufferedMs * _playbackSpeed) - hwLatency;
                            double diff = audioClock - systemClock;

                            if (diff > 2000 || diff < -2000)
                            {
                                currentPlaybackPtsTime = audioClock;
                                stopwatch.Restart();
                                masterClockPtsTime = audioClock;
                            }
                            else if (diff > 100 || diff < -100)
                            {
                                // Pull System Clock smoothly towards Audio Clock
                                currentPlaybackPtsTime += diff * 0.05;
                                masterClockPtsTime = currentPlaybackPtsTime + stopwatch.ElapsedMilliseconds * _playbackSpeed;
                            }
                        }

                        double delay = ptsTime - masterClockPtsTime;

                        _stats.SyncDelayMs = delay;
                        _stats.VideoPts = _lastValidPtsTime;
                        
                        double audioPts = 0;
                        if (_audioStreamIndex != -1 && GetAudioBufferedDurationMs != null && _lastValidAudioPtsTime > 0)
                        {
                            audioPts = _lastValidAudioPtsTime - GetAudioBufferedDurationMs();
                        }
                        _stats.AudioPts = audioPts;
                        
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
                                // sws_getContext failed — skip this frame
                                if (swFrame != null) ReturnFrame(swFrame);
                                ReturnFrame(_videoFrame);
                                _videoFrame = GetFrame();
                                continue;
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
        }

        private void Cleanup()
        {
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

            // Free pooled native objects to prevent memory leak
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

        public void Dispose()
        {
            if (_isDisposed) return;
            Stop();
            _isDisposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
