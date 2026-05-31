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

        private AVFrame* _frame;
        private AVPacket* _packet;

        private Thread? _decodeThread;
        private bool _isRunning;
        private bool _isPaused;

        private readonly object _lock = new object();
        public bool IsRunning => _isRunning;

        public event Action<IntPtr, int, int, int>? FrameDecoded;
        public event Action<byte[], int>? AudioDataAvailable;
        public event Action<double>? PositionChanged; // 0.0 to 1.0 ratio
        public event Action<TimeSpan, TimeSpan>? TimeUpdated; // Current, Total
        public event Action? PlaybackFinished;
        public event Action? SeekPerformed;

        public Func<double>? GetAudioBufferedDurationMs { get; set; }

        public int Width => _width;
        public int Height => _height;

        private string? _currentPath;

        private double _seekRequestRatio = -1;
        private double _playbackSpeed = 1.0;

        private bool _isFinished;
        private bool _speedChanged;
        private bool _isDisposed;

        private DecoderStats _stats;
        private int _framesDecodedThisSecond;
        private DateTime _lastFpsCalcTime;
        private double _totalDecodeTimeMs;
        private int _decodeTimeSamples;

        public bool IsPlaying => _isRunning && !_isPaused;

        public DecoderStats GetStats() => _stats;

        static FFmpegMediaDecoder()
        {
            ffmpeg.RootPath = AppContext.BaseDirectory;
        }

        public void Open(string path)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(FFmpegMediaDecoder));
            Stop();

            _currentPath = path;

            try
            {
                fixed (AVFormatContext** pFormatContext = &_formatContext)
                {
                    if (ffmpeg.avformat_open_input(pFormatContext, path, null, null) < 0)
                        throw new Exception("Could not open file");
                }

                if (ffmpeg.avformat_find_stream_info(_formatContext, null) < 0)
                    throw new Exception("Could not find stream info");

                for (int i = 0; i < _formatContext->nb_streams; i++)
                {
                    if (_formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && _videoStreamIndex == -1)
                    {
                        _videoStreamIndex = i;
                    }
                    else if (_formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && _audioStreamIndex == -1)
                    {
                        _audioStreamIndex = i;
                    }
                }

                if (_videoStreamIndex == -1)
                    throw new Exception("Could not find video stream");

                // --- Setup Video ---
                var videoCodecPar = _formatContext->streams[_videoStreamIndex]->codecpar;
                var videoCodec = ffmpeg.avcodec_find_decoder(videoCodecPar->codec_id);
                _videoCodecContext = ffmpeg.avcodec_alloc_context3(videoCodec);
                _videoCodecContext->thread_count = 0; 
                _videoCodecContext->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
                ffmpeg.avcodec_parameters_to_context(_videoCodecContext, videoCodecPar);

                // Attempt to initialize Hardware Decoding (D3D11VA)
                AVBufferRef* hwDeviceCtx = null;
                if (ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0) == 0)
                {
                    _videoCodecContext->hw_device_ctx = ffmpeg.av_buffer_ref(hwDeviceCtx);
                    ffmpeg.av_buffer_unref(&hwDeviceCtx);
                }
                else if (ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2, null, null, 0) == 0)
                {
                    _videoCodecContext->hw_device_ctx = ffmpeg.av_buffer_ref(hwDeviceCtx);
                    ffmpeg.av_buffer_unref(&hwDeviceCtx);
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
                    ThreadCount = _videoCodecContext->thread_count == 0 ? Environment.ProcessorCount : _videoCodecContext->thread_count
                };

                // --- Setup Audio ---
                if (_audioStreamIndex != -1)
                {
                    var audioCodecPar = _formatContext->streams[_audioStreamIndex]->codecpar;
                    var audioCodec = ffmpeg.avcodec_find_decoder(audioCodecPar->codec_id);
                    _audioCodecContext = ffmpeg.avcodec_alloc_context3(audioCodec);
                    ffmpeg.avcodec_parameters_to_context(_audioCodecContext, audioCodecPar);
                    if (ffmpeg.avcodec_open2(_audioCodecContext, audioCodec, null) < 0)
                        throw new Exception("Could not open audio codec");

                    AudioSampleRate = 48000;
                    AudioChannels = 2;
                    AVSampleFormat targetSampleFormat = AVSampleFormat.AV_SAMPLE_FMT_S16;

                    _audioMaxBufferSize = 192000; // Large enough buffer for resampled audio
                    _audioBuffer = new byte[_audioMaxBufferSize];
                    _audioBufferHandle = GCHandle.Alloc(_audioBuffer, GCHandleType.Pinned);
                    _audioBufferPointer = _audioBufferHandle.AddrOfPinnedObject();

                    _stats.AudioInfo = $"{AudioSampleRate}Hz {AudioChannels}ch {ffmpeg.avcodec_get_name(_audioCodecContext->codec_id)}";
                }
                else
                {
                    _stats.AudioInfo = "No Audio";
                }

                _frame = ffmpeg.av_frame_alloc();
                _packet = ffmpeg.av_packet_alloc();

                _isRunning = true;
                _isPaused = true;
                _isFinished = false;

                _decodeThread = new Thread(DecodeLoop) { IsBackground = true, Name = "FFmpegDecoderThread" };
                _decodeThread.Start();
            }
            catch
            {
                Cleanup();
                throw;
            }
        }

        public void Play()
        {
            lock (_lock)
            {
                _isPaused = false;
                Monitor.Pulse(_lock);
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
            _isRunning = false;
            lock (_lock)
            {
                _isPaused = false;
                Monitor.Pulse(_lock);
            }

            if (_decodeThread != null && _decodeThread.IsAlive)
            {
                if (!_decodeThread.Join(3000))
                {
                    throw new Exception("Fatal Error: Decode thread is deadlocked and could not be stopped.");
                }
            }
            _decodeThread = null;

            Cleanup();
        }

        public void Seek(double ratio)
        {
            lock (_lock)
            {
                _seekRequestRatio = ratio;
                _isFinished = false;
                Monitor.Pulse(_lock);
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
                    Monitor.Pulse(_lock);
                }
            }
        }

        private void DecodeLoop()
        {
            var videoTimeBase = _formatContext->streams[_videoStreamIndex]->time_base;
            double durationInSeconds = _formatContext->duration / (double)ffmpeg.AV_TIME_BASE;
            var totalTime = TimeSpan.FromSeconds(durationInSeconds);

            double fps = ffmpeg.av_q2d(_formatContext->streams[_videoStreamIndex]->r_frame_rate);
            if (fps <= 0) fps = 29.97;

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
                lock (_lock)
                {
                    while (_isPaused && _isRunning && _seekRequestRatio < 0)
                    {
                        stopwatch.Stop();
                        Monitor.Wait(_lock);
                    }

                    if (!_isRunning) break;

                    if (_seekRequestRatio >= 0)
                    {
                        double targetSeconds = _seekRequestRatio * durationInSeconds;
                        long targetTimestamp = (long)(targetSeconds * ffmpeg.AV_TIME_BASE);

                        ffmpeg.av_seek_frame(_formatContext, -1, targetTimestamp, ffmpeg.AVSEEK_FLAG_BACKWARD);
                        ffmpeg.avcodec_flush_buffers(_videoCodecContext);
                        if (_audioStreamIndex != -1)
                        {
                            ffmpeg.avcodec_flush_buffers(_audioCodecContext);
                        }
                        
                        SeekPerformed?.Invoke();
                        
                        targetSeekMs = targetSeconds * 1000.0;
                        stopwatch.Reset();
                        _lastValidAudioPtsTime = -1;
                        _lastValidPtsTime = -1;
                        _seekRequestRatio = -1;
                    }
                }

                if (_isFinished)
                {
                    Thread.Sleep(10);
                    continue;
                }

                int readRes = ffmpeg.av_read_frame(_formatContext, _packet);
                if (readRes < 0)
                {
                    if (!_isFinished)
                    {
                        _isFinished = true;
                        _isPaused = true;
                        PlaybackFinished?.Invoke();
                    }
                    Thread.Sleep(10);
                    continue;
                }

                if (_packet->stream_index == _videoStreamIndex)
                {
                    int sendRes = ffmpeg.avcodec_send_packet(_videoCodecContext, _packet);
                    if (sendRes >= 0)
                    {
                        while (true)
                        {
                            decodeTimer.Restart();
                            if (ffmpeg.avcodec_receive_frame(_videoCodecContext, _frame) < 0) break;
                            decodeTimer.Stop();

                            AVFrame* processedFrame = _frame;
                            AVFrame* swFrame = null;
                            if (_frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11 || 
                                _frame->format == (int)AVPixelFormat.AV_PIX_FMT_DXVA2_VLD ||
                                _frame->format == (int)AVPixelFormat.AV_PIX_FMT_CUDA)
                            {
                                swFrame = ffmpeg.av_frame_alloc();
                                if (ffmpeg.av_hwframe_transfer_data(swFrame, _frame, 0) == 0)
                                {
                                    swFrame->pts = _frame->pts;
                                    swFrame->best_effort_timestamp = _frame->best_effort_timestamp;
                                    swFrame->pkt_dts = _frame->pkt_dts;
                                    processedFrame = swFrame;
                                }
                                else
                                {
                                    ffmpeg.av_frame_free(&swFrame);
                                    continue; // Hardware transfer failed, drop frame
                                }
                            }

                            if (processedFrame->width <= 0 || processedFrame->height <= 0)
                            {
                                if (swFrame != null) ffmpeg.av_frame_free(&swFrame);
                                continue;
                            }

                            long pts = _frame->best_effort_timestamp;
                            if (pts == ffmpeg.AV_NOPTS_VALUE) pts = _frame->pts;
                            if (pts == ffmpeg.AV_NOPTS_VALUE) pts = _frame->pkt_dts;
                            
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

                            if (targetSeekMs >= 0)
                            {
                                if (ptsTime < targetSeekMs - 200)
                                {
                                    continue;
                                }
                                targetSeekMs = -1;
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

                                if (Math.Abs(diff) > 2000)
                                {
                                    currentPlaybackPtsTime = audioClock;
                                    stopwatch.Restart();
                                    masterClockPtsTime = audioClock;
                                }
                                else if (Math.Abs(diff) > 20)
                                {
                                    // Smoothly pull System Clock towards Audio Clock to prevent stutter
                                    currentPlaybackPtsTime += diff * 0.1;
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

                            if (delay > 2000 || delay < -2000) 
                            {
                                // Massive timestamp jump or severe lag, resync stopwatch
                                stopwatch.Restart();
                                currentPlaybackPtsTime = ptsTime;
                            }
                            else if (delay > 0)
                            {
                                lock (_lock)
                                {
                                    if (_isRunning)
                                    {
                                        Monitor.Wait(_lock, (int)(delay / _playbackSpeed));
                                    }
                                }
                            }

                            decodeTimer.Start();
                            if (_swsContext == null)
                            {
                                _swsContext = ffmpeg.sws_getContext(
                                    processedFrame->width, processedFrame->height, (AVPixelFormat)processedFrame->format,
                                    _width, _height, AVPixelFormat.AV_PIX_FMT_BGRA,
                                    1, null, null, null
                                );
                            }
                            
                            byte*[] dstData = { (byte*)_bgraBufferPointer, null, null, null };
                            int[] dstLinesize = { _width * 4, 0, 0, 0 };
                            ffmpeg.sws_scale(
                                _swsContext, processedFrame->data, processedFrame->linesize, 0, _height,
                                dstData, dstLinesize
                            );

                            if (swFrame != null) ffmpeg.av_frame_free(&swFrame);
                            decodeTimer.Stop();

                            _totalDecodeTimeMs += decodeTimer.Elapsed.TotalMilliseconds;
                            _decodeTimeSamples++;
                            _framesDecodedThisSecond++;

                            var now = DateTime.UtcNow;
                            if ((now - _lastFpsCalcTime).TotalMilliseconds >= 1000)
                            {
                                _stats.ActualFps = _framesDecodedThisSecond;
                                if (_decodeTimeSamples > 0)
                                    _stats.AvgDecodeTimeMs = _totalDecodeTimeMs / _decodeTimeSamples;
                                
                                _framesDecodedThisSecond = 0;
                                _totalDecodeTimeMs = 0;
                                _decodeTimeSamples = 0;
                                _lastFpsCalcTime = now;
                            }

                            FrameDecoded?.Invoke(_bgraBufferPointer, _width, _height, _width * 4);

                            double currentTimeSeconds = ptsTime / 1000.0;
                            double ratio = currentTimeSeconds / durationInSeconds;

                            PositionChanged?.Invoke(ratio);
                            TimeUpdated?.Invoke(TimeSpan.FromSeconds(currentTimeSeconds), totalTime);
                        }
                    }
                }
                else if (_packet->stream_index == _audioStreamIndex && _audioStreamIndex != -1)
                {
                    int sendRes = ffmpeg.avcodec_send_packet(_audioCodecContext, _packet);
                    if (sendRes >= 0)
                    {
                        while (true)
                        {
                            if (ffmpeg.avcodec_receive_frame(_audioCodecContext, _frame) < 0) break;

                            if (_frame->nb_samples <= 0)
                            {
                                continue;
                            }

                            if (_swrContext == null)
                            {
                                AVChannelLayout out_ch_layout;
                                ffmpeg.av_channel_layout_default(&out_ch_layout, AudioChannels);
                                
                                AVChannelLayout in_ch_layout = _frame->ch_layout;
                                SwrContext* swr = null;
                                int ret = ffmpeg.swr_alloc_set_opts2(
                                    &swr,
                                    &out_ch_layout,
                                    AVSampleFormat.AV_SAMPLE_FMT_S16,
                                    AudioSampleRate,
                                    &in_ch_layout,
                                    (AVSampleFormat)_frame->format,
                                    _frame->sample_rate,
                                    0, null
                                );
                                _swrContext = swr;
                                if (ret < 0 || _swrContext == null) throw new Exception("Could not allocate SwrContext dynamically");
                                ffmpeg.swr_init(_swrContext);
                            }

                            // If we are seeking, discard audio frames before the target
                            long pts = _frame->best_effort_timestamp;
                            if (pts == ffmpeg.AV_NOPTS_VALUE) pts = _frame->pts;
                            if (pts == ffmpeg.AV_NOPTS_VALUE) pts = _frame->pkt_dts;
                            
                            var audioTimeBase = _formatContext->streams[_audioStreamIndex]->time_base;
                            double audioPtsTime = 0;
                            if (pts != ffmpeg.AV_NOPTS_VALUE)
                            {
                                audioPtsTime = pts * ffmpeg.av_q2d(audioTimeBase) * 1000.0;
                                _lastValidAudioPtsTime = audioPtsTime;
                            }
                            else
                            {
                                // Fallback for broken audio pts
                                double frameDur = 1000.0 * _frame->nb_samples / AudioSampleRate;
                                audioPtsTime = _lastValidAudioPtsTime + frameDur;
                                _lastValidAudioPtsTime = audioPtsTime;
                            }
                            
                            if (targetSeekMs >= 0 && audioPtsTime < targetSeekMs - 200)
                            {
                                continue;
                            }

                            
                            byte* pOutData = (byte*)_audioBufferPointer;
                            byte** outDataPtr = &pOutData;
                            int outSamples = ffmpeg.swr_get_out_samples(_swrContext, _frame->nb_samples);
                            
                            int numSamplesConverted = ffmpeg.swr_convert(
                                _swrContext, 
                                outDataPtr, outSamples, 
                                _frame->extended_data, _frame->nb_samples);
                                
                            if (numSamplesConverted > 0)
                            {
                                int bufferSize = ffmpeg.av_samples_get_buffer_size(null, AudioChannels, numSamplesConverted, AVSampleFormat.AV_SAMPLE_FMT_S16, 1);
                                if (bufferSize > 0 && bufferSize <= _audioMaxBufferSize)
                                {
                                    byte[] managedBuffer = new byte[bufferSize];
                                    Marshal.Copy(_audioBufferPointer, managedBuffer, 0, bufferSize);
                                    
                                    AudioDataAvailable?.Invoke(managedBuffer, bufferSize);
                                }
                            }
                        }
                    }
                }
                
                ffmpeg.av_packet_unref(_packet);
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

            if (_frame != null)
            {
                var f = _frame;
                ffmpeg.av_frame_free(&f);
                _frame = null;
            }

            if (_packet != null)
            {
                var p = _packet;
                ffmpeg.av_packet_free(&p);
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
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            Stop();
            _isDisposed = true;
        }
    }
}
