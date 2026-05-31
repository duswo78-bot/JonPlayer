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
        public double TargetFps;
        public double ActualFps;
        public double AvgDecodeTimeMs;
        public double SyncDelayMs;
        public int LateFrames;
        public int ThreadCount;
    }

    public unsafe class FFmpegVideoDecoder : IDisposable
    {
        private AVFormatContext* _formatContext;
        private AVCodecContext* _codecContext;
        private AVFrame* _frame;
        private AVPacket* _packet;
        private SwsContext* _swsContext;

        private int _videoStreamIndex = -1;
        private int _width;
        private int _height;

        private Thread? _decodeThread;
        private bool _isRunning;
        private bool _isPaused;

        private readonly object _lock = new object();
        public bool IsRunning => _isRunning;

        public event Action<IntPtr, int, int, int>? FrameDecoded;
        public event Action<double>? PositionChanged; // 0.0 to 1.0 ratio
        public event Action<TimeSpan, TimeSpan>? TimeUpdated; // Current, Total
        public event Action? PlaybackFinished;

        public int Width => _width;
        public int Height => _height;

        private string? _currentPath;
        private byte[]? _bgraBuffer;
        private GCHandle _bgraBufferHandle;
        private IntPtr _bgraBufferPointer;

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

        static FFmpegVideoDecoder()
        {
            // Dynamically register FFmpeg root path so DLLs are discovered
            ffmpeg.RootPath = AppContext.BaseDirectory;
        }

        public void Open(string path)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(FFmpegVideoDecoder));
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
                    if (_formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        _videoStreamIndex = i;
                        break;
                    }
                }

                if (_videoStreamIndex == -1)
                    throw new Exception("Could not find video stream");

                var codecParameters = _formatContext->streams[_videoStreamIndex]->codecpar;
                var codec = ffmpeg.avcodec_find_decoder(codecParameters->codec_id);
                _codecContext = ffmpeg.avcodec_alloc_context3(codec);

                // Enable Multithreaded Decoding
                _codecContext->thread_count = 0; // 0 = Auto-detect CPU cores
                _codecContext->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;

                ffmpeg.avcodec_parameters_to_context(_codecContext, codecParameters);

                if (ffmpeg.avcodec_open2(_codecContext, codec, null) < 0)
                    throw new Exception("Could not open codec");

                _width = _codecContext->width;
                _height = _codecContext->height;

                _frame = ffmpeg.av_frame_alloc();
                _packet = ffmpeg.av_packet_alloc();

                _swsContext = ffmpeg.sws_getContext(
                    _width, _height, _codecContext->pix_fmt,
                    _width, _height, AVPixelFormat.AV_PIX_FMT_BGRA,
                    1, null, null, null // 1 = SWS_FAST_BILINEAR
                );

                _stats = new DecoderStats
                {
                    VideoInfo = $"{_width}x{_height} {ffmpeg.avcodec_get_name(_codecContext->codec_id)}",
                    ThreadCount = _codecContext->thread_count == 0 ? Environment.ProcessorCount : _codecContext->thread_count
                };

                _bgraBuffer = new byte[_width * _height * 4];
                _bgraBufferHandle = GCHandle.Alloc(_bgraBuffer, GCHandleType.Pinned);
                _bgraBufferPointer = _bgraBufferHandle.AddrOfPinnedObject();

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
                _isPaused = false; // Ensure decode loop isn't stuck in Monitor.Wait
                Monitor.Pulse(_lock);
            }

            if (_decodeThread != null && _decodeThread.IsAlive)
            {
                if (!_decodeThread.Join(3000))
                {
                    // Thread didn't exit in time. If we cleanup now, the running thread will access freed pointers and cause 0x80131506!
                    throw new Exception("Fatal Error: Decode thread is deadlocked and could not be stopped. Please restart the application.");
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
            var timeBase = _formatContext->streams[_videoStreamIndex]->time_base;
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

                    // Handle Seek
                    if (_seekRequestRatio >= 0)
                    {
                        double targetSeconds = _seekRequestRatio * durationInSeconds;
                        long targetTimestamp = (long)(targetSeconds * ffmpeg.AV_TIME_BASE);

                        ffmpeg.av_seek_frame(_formatContext, -1, targetTimestamp, ffmpeg.AVSEEK_FLAG_BACKWARD);
                        ffmpeg.avcodec_flush_buffers(_codecContext);

                        targetSeekMs = targetSeconds * 1000.0;
                        stopwatch.Reset();
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
                    int sendRes = ffmpeg.avcodec_send_packet(_codecContext, _packet);
                    if (sendRes >= 0)
                    {
                        while (true)
                        {
                            decodeTimer.Restart();
                            if (ffmpeg.avcodec_receive_frame(_codecContext, _frame) < 0) break;
                            decodeTimer.Stop();

                            double ptsTime = _frame->best_effort_timestamp * ffmpeg.av_q2d(timeBase) * 1000.0; // ms

                            if (targetSeekMs >= 0)
                            {
                                if (ptsTime < targetSeekMs - 200) // Discard frames until we are within 200ms of target
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

                            // Sync / Delay logic based on stopwatch and speed
                            if (!stopwatch.IsRunning)
                            {
                                stopwatch.Restart();
                                currentPlaybackPtsTime = ptsTime;
                            }

                            double elapsed = stopwatch.ElapsedMilliseconds * _playbackSpeed;
                            double targetPtsDelta = ptsTime - currentPlaybackPtsTime;
                            double delay = targetPtsDelta - elapsed;

                            _stats.SyncDelayMs = delay;
                            if (delay < -30.0) _stats.LateFrames++;

                            if (delay > 0 && delay < 2000)
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
                            // Convert to BGRA via SwScale
                            byte*[] dstData = { (byte*)_bgraBufferPointer, null, null, null };
                            int[] dstLinesize = { _width * 4, 0, 0, 0 };
                            ffmpeg.sws_scale(
                                _swsContext, _frame->data, _frame->linesize, 0, _height,
                                dstData, dstLinesize
                            );
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

            if (_swsContext != null)
            {
                ffmpeg.sws_freeContext(_swsContext);
                _swsContext = null;
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

            if (_codecContext != null)
            {
                var c = _codecContext;
                ffmpeg.avcodec_free_context(&c);
                _codecContext = null;
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
