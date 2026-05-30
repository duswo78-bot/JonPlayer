using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Diagnostics;
using FFmpeg.AutoGen;

namespace JonPlayer
{
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

        public event Action<IntPtr, int, int, int>? FrameDecoded;
        public event Action<double>? PositionChanged; // 0.0 to 1.0 ratio
        public event Action<TimeSpan, TimeSpan>? TimeUpdated; // Current, Total

        public int Width => _width;
        public int Height => _height;

        private string? _currentPath;
        private byte[]? _bgraBuffer;
        private GCHandle _bgraBufferHandle;
        private IntPtr _bgraBufferPointer;

        private double _seekRequestRatio = -1;
        private double _playbackSpeed = 1.0;

        private bool _isFinished;

        public bool IsPlaying => _isRunning && !_isPaused;

        static FFmpegVideoDecoder()
        {
            // Dynamically register FFmpeg root path so DLLs are discovered
            ffmpeg.RootPath = AppContext.BaseDirectory;
        }

        public void Open(string path)
        {
            Stop();

            _currentPath = path;

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
                2, null, null, null
            );

            _bgraBuffer = new byte[_width * _height * 4];
            _bgraBufferHandle = GCHandle.Alloc(_bgraBuffer, GCHandleType.Pinned);
            _bgraBufferPointer = _bgraBufferHandle.AddrOfPinnedObject();

            _isRunning = true;
            _isPaused = true;
            _isFinished = false;

            _decodeThread = new Thread(DecodeLoop) { IsBackground = true, Name = "FFmpegDecoderThread" };
            _decodeThread.Start();
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
                _playbackSpeed = speed;
            }
        }

        private void DecodeLoop()
        {
            var timeBase = _formatContext->streams[_videoStreamIndex]->time_base;
            double durationInSeconds = _formatContext->duration / (double)ffmpeg.AV_TIME_BASE;
            var totalTime = TimeSpan.FromSeconds(durationInSeconds);

            double fps = ffmpeg.av_q2d(_formatContext->streams[_videoStreamIndex]->r_frame_rate);
            if (fps <= 0) fps = 29.97;

            var stopwatch = new Stopwatch();
            double currentPlaybackPtsTime = 0;

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
                        long targetPts = (long)(targetSeconds / ffmpeg.av_q2d(timeBase));

                        ffmpeg.av_seek_frame(_formatContext, _videoStreamIndex, targetPts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                        ffmpeg.avcodec_flush_buffers(_codecContext);

                        currentPlaybackPtsTime = targetSeconds * 1000.0;
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
                    _isFinished = true;
                    _isPaused = true;
                    continue;
                }

                if (_packet->stream_index == _videoStreamIndex)
                {
                    int sendRes = ffmpeg.avcodec_send_packet(_codecContext, _packet);
                    if (sendRes >= 0)
                    {
                        while (ffmpeg.avcodec_receive_frame(_codecContext, _frame) >= 0)
                        {
                            double ptsTime = _frame->best_effort_timestamp * ffmpeg.av_q2d(timeBase) * 1000.0; // ms

                            // Sync / Delay logic based on stopwatch and speed
                            if (!stopwatch.IsRunning)
                            {
                                stopwatch.Start();
                                currentPlaybackPtsTime = ptsTime;
                            }

                            double elapsed = stopwatch.ElapsedMilliseconds * _playbackSpeed;
                            double targetPtsDelta = ptsTime - currentPlaybackPtsTime;

                            if (targetPtsDelta > elapsed)
                            {
                                double delay = targetPtsDelta - elapsed;
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
                            }

                            // Convert to BGRA via SwScale
                            byte*[] dstData = { (byte*)_bgraBufferPointer, null, null, null };
                            int[] dstLinesize = { _width * 4, 0, 0, 0 };
                            ffmpeg.sws_scale(
                                _swsContext, _frame->data, _frame->linesize, 0, _height,
                                dstData, dstLinesize
                            );

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
                _bgraBufferHandle.Free();

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
            Stop();
        }
    }
}
