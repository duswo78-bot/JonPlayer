using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NAudio.Wave;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace JonPlayer
{
    public partial class MainWindow
    {
        private void InitAudioPlayer()
        {
            if (_waveOut != null)
            {
                _waveOut.Stop();
                _waveOut.Dispose();
                _waveOut = null;
            }

            if (_decoder != null && _decoder.AudioSampleRate > 0)
            {
                var format = new WaveFormat(_decoder.AudioSampleRate, 16, _decoder.AudioChannels);
                _waveProvider = new BufferedWaveProvider(format)
                {
                    BufferDuration = TimeSpan.FromSeconds(5),
                    DiscardOnBufferOverflow = true
                };

                var sampleProvider = _waveProvider.ToSampleProvider();
                // We ensure it is stereo (if mono, ToStereo() must be called, but assuming 2 channels as per current pipeline)
                if (sampleProvider.WaveFormat.Channels == 1) sampleProvider = sampleProvider.ToStereo();

                _audioEnhancer = new AudioEnhancerProvider(sampleProvider)
                {
                    IsEnhancerEnabled = BtnBass.Tag?.ToString() == "On"
                };

                _waveOut = new WaveOutEvent
                {
                    // 40ms is too aggressive for this decoder pipeline and can cause underruns
                    // that sound like crackling/static when packets arrive unevenly.
                    DesiredLatency = 160,
                    NumberOfBuffers = 3
                };
                _waveOut.Init(_audioEnhancer);
                if (SliderVolume != null)
                {
                    _waveOut.Volume = _isMuted ? 0 : (float)(SliderVolume.Value / 100.0);
                }
            }
        }

        private void Decoder_AudioDataAvailable(byte[] buffer, int length, double chunkEndPtsMs)
        {
            // Check both decoder state and our intent. This prevents data from being added
            // right after Pause() or before Resume() is fully processed (race with decoder thread).
            if (_decoder == null || _decoder.IsPaused || !_userWantsPlayback)
            {
                return;
            }
            Volatile.Write(ref _lastAudioTicks, DateTime.UtcNow.Ticks);
            if (_waveProvider != null && _waveProvider.BufferedDuration.TotalSeconds < 4.5)
            {
                _waveProvider.AddSamples(buffer, 0, length);
                int bytesPerSample = _decoder.AudioChannels * 2;
                if (bytesPerSample > 0 && _decoder.AudioSampleRate > 0)
                {
                    int sampleCount = length / bytesPerSample;
                    _decoder.NotifyAudioSamplesSubmitted(sampleCount, chunkEndPtsMs);
                }
            }
        }

        private static void PauseLog(string msg)
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "pause_debug.log");
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
            catch { }
        }

        private bool TryConsumePlayPauseToggle(string source)
        {
            long now = DateTime.UtcNow.Ticks;
            long last = Volatile.Read(ref _lastPlayPauseToggleUtcTicks);
            if (last > 0 && (now - last) / TimeSpan.TicksPerMillisecond < PlayPauseToggleDebounceMs)
            {
                PauseLog($"[DEBOUNCE] source={source} ignored");
                return false;
            }
            Volatile.Write(ref _lastPlayPauseToggleUtcTicks, now);
            return true;
        }

        private void UserPausePlayback(string source = "internal")
        {
            if (_decoder == null || !_decoder.IsRunning || _decoder.IsPaused)
            {
                return;
            }
            PauseLog($"[PAUSE] source={source}");
            _userWantsPlayback = false;
            _decoder.Pause();
            _waveOut?.Pause();

            // Do not clear the provider on a normal pause. For audio-only files, clearing the
            // buffer can make the decoder's EOF/drained-buffer check look like playback ended.
            // Seek/stop paths still clear the buffer explicitly.

            PauseUiClock();
            UpdatePlayPauseUI(false);
        }

        private void UserResumePlayback(string source = "internal")
        {
            if (_decoder == null || !_decoder.IsRunning || _decoder.IsPlaying)
            {
                return;
            }
            PauseLog($"[RESUME] source={source}");
            _userWantsPlayback = true;
            _decoder.Play();
            _waveOut?.Play();

            if (_decoder != null)
                StartUiClock(_decoder.GetCurrentTimeMs());

            UpdatePlayPauseUI(true);
        }

        private void TogglePlayPause(string source = "unknown")
        {
            if (!TryConsumePlayPauseToggle(source))
            {
                return;
            }

            if (_decoder == null || !_decoder.IsRunning)
            {
                if (!string.IsNullOrEmpty(_currentFilePath))
                {
                    double ratio = 0.0;
                    if (SliderTimeline != null && SliderTimeline.Maximum > 0)
                    {
                        ratio = SliderTimeline.Value / SliderTimeline.Maximum;
                    }
                    PlayFile(_currentFilePath, ratio);
                }
                return;
            }

            if (_decoder.IsPlaying)
            {
                UserPausePlayback(source);
            }
            else
            {
                UserResumePlayback(source);
            }
        }

        private void UpdatePlayPauseUI(bool isPlaying)
        {
            if (isPlaying && AudioUI != null && AudioUI.Visibility == Visibility.Visible)
            {
                if (!_notesTimer.IsEnabled) _notesTimer.Start();
            }
            else
            {
                if (_notesTimer.IsEnabled) _notesTimer.Stop();
                FadeOutAllNotes();
            }

            var geom = (Geometry)FindResource(isPlaying ? "PauseIcon" : "PlayIcon");
            if (PlayPauseIconPath != null)
            {
                PlayPauseIconPath.Data = geom;
                PlayPauseIconPath.Margin = isPlaying ? new Thickness(0) : new Thickness(2,0,0,0);
            }
            if (PlayPauseIconPathFS != null)
            {
                PlayPauseIconPathFS.Data = geom;
                PlayPauseIconPathFS.Margin = isPlaying ? new Thickness(0) : new Thickness(2,0,0,0);
            }
            if (PlayPauseIconPathPip != null)
            {
                PlayPauseIconPathPip.Data = geom;
                PlayPauseIconPathPip.Margin = isPlaying ? new Thickness(0) : new Thickness(2,0,0,0);
            }

            if (BtnPlayPause != null)
                BtnPlayPause.ToolTip = isPlaying ? "Pause (Space)" : "Play (Space)";
            if (BtnPlayPauseFS != null) BtnPlayPauseFS.ToolTip = isPlaying ? "Pause" : "Play";
            if (BtnPlayPausePip != null) BtnPlayPausePip.ToolTip = isPlaying ? "Pause" : "Play";
        }

        private void NotesTimer_Tick(object? sender, EventArgs e)
        {
            if (AudioNotesCanvas == null) return;
            bool audioUiVisible = AudioUI != null && AudioUI.Visibility == Visibility.Visible;
            if (AudioNotesCanvas.Visibility != Visibility.Visible && !audioUiVisible) return;
            if (_decoder == null || !_decoder.IsPlaying) return;

            var note = new TextBlock
            {
                Text = _musicNotes[_notesRandom.Next(_musicNotes.Length)],
                Foreground = new SolidColorBrush(Color.FromArgb((byte)_notesRandom.Next(100, 200), (byte)_notesRandom.Next(200, 255), (byte)_notesRandom.Next(200, 255), 255)),
                FontSize = _notesRandom.Next(24, 48),
                RenderTransformOrigin = new Point(0.5, 0.5),
                Opacity = 0
            };

            var transform = new TransformGroup();
            var translate = new TranslateTransform();
            var rotate = new RotateTransform();
            transform.Children.Add(translate);
            transform.Children.Add(rotate);
            note.RenderTransform = transform;

            AudioNotesCanvas.Children.Add(note);

            double startX = AudioUI.ActualWidth / 2 + _notesRandom.Next(-300, 300);
            double startY = AudioUI.ActualHeight / 2 + 100 + _notesRandom.Next(-50, 50);

            Canvas.SetLeft(note, startX);
            Canvas.SetTop(note, startY);

            var duration = TimeSpan.FromSeconds(_notesRandom.Next(4, 7));

            var opacityAnim = new DoubleAnimationUsingKeyFrames();
            opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.8, KeyTime.FromPercent(0.2)));
            opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.8, KeyTime.FromPercent(0.6)));
            opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1.0)));
            opacityAnim.Duration = duration;

            var moveUpAnim = new DoubleAnimation(startY, startY - _notesRandom.Next(150, 300), new Duration(duration));
            var swayAnim = new DoubleAnimation(-20, 20, new Duration(TimeSpan.FromSeconds(1.5))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            var rotateAnim = new DoubleAnimation(-15, 15, new Duration(TimeSpan.FromSeconds(2))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };

            note.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
            note.BeginAnimation(Canvas.TopProperty, moveUpAnim);
            translate.BeginAnimation(TranslateTransform.XProperty, swayAnim);
            rotate.BeginAnimation(RotateTransform.AngleProperty, rotateAnim);

            var removeTimer = new DispatcherTimer { Interval = duration };
            removeTimer.Tick += (s, ev) =>
            {
                removeTimer.Stop();
                AudioNotesCanvas.Children.Remove(note);
            };
            removeTimer.Start();
        }

        private void FadeOutAllNotes()
        {
            if (AudioNotesCanvas == null) return;
            foreach (UIElement child in AudioNotesCanvas.Children)
            {
                var currentOpacity = child.Opacity;
                var fadeOut = new DoubleAnimation(currentOpacity, 0, new Duration(TimeSpan.FromSeconds(1)));
                child.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
        }
    }
}