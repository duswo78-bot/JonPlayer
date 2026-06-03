using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Text;
using Microsoft.Win32;
using System.Windows.Shell;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using NAudio.Wave;
using System.Diagnostics;
using System.Windows.Media.Animation;

// ── Resolve WPF vs WinForms ambiguities ──────────────────
using Size           = System.Windows.Size;
using Point          = System.Windows.Point;
using Color          = System.Windows.Media.Color;
using WpfBrush       = System.Windows.Media.Brush;
using WpfBrushes     = System.Windows.Media.Brushes;
using WpfButton      = System.Windows.Controls.Button;
using WpfTextBox     = System.Windows.Controls.TextBox;
using WpfComboBox    = System.Windows.Controls.ComboBox;
using WpfMessageBox  = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace JonPlayer
{
    // Converter: bool → Accent(green) or Transparent — used for now-playing bar indicator
    public class BoolToAccentBrushConverter : IValueConverter
    {
        public static readonly BoolToAccentBrushConverter Instance = new();
        private static readonly SolidColorBrush AccentBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
        private static readonly SolidColorBrush TransBrush  = new SolidColorBrush(Colors.Transparent);
        public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? AccentBrush : TransBrush;
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
    }

    // Converter: bool → bright white or dimmed white — used for now-playing text
    public class BoolToTextBrushConverter : IValueConverter
    {
        public static readonly BoolToTextBrushConverter Instance = new();
        private static readonly SolidColorBrush WhiteBrush = new SolidColorBrush(Colors.White);
        private static readonly SolidColorBrush MutedBrush = new SolidColorBrush(Color.FromArgb(0xBB, 0xFF, 0xFF, 0xFF));
        public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? WhiteBrush : MutedBrush;
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
    }

    public class SliderFillConverter : IMultiValueConverter
    {
        public static readonly SliderFillConverter Instance = new();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (values.Length == 3
                    && values[0] != DependencyProperty.UnsetValue
                    && values[1] != DependencyProperty.UnsetValue
                    && values[2] != DependencyProperty.UnsetValue)
                {
                    double val     = System.Convert.ToDouble(values[0]);
                    double maximum = System.Convert.ToDouble(values[1]);
                    double width   = System.Convert.ToDouble(values[2]);
                    if (maximum > 0)
                        return (val / maximum) * Math.Max(0, width - 12);
                }
            }
            catch { /* safe fallback */ }
            return 0d;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public partial class MainWindow : Window
    {
        private D3D11VideoRenderer? _renderer;
        private FFmpegMediaDecoder? _decoder;
        
        private WaveOutEvent? _waveOut;
        private BufferedWaveProvider? _waveProvider;

        private bool _isUserDraggingSlider;
        private bool _isUpdatingFromPlayer;
        private bool _isSeeking;

        private int   _lastVolume    = 80;
        private bool  _isMuted;
        private bool  _isLightTheme = false;
        private double _currentSpeed  = 1.0f;

        private bool        _isFullscreen;
        private WindowState _prevWindowState;
        private bool        _prevTopmost;
        private WindowStyle _prevWindowStyle;
        private ResizeMode  _prevResizeMode;

        private DispatcherTimer _fsMousePollTimer;
        private DispatcherTimer _statsTimer;
        private DispatcherTimer _toastTimer;

        // Stats Overlay
        private int _openCount = 0;
        private int _seekCount = 0;
        private Stopwatch _renderTimer = new Stopwatch();
        private double _totalRenderTimeMs = 0;
        private int _renderSamples = 0;
        private TimeSpan _lastCpuTime;
        private DateTime _lastCpuCheckTime;

        private static string? _lastOpenDirectory;

        private bool _isMouseOverFsStrip;
        private bool _isMouseOverFsExitBadge;


        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint uPeriod);

        private DispatcherTimer? _playlistTimer;
        private bool _isPlaylistHovered;

        private DispatcherTimer _cursorHideTimer;
        private DispatcherTimer _fsVolumeTimer;
        private DispatcherTimer _notesTimer;
        private Random _notesRandom = new Random();
        private string[] _musicNotes = { "♩", "♪", "♫", "♬", "♭", "♮", "♯" };

        private readonly string[] _idleVibes = new string[]
        {
            "Pick Your Vibe",
            "Bring on the Good Stuff",
            "Queue Up Some Magic",
            "Ready for Your Next Favorite",
            "The Show Starts With You"
        };
        private void SetRandomVibe()
        {
            if (TxtNowPlaying != null)
            {
                string newVibe;
                do
                {
                    newVibe = _idleVibes[Random.Shared.Next(_idleVibes.Length)];
                } while (newVibe == TxtNowPlaying.Text && _idleVibes.Length > 1);

                TxtNowPlaying.Text = newVibe;
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            SetRandomVibe();

            this.StateChanged += Window_StateChanged;
            this.MouseMove += Window_MouseMove;

            _fsMousePollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _fsMousePollTimer.Tick += FsMousePollTimer_Tick;

            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _statsTimer.Tick += StatsTimer_Tick;

            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _toastTimer.Tick += (s, e) => { ToastOverlay.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
            
            _cursorHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _cursorHideTimer.Tick += (s, e) =>
            {
                if (_isFullscreen) this.Cursor = System.Windows.Input.Cursors.None;
                _cursorHideTimer.Stop();
            };

            _fsVolumeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _fsVolumeTimer.Tick += (s, e) =>
            {
                OverlayFsVolume.Visibility = Visibility.Collapsed;
                _fsVolumeTimer.Stop();
            };

            _notesTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _notesTimer.Tick += NotesTimer_Tick;

            _lastCpuTime = Process.GetCurrentProcess().TotalProcessorTime;
            _lastCpuCheckTime = DateTime.UtcNow;

            ApplyTheme(false);

            timeBeginPeriod(1);

            FsBottomStrip.IsVisibleChanged += (s, e) =>
            {
                var anim = new System.Windows.Media.Animation.DoubleAnimation
                {
                    To = FsBottomStrip.IsVisible ? -100 : 0,
                    Duration = TimeSpan.FromSeconds(0.3),
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };
                FsSubtitleShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, anim);
            };

            Closing += (s, e) => {
                // Stop all timers first to prevent null reference after disposal
                _fsMousePollTimer.Stop();
                _statsTimer.Stop();
                _toastTimer.Stop();
                _cursorHideTimer.Stop();
                _fsVolumeTimer.Stop();
                _notesTimer.Stop();
                _playlistTimer?.Stop();

                CancelWhisperExtraction();
                if (_decoder != null)
                {
                    DetachDecoderEvents(_decoder);
                    _decoder.Stop();
                    _decoder.Dispose();
                    _decoder = null;
                }
                _waveOut?.Stop();
                _waveOut?.Dispose();
                _renderer?.Dispose();
                _renderer = null;
                timeEndPeriod(1);
            };
        }

        private void Decoder_FrameDecoded(IntPtr bgraData, int width, int height, int stride, bool isHardwareTexture)
        {
            _renderTimer.Restart();
            _renderer?.ResetSize(width, height);
            _renderer?.RenderFrame(bgraData, width, height, stride, isHardwareTexture);
            _renderTimer.Stop();

            _totalRenderTimeMs += _renderTimer.Elapsed.TotalMilliseconds;
            _renderSamples++;
        }

        private DateTime _lastSliderUpdate = DateTime.MinValue;

        private void Decoder_PositionChanged(double ratio)
        {
            if (_isUserDraggingSlider || _isSeeking) return;

            var now = DateTime.UtcNow;
            if ((now - _lastSliderUpdate).TotalMilliseconds < 33) return;
            _lastSliderUpdate = now;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isUserDraggingSlider || _isSeeking || _decoder == null || !_decoder.IsRunning) return;
                _isUpdatingFromPlayer = true;
                SliderTimeline.Value = ratio * SliderTimeline.Maximum;
                if (SliderTimelineFS != null) SliderTimelineFS.Value = SliderTimeline.Value;
                _isUpdatingFromPlayer = false;
            }));
        }

        private DateTime _lastTimeUpdate = DateTime.MinValue;

        private void Decoder_TimeUpdated(TimeSpan current, TimeSpan total)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastTimeUpdate).TotalMilliseconds < 100) return;
            _lastTimeUpdate = now;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isSeeking || _decoder == null || !_decoder.IsRunning) return;
                TxtCurrentTime.Text = current.ToString(@"hh\:mm\:ss");
                
                var remaining = total - current;
                if (remaining.TotalSeconds < 0) remaining = TimeSpan.Zero;
                TxtTotalTime.Text = "-" + remaining.ToString(@"hh\:mm\:ss");
                
                if (TxtCurrentTimeFS != null) TxtCurrentTimeFS.Text = TxtCurrentTime.Text;
                if (TxtTotalTimeFS != null) TxtTotalTimeFS.Text = TxtTotalTime.Text;

                // Subtitle Update
                if (_subtitlesEnabled && _subtitleManager.HasSubtitles)
                {
                    string subText = _subtitleManager.GetSubtitleText((int)current.TotalMilliseconds);
                    if (!string.IsNullOrEmpty(subText))
                    {
                        TxtSubtitle.Text = subText;
                        SubtitleBorder.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        SubtitleBorder.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    SubtitleBorder.Visibility = Visibility.Collapsed;
                }

                // Handle Next Video Overlay
                if (!_isShuffle && _playlist.Count > 1 && _playlistIndex >= 0)
                {
                    if (_playlistIndex < _playlist.Count - 1)
                    {
                        if (total.TotalSeconds > 0 && total - current <= TimeSpan.FromSeconds(5))
                        {
                            TxtNextVideoName.Text = _playlist[_playlistIndex + 1].Name;
                            NextVideoOverlay.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            NextVideoOverlay.Visibility = Visibility.Collapsed;
                        }
                    }
                    else if (_isRepeat && _playlistIndex == _playlist.Count - 1)
                    {
                        if (total.TotalSeconds > 0 && total - current <= TimeSpan.FromSeconds(5))
                        {
                            TxtNextVideoName.Text = _playlist[0].Name;
                            NextVideoOverlay.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            NextVideoOverlay.Visibility = Visibility.Collapsed;
                        }
                    }
                    else
                    {
                        NextVideoOverlay.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    if (NextVideoOverlay != null) NextVideoOverlay.Visibility = Visibility.Collapsed;
                }
            }));
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            if (_isChangingFullscreen) return;

            if (_isFullscreen && WindowState != WindowState.Maximized)
            {
                ExitFullscreen();
                return;
            }

            if (WindowState == WindowState.Maximized && !_isFullscreen)
                MainGrid.Margin = new Thickness(8); // 최대화 시 화면 밖으로 컨트롤 바가 잘리는 현상 방지
            else
                MainGrid.Margin = new Thickness(0);
        }

        private void MouseLayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleFullscreen();
                e.Handled = true;
                return;
            }

            if (e.ClickCount == 1 && _decoder != null)
            {
                TogglePlayPause();
                e.Handled = true;
            }
        }

        // 전체화면 마우스 위치 추적 및 팝업 표시
        private void FsMousePollTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isFullscreen) return;

            try 
            {
                Point pos = Mouse.GetPosition(VideoGrid);
                double w = VideoGrid.ActualWidth;
                double h = VideoGrid.ActualHeight;

                if (pos.X >= 0 && pos.X <= w && pos.Y >= 0 && pos.Y <= h)
                {
                    if (pos.Y < 80)
                    {
                        ShowFsExitBadge();
                    }
                    else if (!_isMouseOverFsExitBadge)
                    {
                        HideFsExitBadge();
                    }

                    if (pos.Y > h - 130)
                    {
                        ShowFsBottomStrip();
                    }
                    else if (!_isMouseOverFsStrip)
                    {
                        FsBottomStrip.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    if (!_isMouseOverFsExitBadge) HideFsExitBadge();
                    if (!_isMouseOverFsStrip) FsBottomStrip.Visibility = Visibility.Collapsed;
                }
            }
            catch { /* safe fallback */ }
        }

        private void ShowFsExitBadge()
        {
            if (_isFullscreen) PopupFsExit.IsOpen = true;
        }

        private void HideFsExitBadge()
        {
            PopupFsExit.IsOpen = false;
        }

        private void ShowFsBottomStrip()
        {
            if (_isFullscreen)
            {
                FsBottomStrip.Visibility = Visibility.Visible;
            }
        }

        private void RestartFsHideTimer()
        {
            // Auto hide timer is handled by mouse position polling
        }

        private void FsExitBadge_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => _isMouseOverFsExitBadge = true;
        private void FsExitBadge_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _isMouseOverFsExitBadge = false;
            if (_isFullscreen) HideFsExitBadge();
        }

        private void FsBottomStrip_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _isMouseOverFsStrip = true;
        }

        private void FsBottomStrip_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _isMouseOverFsStrip = false;
            if (_isFullscreen) FsBottomStrip.Visibility = Visibility.Collapsed;
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void BtnMaximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void ToggleMaximize()
        {
            if (_isFullscreen) return;
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                BtnMaximize.Content = "⬜";
                BtnMaximize.ToolTip = "Maximize";
            }
            else
            {
                WindowState = WindowState.Maximized;
                BtnMaximize.Content = "❐";
                BtnMaximize.ToolTip = "Restore";
            }
            RowTitleBar.Height = new GridLength(40);
            RowTimeline.Height = GridLength.Auto;
            RowControls.Height = GridLength.Auto;
        }

        private void BtnTheme_Click(object sender, RoutedEventArgs e)
        {
            _isLightTheme = !_isLightTheme;
            ApplyTheme(_isLightTheme);
        }

        private void ApplyTheme(bool light)
        {
            if (light)
            {
                SetBrush("BgBrush", 0xF3, 0xF3, 0xF7);
                SetBrush("PanelBrush", 0xFF, 0xFF, 0xFF);
                SetBrush("TextBrush", 0x00, 0x00, 0x00);
                SetBrush("TextMutedBrush", 0x44, 0x44, 0x44);
                SetBrush("AccentBrush", 0x00, 0x7A, 0xFF);
                SetBrush("HoverBrush", 0xE5, 0xE5, 0xEA);
                SetBrush("ActiveBrush", 0xD1, 0xD1, 0xD6);
                SetBrush("DividerBrush", 0xD8, 0xD8, 0xDC);

                SetBrush("ToggleOnBrush", 0x00, 0x50, 0xC0); // 진한 파란색으로 가독성 개선
                SetColor("ToggleOnGlowColor", 0x00, 0x50, 0xC0);

                SetBrushAlpha("PlaylistBgBrush", 0x99, 0xFF, 0xFF, 0xFF);
                SetBrushAlpha("PlaylistBorderBrush", 0x66, 0xAA, 0xAA, 0xAA);
                SetBrushAlpha("PlaylistTopGlossBrush", 0x44, 0xFF, 0xFF, 0xFF);
                SetBrushAlpha("KeyBadgeBrush", 0x33, 0x00, 0x00, 0x00);

                var knob = MakeKnob(0xF2, 0xF2, 0xF7, 0xE5, 0xE5, 0xEA, 0xC7, 0xC7, 0xCC, 0xAE, 0xAE, 0xB2);
                this.Resources["KnobBgBrush"] = knob;

                BtnTheme.ToolTip = "Dark Mode로 전환";
                if (BtnThemeFS != null) BtnThemeFS.ToolTip = "Dark Mode로 전환";

                var sunGeom = (Geometry)FindResource("SunIcon");
                if (ThemeIconPath != null) ThemeIconPath.Data = sunGeom;
                if (ThemeIconPathFS != null) ThemeIconPathFS.Data = sunGeom;
            }
            else
            {
                SetBrush("BgBrush", 0x11, 0x11, 0x11);
                SetBrush("PanelBrush", 0x1A, 0x1A, 0x1A);
                SetBrush("TextBrush", 0xFF, 0xFF, 0xFF);
                SetBrush("TextMutedBrush", 0x88, 0x88, 0x88);
                SetBrush("AccentBrush", 0x1D, 0xB9, 0x54);
                SetBrush("HoverBrush", 0x2A, 0x2A, 0x2A);
                SetBrush("ActiveBrush", 0x38, 0x38, 0x38);
                SetBrush("DividerBrush", 0x2C, 0x2C, 0x2C);

                SetBrush("ToggleOnBrush", 0x00, 0xE5, 0xFF); // 기존 네온 시안색
                SetColor("ToggleOnGlowColor", 0x00, 0xE5, 0xFF);

                SetBrushAlpha("PlaylistBgBrush", 0x99, 0x0A, 0x0A, 0x0A);
                SetBrushAlpha("PlaylistBorderBrush", 0x40, 0xFF, 0xFF, 0xFF);
                SetBrushAlpha("PlaylistTopGlossBrush", 0x18, 0xFF, 0xFF, 0xFF);
                SetBrushAlpha("KeyBadgeBrush", 0x33, 0xFF, 0xFF, 0xFF);

                var knob = MakeKnob(0x4E, 0x4E, 0x52, 0x24, 0x24, 0x26, 0x5E, 0x5E, 0x62, 0x2C, 0x2C, 0x2F);
                this.Resources["KnobBgBrush"] = knob;

                BtnTheme.ToolTip = "Light Mode로 전환";
                if (BtnThemeFS != null) BtnThemeFS.ToolTip = "Light Mode로 전환";

                var moonGeom = (Geometry)FindResource("MoonIcon");
                if (ThemeIconPath != null) ThemeIconPath.Data = moonGeom;
                if (ThemeIconPathFS != null) ThemeIconPathFS.Data = moonGeom;
            }
        }

        private void SetBrush(string key, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            this.Resources[key] = brush;
        }

        private void SetColor(string key, byte r, byte g, byte b)
        {
            this.Resources[key] = Color.FromRgb(r, g, b);
        }

        private void SetBrushAlpha(string key, byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            this.Resources[key] = brush;
        }

        private static LinearGradientBrush MakeKnob(byte r0, byte g0, byte b0, byte r1, byte g1, byte b1, byte r2, byte g2, byte b2, byte r3, byte g3, byte b3)
        {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(r0, g0, b0), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(r1, g1, b1), 0.4));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(r2, g2, b2), 0.8));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(r3, g3, b3), 1.0));
            return brush;
        }

        private void BtnOpen_Click(object sender, RoutedEventArgs e) => OpenFile();

        public class PlaylistItem : System.ComponentModel.INotifyPropertyChanged
        {
            public string Name { get; set; } = string.Empty;
            public string Path { get; set; } = string.Empty;

            private bool _isCurrentlyPlaying;
            public bool IsCurrentlyPlaying
            {
                get => _isCurrentlyPlaying;
                set
                {
                    if (_isCurrentlyPlaying != value)
                    {
                        _isCurrentlyPlaying = value;
                        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsCurrentlyPlaying)));
                    }
                }
            }

            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        }

        // Drag state for playlist overlay
        private bool   _isPlaylistDragging;
        private Point  _playlistDragStart;
        private double _playlistDragOriginX;
        private double _playlistDragOriginY;

        private bool   _isShortcutsDragging;
        private Point  _shortcutsDragStart;
        private double _shortcutsDragOriginX;
        private double _shortcutsDragOriginY;

        private System.Collections.ObjectModel.ObservableCollection<PlaylistItem> _playlist = new System.Collections.ObjectModel.ObservableCollection<PlaylistItem>();
        private int _playlistIndex = -1;

        private void OpenFile()
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Open Media File",
                Filter = "Supported Media Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.ts;*.m2ts;*.mp3;*.flac;*.wav;*.aac;*.ogg;*.m4a|Video Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.ts;*.m2ts|Audio Files|*.mp3;*.flac;*.wav;*.aac;*.ogg;*.m4a|All Files (*.*)|*.*",
                Multiselect = true
            };
            
            if (!string.IsNullOrEmpty(_lastOpenDirectory))
                dlg.InitialDirectory = _lastOpenDirectory;
            else
                dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            if (dlg.ShowDialog() == true)
            {
                _lastOpenDirectory = Path.GetDirectoryName(dlg.FileName);
                LoadPlaylist(dlg.FileNames);
            }
        }

        private void OpenFolder()
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select a folder containing media files";
                dialog.UseDescriptionForTitle = true;
                
                if (!string.IsNullOrEmpty(_lastOpenDirectory))
                    dialog.SelectedPath = _lastOpenDirectory;

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    _lastOpenDirectory = dialog.SelectedPath;
                    var extensions = new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".ts", ".m2ts", ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a" };
                    var files = Directory.GetFiles(dialog.SelectedPath, "*.*", SearchOption.AllDirectories)
                        .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        .ToArray();

                    if (files.Length > 0)
                        LoadPlaylist(files);
                }
            }
        }

        private void UpdateNowPlayingHighlight()
        {
            for (int i = 0; i < _playlist.Count; i++)
                _playlist[i].IsCurrentlyPlaying = (i == _playlistIndex);

            // Update count badge
            if (TxtPlaylistCount != null)
                TxtPlaylistCount.Text = _playlist.Count.ToString();

            // Scroll to current item
            if (_playlistIndex >= 0 && _playlistIndex < _playlist.Count)
                ListPlaylist.ScrollIntoView(_playlist[_playlistIndex]);
        }

        private void LoadPlaylist(string[] files)
        {
            _playlist.Clear();
            _playedIndices.Clear();
            
            var allowedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm",
                ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a"
            };

            foreach (var f in files) 
            {
                string ext = System.IO.Path.GetExtension(f);
                if (allowedExts.Contains(ext))
                {
                    _playlist.Add(new PlaylistItem { Name = System.IO.Path.GetFileName(f), Path = f });
                }
            }

            ListPlaylist.ItemsSource = _playlist;

            if (_playlist.Count > 0)
            {
                _playlistIndex = 0;
                PlayFile(_playlist[_playlistIndex].Path);
                UpdateNowPlayingHighlight();
                
                if (_playlist.Count > 1)
                {
                    BtnPlaylistToggle.IsEnabled = true;
                    BtnPlaylistToggleFS.IsEnabled = true;
                    ShowPlaylistBriefly();
                }
            }

            if (TxtPlaylistCount != null)
                TxtPlaylistCount.Text = _playlist.Count.ToString();
        }

        private void ListPlaylist_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ListPlaylist.SelectedItem is PlaylistItem item)
            {
                _playlistIndex = _playlist.IndexOf(item);
                _playedIndices.Clear();
                PlayFile(item.Path);
                UpdateNowPlayingHighlight();
            }
        }

        private void ListPlaylist_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Delete && ListPlaylist.SelectedItem is PlaylistItem item)
            {
                int idx = _playlist.IndexOf(item);
                if (idx != -1)
                {
                    _playlist.RemoveAt(idx);
                    if (idx == _playlistIndex)
                    {
                        if (_playlist.Count > 0)
                        {
                            _playlistIndex = idx % _playlist.Count;
                            PlayFile(_playlist[_playlistIndex].Path);
                        }
                        else
                        {
                            _playlistIndex = -1;
                            CloseFile();
                        }
                    }
                    else if (idx < _playlistIndex)
                    {
                        _playlistIndex--;
                    }
                }
                if (_playlist.Count <= 1)
                {
                    BtnPlaylistToggle.IsEnabled = false;
                    BtnPlaylistToggleFS.IsEnabled = false;
                    PlaylistOverlay.Visibility = Visibility.Collapsed;
                }
                e.Handled = true;
            }
        }

        private void BtnPlaylistToggle_Click(object sender, RoutedEventArgs e)
        {
            TogglePlaylist();
        }

        private void TogglePlaylist()
        {
            if (PlaylistOverlay.Visibility == Visibility.Visible)
            {
                PlaylistOverlay.Visibility = Visibility.Collapsed;
            }
            else
            {
                PlaylistOverlay.Visibility = Visibility.Visible;
                StartPlaylistHideTimer();
            }
        }

        private void PlaylistOverlay_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _isPlaylistHovered = true;
            _playlistTimer?.Stop();
        }

        private void PlaylistOverlay_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _isPlaylistHovered = false;
            StartPlaylistHideTimer();
        }

        private void ShowPlaylistBriefly()
        {
            PlaylistOverlay.Visibility = Visibility.Visible;
            StartPlaylistHideTimer();
        }

        private void StartPlaylistHideTimer()
        {
            _playlistTimer?.Stop();
            _playlistTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _playlistTimer.Tick += (s, e) =>
            {
                if (!_isPlaylistHovered && !PlaylistOverlay.IsKeyboardFocusWithin)
                {
                    PlaylistOverlay.Visibility = Visibility.Collapsed;
                }
                _playlistTimer.Stop();
            };
            _playlistTimer.Start();
        }

        private void BtnPlaylistAdd_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "Supported Media Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.ts;*.m2ts;*.mp3;*.flac;*.wav;*.aac;*.ogg;*.m4a|Video Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.ts;*.m2ts|Audio Files|*.mp3;*.flac;*.wav;*.aac;*.ogg;*.m4a|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                foreach (var f in dlg.FileNames)
                {
                    _playlist.Add(new PlaylistItem { Name = Path.GetFileName(f), Path = f });
                }
                BtnPlaylistToggle.IsEnabled = _playlist.Count > 1;
                BtnPlaylistToggleFS.IsEnabled = _playlist.Count > 1;
                UpdateNowPlayingHighlight();
            }
        }

        private void BtnPlaylistClose_Click(object sender, RoutedEventArgs e)
        {
            PlaylistOverlay.Visibility = Visibility.Collapsed;
        }

        private void BtnShortcutsClose_Click(object sender, RoutedEventArgs e)
        {
            ShortcutsOverlay.Visibility = Visibility.Collapsed;
        }

        // ── Shortcuts drag-to-move ──────────────────────────────────────────
        private void ShortcutsOverlay_DragStart(object sender, MouseButtonEventArgs e)
        {
            _isShortcutsDragging = true;
            _shortcutsDragStart  = e.GetPosition(VideoGrid);
            var tx = ShortcutsTranslate;
            _shortcutsDragOriginX = tx.X;
            _shortcutsDragOriginY = tx.Y;
            (sender as UIElement)?.CaptureMouse();
            e.Handled = true;
        }

        private void ShortcutsOverlay_DragMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isShortcutsDragging) return;
            var current = e.GetPosition(VideoGrid);
            var tx = ShortcutsTranslate;

            double newX = _shortcutsDragOriginX + (current.X - _shortcutsDragStart.X);
            double newY = _shortcutsDragOriginY + (current.Y - _shortcutsDragStart.Y);
            
            tx.X = newX;
            tx.Y = newY;
        }

        private void ShortcutsOverlay_DragEnd(object sender, MouseButtonEventArgs e)
        {
            if (!_isShortcutsDragging) return;
            _isShortcutsDragging = false;
            (sender as UIElement)?.ReleaseMouseCapture();
            e.Handled = true;
        }

        // ── Playlist drag-to-move ──────────────────────────────────────────
        private void PlaylistOverlay_DragStart(object sender, MouseButtonEventArgs e)
        {
            _isPlaylistDragging = true;
            _playlistDragStart  = e.GetPosition(VideoGrid);
            var tx = PlaylistTranslate;
            _playlistDragOriginX = tx.X;
            _playlistDragOriginY = tx.Y;
            (sender as UIElement)?.CaptureMouse();
            e.Handled = true;
        }

        private void PlaylistOverlay_DragMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isPlaylistDragging) return;
            var current = e.GetPosition(VideoGrid);
            var tx = PlaylistTranslate;

            double newX = _playlistDragOriginX + (current.X - _playlistDragStart.X);
            double newY = _playlistDragOriginY + (current.Y - _playlistDragStart.Y);

            double minX = -(VideoGrid.ActualWidth - PlaylistOverlay.Margin.Right - PlaylistOverlay.ActualWidth);
            double maxX = PlaylistOverlay.Margin.Right;
            if (minX > maxX) minX = maxX;

            double minY = -(VideoGrid.ActualHeight - PlaylistOverlay.Margin.Bottom - PlaylistOverlay.ActualHeight);
            double maxY = PlaylistOverlay.Margin.Bottom;
            if (minY > maxY) minY = maxY;

            tx.X = Math.Max(minX, Math.Min(newX, maxX));
            tx.Y = Math.Max(minY, Math.Min(newY, maxY));

            e.Handled = true;
        }

        private void PlaylistOverlay_DragEnd(object sender, MouseButtonEventArgs e)
        {
            _isPlaylistDragging = false;
            (sender as UIElement)?.ReleaseMouseCapture();
            e.Handled = true;
        }



        private void NextVideoOverlay_Click(object sender, RoutedEventArgs e)
        {
            NextVideoOverlay.Visibility = Visibility.Collapsed;
            if (_playlist.Count > 1 && _playlistIndex >= 0 && _playlistIndex < _playlist.Count - 1)
            {
                _playlistIndex++;
                PlayFile(_playlist[_playlistIndex].Path);
                UpdateNowPlayingHighlight();
            }
        }

        private void BtnMoveUp_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as WpfButton;
            var item = btn?.DataContext as PlaylistItem;
            if (item != null)
            {
                int index = _playlist.IndexOf(item);
                if (index > 0)
                {
                    var currentlyPlaying = _playlistIndex >= 0 && _playlistIndex < _playlist.Count ? _playlist[_playlistIndex] : null;
                    _playlist.Move(index, index - 1);
                    if (currentlyPlaying != null) _playlistIndex = _playlist.IndexOf(currentlyPlaying);
                }
            }
        }

        private void BtnMoveDown_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as WpfButton;
            var item = btn?.DataContext as PlaylistItem;
            if (item != null)
            {
                int index = _playlist.IndexOf(item);
                if (index >= 0 && index < _playlist.Count - 1)
                {
                    var currentlyPlaying = _playlistIndex >= 0 && _playlistIndex < _playlist.Count ? _playlist[_playlistIndex] : null;
                    _playlist.Move(index, index + 1);
                    if (currentlyPlaying != null) _playlistIndex = _playlist.IndexOf(currentlyPlaying);
                }
            }
        }

        private void UpdatePlaylistIndexAfterReorder()
        {
        }

        private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            do
            {
                if (current is T ancestor)
                {
                    return ancestor;
                }
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            while (current != null);
            return null;
        }


        private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) e.Effects = System.Windows.DragDropEffects.Copy;
            else e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (this.Cursor != System.Windows.Input.Cursors.Arrow) this.Cursor = System.Windows.Input.Cursors.Arrow;
            _cursorHideTimer.Stop();
            _cursorHideTimer.Start();
        }

        private void Window_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
            var files = e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            LoadPlaylist(files);
        }

        private string? _currentFilePath;

        private bool _isOpeningFile = false;

        private void PlayPrev()
        {
            if (_isOpeningFile) return;
            if (_playlist.Count > 0)
            {
                if (_isShuffle)
                {
                    if (_playlist.Count > 1)
                    {
                        int nextIndex;
                        do {
                            nextIndex = Random.Shared.Next(_playlist.Count);
                        } while (nextIndex == _playlistIndex);
                        _playlistIndex = nextIndex;
                    }
                    PlayFile(_playlist[_playlistIndex].Path);
                    UpdateNowPlayingHighlight();
                }
                else if (_playlistIndex > 0)
                {
                    _playlistIndex--;
                    PlayFile(_playlist[_playlistIndex].Path);
                    UpdateNowPlayingHighlight();
                }
                else if (_isRepeat)
                {
                    _playlistIndex = _playlist.Count - 1;
                    PlayFile(_playlist[_playlistIndex].Path);
                    UpdateNowPlayingHighlight();
                }
            }
        }

        private void BtnPrev_Click(object sender, RoutedEventArgs e) => PlayPrev();
        private void BtnNext_Click(object sender, RoutedEventArgs e) => PlayNext();

        private bool PlayNext()
        {
            if (_isOpeningFile) return false;
            if (_playlist.Count > 0)
            {
                if (_isShuffle)
                {
                    _playedIndices.Add(_playlistIndex);

                    if (_playedIndices.Count >= _playlist.Count)
                    {
                        if (!_isRepeat) return false;
                        _playedIndices.Clear();
                        _playedIndices.Add(_playlistIndex);
                    }

                    if (_playlist.Count > 1)
                    {
                        int nextIndex;
                        do {
                            nextIndex = Random.Shared.Next(_playlist.Count);
                        } while (nextIndex == _playlistIndex || _playedIndices.Contains(nextIndex));
                        _playlistIndex = nextIndex;
                    }
                    PlayFile(_playlist[_playlistIndex].Path);
                    UpdateNowPlayingHighlight();
                    return true;
                }
                else if (_playlistIndex < _playlist.Count - 1)
                {
                    _playlistIndex++;
                    PlayFile(_playlist[_playlistIndex].Path);
                    UpdateNowPlayingHighlight();
                    return true;
                }
                else if (_isRepeat)
                {
                    _playlistIndex = 0;
                    PlayFile(_playlist[_playlistIndex].Path);
                    UpdateNowPlayingHighlight();
                    return true;
                }
            }
            return false;
        }

        private void Decoder_PlaybackFinished()
        {
            // Use BeginInvoke (async) instead of Invoke (sync) to prevent deadlock
            // when decoder thread waits for UI and UI waits for decoder.Stop()/Join()
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdatePlayPauseUI(false);
                if (!PlayNext())
                {
                    if (VideoViewbox != null) VideoViewbox.Visibility = Visibility.Collapsed;
                    if (AudioUI != null) AudioUI.Visibility = Visibility.Collapsed;
                    if (ImgSplash != null) ImgSplash.Visibility = Visibility.Visible;
                    
                    _isUpdatingFromPlayer = true;
                    if (SliderTimeline != null) 
                    {
                        SliderTimeline.Value = 0;
                        SliderTimeline.IsEnabled = false;
                        SliderTimeline.IsHitTestVisible = false;
                    }
                    if (SliderTimelineFS != null) 
                    {
                        SliderTimelineFS.Value = 0;
                        SliderTimelineFS.IsEnabled = false;
                        SliderTimelineFS.IsHitTestVisible = false;
                    }
                    _isUpdatingFromPlayer = false;
                    
                    if (TxtCurrentTime != null) TxtCurrentTime.Text = "00:00:00";
                    if (TxtTotalTime != null) TxtTotalTime.Text = "00:00:00";
                    if (TxtCurrentTimeFS != null) TxtCurrentTimeFS.Text = "00:00:00";
                    if (TxtTotalTimeFS != null) TxtTotalTimeFS.Text = "00:00:00";
                    
                    SetRandomVibe();
                    Title = "JonPlayer";

                    if (_decoder != null)
                    {
                        var oldDec = _decoder;
                        _decoder = null;
                        DetachDecoderEvents(oldDec);
                        Task.Run(() => { oldDec.Stop(); oldDec.Dispose(); });
                    }
                }
            }));
        }

        private void DetachDecoderEvents(FFmpegMediaDecoder decoder)
        {
            if (decoder == null) return;
            decoder.PlaybackFinished -= Decoder_PlaybackFinished;
            decoder.FrameDecoded -= Decoder_FrameDecoded;
            decoder.AudioDataAvailable -= Decoder_AudioDataAvailable;
            decoder.PositionChanged -= Decoder_PositionChanged;
            decoder.TimeUpdated -= Decoder_TimeUpdated;
            decoder.RotationDetected -= Decoder_RotationDetected;
            decoder.SeekInitiated -= Decoder_SeekInitiated;
            decoder.SeekPerformed -= Decoder_SeekPerformed;
        }

        private async void PlayFile(string path, double startRatio = 0.0)
        {
            if (_isOpeningFile) return;
            _isOpeningFile = true;

            FFmpegMediaDecoder? newDecoder = null;
            try
            {
                _currentFilePath = path;
                _openCount++;

                TxtNowPlaying.Text = "Loading...";

                var oldDecoder = _decoder;
                _decoder = null;
                
                if (oldDecoder != null)
                {
                    DetachDecoderEvents(oldDecoder);
                    
                    await Task.Run(() => 
                    {
                        oldDecoder.Stop();
                        oldDecoder.Dispose();
                    });
                }

                if (_renderer == null)
                {
                    _renderer = new D3D11VideoRenderer();
                    if (VideoElement != null) VideoElement.Source = _renderer.D3DImage;
                }
                if (_renderer != null)
                {
                    if (VideoElement != null && VideoElement.Source != _renderer.D3DImage)
                        VideoElement.Source = _renderer.D3DImage;
                }

                newDecoder = new FFmpegMediaDecoder();
                newDecoder.SetSpeed(_currentSpeed);
                if (_renderer != null)
                {
                    newDecoder.SetD3D11Device(_renderer.D3D11DevicePtr, _renderer.D3D11ContextPtr);
                }
                newDecoder.FrameDecoded += Decoder_FrameDecoded;
                newDecoder.AudioDataAvailable += Decoder_AudioDataAvailable;
                newDecoder.PositionChanged += Decoder_PositionChanged;
                newDecoder.TimeUpdated += Decoder_TimeUpdated;
                newDecoder.PlaybackFinished += Decoder_PlaybackFinished;
                newDecoder.RotationDetected += Decoder_RotationDetected;
                newDecoder.SeekInitiated += Decoder_SeekInitiated;
                newDecoder.SeekPerformed += Decoder_SeekPerformed;
                newDecoder.GetAudioBufferedDurationMs = () => _waveProvider?.BufferedDuration.TotalMilliseconds ?? 0;

                Dispatcher.Invoke(() => {
                    if (VideoRotation != null) VideoRotation.Angle = 0;
                });

                await Task.Run(() => newDecoder.Open(path));
                
                _decoder = newDecoder;
                InitAudioPlayer();
                
                Dispatcher.Invoke(() => {
                    if (VideoElement != null && _decoder != null)
                    {
                        VideoElement.Width = _decoder.Width > 0 ? _decoder.Width : 1920;
                        VideoElement.Height = _decoder.Height > 0 ? _decoder.Height : 1080;
                    }
                });
                
                _decoder.Play();
                _waveOut?.Play();
                
                if (startRatio > 0.0)
                {
                    _decoder.Seek(startRatio);
                }

                Dispatcher.Invoke(() => {
                    _isSeeking = false;
                    if (SliderTimeline != null) 
                    {
                        SliderTimeline.IsEnabled = true;
                        SliderTimeline.IsHitTestVisible = true;
                    }
                    if (SliderTimelineFS != null) 
                    {
                        SliderTimelineFS.IsEnabled = true;
                        SliderTimelineFS.IsHitTestVisible = true;
                    }

                    // Auto load subtitle if exists
                    CancelWhisperExtraction();
                    if (WhisperLoadingOverlay != null) WhisperLoadingOverlay.Visibility = Visibility.Collapsed;
                    if (TxtWhisperProgress != null) TxtWhisperProgress.Text = "";
                    if (TxtWhisperProgressFS != null) TxtWhisperProgressFS.Text = "";

                    _subtitleManager.Clear();
                    _subtitlesEnabled = false;
                    SubtitleBorder.Visibility = Visibility.Collapsed;
                    if (BtnWhisper != null) BtnWhisper.Tag = null;
                    if (BtnWhisperFS != null) BtnWhisperFS.Tag = null;

                    string[] subExts = { ".srt", ".smi", ".vtt" };
                    string baseDir = Path.GetDirectoryName(path) ?? "";
                    string baseName = Path.GetFileNameWithoutExtension(path);
                    
                    foreach (var ext in subExts)
                    {
                        string subPath = Path.Combine(baseDir, baseName + ext);
                        if (File.Exists(subPath))
                        {
                            if (_subtitleManager.LoadSubtitle(subPath))
                            {
                                _subtitlesEnabled = true;
                                SubtitleBorder.Visibility = Visibility.Visible;
                                
                                if (BtnWhisper != null) BtnWhisper.Tag = "On";
                                if (BtnWhisperFS != null) BtnWhisperFS.Tag = "On";

                                // 3초 블링킹 애니메이션 (Opactiy 0.2 <-> 1.0)
                                var blinkAnim = new System.Windows.Media.Animation.DoubleAnimation
                                {
                                    From = 1.0,
                                    To = 0.2,
                                    Duration = new Duration(TimeSpan.FromSeconds(0.5)),
                                    AutoReverse = true,
                                    RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(3)
                                };
                                if (BtnWhisper != null) BtnWhisper.BeginAnimation(UIElement.OpacityProperty, blinkAnim);
                                if (BtnWhisperFS != null) BtnWhisperFS.BeginAnimation(UIElement.OpacityProperty, blinkAnim);

                                break;
                            }
                        }
                    }
                });

                var name = Path.GetFileName(path);
                TxtNowPlaying.Text = name;
                if (_playlist.Count > 1)
                {
                    Title = $"JonPlayer — ({_playlistIndex + 1}/{_playlist.Count}) {name}";
                }
                else
                {
                    Title = $"JonPlayer — {name}";
                }

                if (_decoder.HasVideo)
                {
                    if (VideoViewbox != null) VideoViewbox.Visibility = Visibility.Visible;
                    if (ImgSplash != null) ImgSplash.Visibility = Visibility.Collapsed;
                    if (AudioUI != null) AudioUI.Visibility = Visibility.Collapsed;
                }
                else
                {
                    if (VideoViewbox != null) VideoViewbox.Visibility = Visibility.Collapsed;
                    if (ImgSplash != null) ImgSplash.Visibility = Visibility.Collapsed;
                    if (AudioUI != null) AudioUI.Visibility = Visibility.Visible;

                    try
                    {
                        var tfile = TagLib.File.Create(path);
                        TxtAudioTitle.Text = string.IsNullOrWhiteSpace(tfile.Tag.Title) ? name : tfile.Tag.Title;
                        
                        string artist = string.Join(", ", tfile.Tag.Performers);
                        TxtAudioArtist.Text = string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist;
                        
                        TxtAudioAlbum.Text = string.IsNullOrWhiteSpace(tfile.Tag.Album) ? "Unknown Album" : tfile.Tag.Album;

                        if (tfile.Tag.Pictures.Length > 0)
                        {
                            var pic = tfile.Tag.Pictures[0];
                            var ms = new System.IO.MemoryStream(pic.Data.Data);
                            var bi = new System.Windows.Media.Imaging.BitmapImage();
                            bi.BeginInit();
                            bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            bi.StreamSource = ms;
                            bi.EndInit();
                            bi.Freeze();

                            AudioCoverBackground.Source = bi;
                            AudioCoverForeground.Source = bi;
                        }
                        else
                        {
                            var fallback = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/logo.png"));
                            AudioCoverBackground.Source = fallback;
                            AudioCoverForeground.Source = fallback;
                        }
                    }
                    catch
                    {
                        TxtAudioTitle.Text = name;
                        TxtAudioArtist.Text = "Unknown Artist";
                        TxtAudioAlbum.Text = "Unknown Album";
                        var fallback = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/logo.png"));
                        AudioCoverBackground.Source = fallback;
                        AudioCoverForeground.Source = fallback;
                    }
                }

                UpdatePlayPauseUI(true);
            }
            catch (Exception ex)
            {
                newDecoder?.Dispose();
                WpfMessageBox.Show($"파일을 열 수 없습니다.\n{ex.Message}", "JonPlayer", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _isOpeningFile = false;
            }
        }

        private void BtnPlayPause_Click   (object sender, RoutedEventArgs e) => TogglePlayPause();
        private async void BtnStop_Click        (object sender, RoutedEventArgs e)
        {
            var oldDecoder = _decoder;
            _decoder = null;
            if (oldDecoder != null)
            {
                DetachDecoderEvents(oldDecoder);
                
                await Task.Run(() => 
                {
                    oldDecoder.Stop();
                    oldDecoder.Dispose();
                });
            }
            
            _waveOut?.Stop();
            // _waveProvider?.ClearBuffer(); // Cleared in SeekPerformed, and provider is destroyed on next PlayFile
            UpdatePlayPauseUI(false);
            if (VideoViewbox != null) VideoViewbox.Visibility = Visibility.Collapsed;
            if (AudioUI != null) AudioUI.Visibility = Visibility.Collapsed;
            
            // 초기화: 정지 시 자막 초기화
            CancelWhisperExtraction();
            if (WhisperLoadingOverlay != null) WhisperLoadingOverlay.Visibility = Visibility.Collapsed;
            if (TxtWhisperProgress != null) TxtWhisperProgress.Text = "";
            if (TxtWhisperProgressFS != null) TxtWhisperProgressFS.Text = "";

            if (TxtSubtitle != null) TxtSubtitle.Text = "";
            if (SubtitleBorder != null) SubtitleBorder.Visibility = Visibility.Collapsed;
            if (BtnWhisper != null) BtnWhisper.Tag = null;
            if (BtnWhisperFS != null) BtnWhisperFS.Tag = null;

            if (ImgSplash != null) ImgSplash.Visibility = Visibility.Visible;
            _isUpdatingFromPlayer = true;
            if (SliderTimeline != null) SliderTimeline.Value = 0;
            if (SliderTimelineFS != null) SliderTimelineFS.Value = 0;
            _isUpdatingFromPlayer = false;
            if (TxtCurrentTime != null) TxtCurrentTime.Text = "00:00:00";
            if (TxtCurrentTimeFS != null) TxtCurrentTimeFS.Text = "00:00:00";
            SetRandomVibe();
            Title = "JonPlayer";
        }
        private void BtnSkipBack_Click    (object sender, RoutedEventArgs e) => SeekRelative(-10);
        private void BtnSkipForward_Click (object sender, RoutedEventArgs e) => SeekForward( 10);
        
        private void SeekForward(double offsetSeconds) => SeekRelative(offsetSeconds);

        private bool _isRepeat = false;
        private bool _isShuffle = false;
        private HashSet<int> _playedIndices = new HashSet<int>();

        private void BtnRepeat_Click(object sender, RoutedEventArgs e)
        {
            _isRepeat = !_isRepeat;
            if (BtnRepeat != null) BtnRepeat.Tag = _isRepeat ? "On" : "";
            if (BtnRepeatFS != null) BtnRepeatFS.Tag = _isRepeat ? "On" : "";
        }

        private void BtnShuffle_Click(object sender, RoutedEventArgs e)
        {
            _isShuffle = !_isShuffle;
            if (BtnShuffle != null) BtnShuffle.Tag = _isShuffle ? "On" : "";
            if (BtnShuffleFS != null) BtnShuffleFS.Tag = _isShuffle ? "On" : "";
            if (_isShuffle) _playedIndices.Clear();
        }

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

                _waveOut = new WaveOutEvent { DesiredLatency = 100 };
                _waveOut.Init(_waveProvider);
                if (SliderVolume != null)
                {
                _waveOut.Volume = _isMuted ? 0 : (float)(SliderVolume.Value / 100.0);
                }
            }
        }

        private void Decoder_AudioDataAvailable(byte[] buffer, int length)
        {
            if (_waveProvider != null && _waveProvider.BufferedDuration.TotalSeconds < 4.5)
            {
                _waveProvider.AddSamples(buffer, 0, length);
            }
        }

        private void TogglePlayPause()
        {
            if (_decoder == null || _decoder.IsFinished || !_decoder.IsRunning)
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
                _decoder.Pause();
                _waveOut?.Pause();
                UpdatePlayPauseUI(false);
            }
            else
            {
                _decoder.Play();
                _waveOut?.Play();
                UpdatePlayPauseUI(true);
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

            BtnPlayPause.ToolTip = isPlaying ? "Pause (Space)" : "Play (Space)";
            if (BtnPlayPauseFS != null) BtnPlayPauseFS.ToolTip = isPlaying ? "Pause" : "Play";
        }

        private void NotesTimer_Tick(object? sender, EventArgs e)
        {
            if (AudioNotesCanvas == null || (AudioNotesCanvas.Visibility != Visibility.Visible && AudioUI.Visibility != Visibility.Visible)) return;
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

        private void SeekRelative(double offsetSeconds)
        {
            if (_decoder == null || !_decoder.IsRunning) return;
            
            double totalSeconds = _decoder.DurationSeconds;
            if (totalSeconds <= 0) return;
            
            double currentRatio = SliderTimeline.Value / 1000.0;
            double currentSeconds = currentRatio * totalSeconds;
            double targetSeconds = Math.Clamp(currentSeconds + offsetSeconds, 0, totalSeconds);
            double targetRatio = targetSeconds / totalSeconds;
            
            _isUpdatingFromPlayer = true;
            SliderTimeline.Value = targetRatio * 1000.0;
            if (SliderTimelineFS != null) SliderTimelineFS.Value = targetRatio * 1000.0;
            _isUpdatingFromPlayer = false;

            _isSeeking = true;
            _decoder.Seek(targetRatio);
            _seekCount++;
        }

        private void BtnSpeed_Click(object sender, RoutedEventArgs e)
        {
            var btn  = sender as WpfButton;
            var menu = btn?.ContextMenu;
            if (menu == null) return;
            menu.PlacementTarget = btn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            menu.IsOpen = true;
        }

        private void UpdateSpeedUI(double speed)
        {
            _currentSpeed = speed;
            if (_decoder != null) _decoder.SetSpeed(speed);
            
            // Format without trailing .00 if it's an integer, etc.
            string formattedSpeed = (speed % 1 == 0) ? $"{speed:F1}" : $"{speed:F2}";
            string label = $"{formattedSpeed}x ▾";
            
            BtnSpeed.Content = label;
            if (BtnSpeedFS != null) BtnSpeedFS.Content = label;
            ShowToast($"재생 속도: {formattedSpeed}x");
        }

        private SubtitleManager _subtitleManager = new SubtitleManager();
        private bool _subtitlesEnabled = false;

        private void StartRainbowBlink(System.Windows.Controls.TextBlock target)
        {
            if (target == null) return;
            target.Visibility = Visibility.Visible;
            var anim = new System.Windows.Media.Animation.ColorAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromSeconds(5), // Slowed down from 2s to 5s
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
            };
            anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearColorKeyFrame(System.Windows.Media.Colors.Red, System.Windows.Media.Animation.KeyTime.FromPercent(0.0)));
            anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearColorKeyFrame(System.Windows.Media.Colors.Yellow, System.Windows.Media.Animation.KeyTime.FromPercent(0.2)));
            anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearColorKeyFrame(System.Windows.Media.Colors.Lime, System.Windows.Media.Animation.KeyTime.FromPercent(0.4)));
            anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearColorKeyFrame(System.Windows.Media.Colors.Cyan, System.Windows.Media.Animation.KeyTime.FromPercent(0.6)));
            anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearColorKeyFrame(System.Windows.Media.Colors.Magenta, System.Windows.Media.Animation.KeyTime.FromPercent(0.8)));
            anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearColorKeyFrame(System.Windows.Media.Colors.Red, System.Windows.Media.Animation.KeyTime.FromPercent(1.0)));

            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red);
            target.Foreground = brush;
            brush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, anim);
        }

        private void StopRainbowBlink(System.Windows.Controls.TextBlock? target)
        {
            if (target == null) return;
            target.Visibility = Visibility.Collapsed;
            if (target.Foreground is System.Windows.Media.SolidColorBrush brush)
            {
                brush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, null);
            }
            target.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xE5, 0xFF));
        }

        private bool _isWhisperAnimatingToCC = false;

        private void AnimateWhisperLoadingToCCButton()
        {
            if (WhisperLoadingOverlay.Visibility != Visibility.Visible || _isWhisperAnimatingToCC) return;
            _isWhisperAnimatingToCC = true;

            // Approximate target coordinates based on CC+ button location
            System.Windows.Point targetPos = new System.Windows.Point(0, 0);
            if (BtnWhisper != null && BtnWhisper.IsVisible)
            {
                try {
                    // Center of CC+ button
                    var ccPos = BtnWhisper.TransformToAncestor(this).Transform(new System.Windows.Point(BtnWhisper.ActualWidth / 2, BtnWhisper.ActualHeight / 2));
                    var centerPos = new System.Windows.Point(this.ActualWidth / 2, this.ActualHeight / 2);
                    // Add +30 to X so it lands on the right side of CC+ where the % text is
                    targetPos = new System.Windows.Point(ccPos.X - centerPos.X + 30, ccPos.Y - centerPos.Y);
                } catch { }
            }

            var duration = new Duration(TimeSpan.FromSeconds(1.2)); // Extended slightly for more drama

            // 1. Scale animation: Bounce up to 1.3, then shrink down to 0.1
            var scaleX = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
            scaleX.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(1.0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.Zero)));
            scaleX.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(1.3, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.3)), new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }));
            scaleX.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(0.1, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2)), new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }));

            var scaleY = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
            scaleY.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(1.0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.Zero)));
            scaleY.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(1.3, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.3)), new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }));
            scaleY.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(0.1, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2)), new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }));

            // 2. Translate animation: BackEase to pull back slightly before flying
            var transEase = new System.Windows.Media.Animation.BackEase { Amplitude = 0.6, EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn };
            var transX = new System.Windows.Media.Animation.DoubleAnimation(0, targetPos.X, duration) { EasingFunction = transEase };
            var transY = new System.Windows.Media.Animation.DoubleAnimation(0, targetPos.Y, duration) { EasingFunction = transEase };
            
            // 3. Fade out
            var fade = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.0, duration) { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn } };

            fade.Completed += (s, ev) =>
            {
                WhisperLoadingOverlay.Visibility = Visibility.Collapsed;
                // Reset for next time
                WhisperLoadingScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
                WhisperLoadingScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
                WhisperLoadingScale.ScaleX = 1;
                WhisperLoadingScale.ScaleY = 1;
                WhisperLoadingTranslate.X = 0;
                WhisperLoadingTranslate.Y = 0;
                WhisperLoadingPanel.Opacity = 1;
                WhisperLoadingOverlay.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x90, 0, 0, 0));
                
                _isWhisperAnimatingToCC = false;
                
                // Show the small text after animation finishes
                int currentProgress = (int)ProgWhisper.Value;
                TxtWhisperProgress.Text = $"{currentProgress}%";
                if (TxtWhisperProgressFS != null) TxtWhisperProgressFS.Text = $"{currentProgress}%";

                TxtWhisperProgress.Visibility = Visibility.Visible;
                if (TxtWhisperProgressFS != null) TxtWhisperProgressFS.Visibility = Visibility.Visible;
                StartRainbowBlink(TxtWhisperProgress);
                if (TxtWhisperProgressFS != null) StartRainbowBlink(TxtWhisperProgressFS);
            };

            WhisperLoadingScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleX);
            WhisperLoadingScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleY);
            WhisperLoadingTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, transX);
            WhisperLoadingTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, transY);
            WhisperLoadingPanel.BeginAnimation(UIElement.OpacityProperty, fade);
            
            // Fade out overlay background quickly
            var bgBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x90, 0, 0, 0));
            WhisperLoadingOverlay.Background = bgBrush;
            var bgFade = new System.Windows.Media.Animation.ColorAnimation(System.Windows.Media.Colors.Transparent, new Duration(TimeSpan.FromSeconds(0.4)));
            bgBrush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, bgFade);
        }

        private CancellationTokenSource? _whisperCts;

        private void CancelWhisperExtraction()
        {
            if (_whisperCts != null)
            {
                try { _whisperCts.Cancel(); } catch { }
                // DO NOT Dispose or set to null immediately. 
                // Whisper.net's native code crashes with AccessViolation if the CancellationTokenSource 
                // is disposed while its token is still being observed by the native process thread.
            }
        }

        private async void BtnWhisper_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFilePath)) {
                ShowToast("먼저 동영상을 열어주세요.");
                return;
            }

            // If CC+ is ON, turn it off
            if (BtnWhisper.Tag?.ToString() == "On")
            {
                CancelWhisperExtraction();
                _subtitlesEnabled = false;
                SubtitleBorder.Visibility = Visibility.Collapsed;
                BtnWhisper.Tag = null;
                if (BtnWhisperFS != null) BtnWhisperFS.Tag = null;
                ShowToast("자막(Whisper)을 껐습니다.");
                return;
            }

            // Otherwise, we want to extract/turn it ON
            string srtPath = Path.ChangeExtension(_currentFilePath, ".srt");
            
            // If already extracted or exists, just turn it on
            if (File.Exists(srtPath) && _subtitleManager.LoadSubtitle(srtPath))
            {
                _subtitlesEnabled = true;
                SubtitleBorder.Visibility = Visibility.Visible;
                BtnWhisper.Tag = "On";
                if (BtnWhisperFS != null) BtnWhisperFS.Tag = "On";
                ShowToast("생성된 자막을 표시합니다.");
                return;
            }
            
            ShowToast("Whisper 자막 추출 작업을 시작합니다.");
            WhisperLoadingOverlay.Visibility = Visibility.Visible;
            
            _subtitleManager.Clear();
            _subtitlesEnabled = true;
            SubtitleBorder.Visibility = Visibility.Visible;
            BtnWhisper.Tag = "On";
            if (BtnWhisperFS != null) BtnWhisperFS.Tag = "On";

            // 이전 진행률 UI 초기화
            TxtWhisperProgress.Text = "";
            if (TxtWhisperProgressFS != null) TxtWhisperProgressFS.Text = "";

            CancelWhisperExtraction();
            _whisperCts = new CancellationTokenSource();

            _isWhisperAnimatingToCC = false;
            bool canFly = false;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(3000, _whisperCts.Token);
                    canFly = true;
                    Dispatcher.Invoke(() =>
                    {
                        if (!_isWhisperAnimatingToCC && WhisperLoadingOverlay.Visibility == Visibility.Visible)
                        {
                            AnimateWhisperLoadingToCCButton();
                        }
                    });
                }
                catch { }
            });

            await WhisperExtractor.ExtractSubtitlesAsync(_currentFilePath, "temp_audio.wav", (status, progress) => {
                Dispatcher.InvokeAsync(() => {
                    int percentage = (int)progress; // 10% ~ 100%
                    
                    // 중앙 알림창 텍스트 통일 (아이콘 제외)
                    TxtWhisperStatus.Text = $"AI Subtitles 추출 중... ({percentage}%)";
                    ProgWhisper.Value = progress;

                    if (canFly)
                    {
                        if (!_isWhisperAnimatingToCC && WhisperLoadingOverlay.Visibility == Visibility.Visible)
                        {
                            AnimateWhisperLoadingToCCButton();
                        }
                        else if (!_isWhisperAnimatingToCC && WhisperLoadingOverlay.Visibility == Visibility.Collapsed)
                        {
                            TxtWhisperProgress.Text = $"{percentage}%";
                            if (TxtWhisperProgressFS != null) TxtWhisperProgressFS.Text = $"{percentage}%";

                            if (TxtWhisperProgress.Visibility != Visibility.Visible)
                            {
                                TxtWhisperProgress.Visibility = Visibility.Visible;
                                if (TxtWhisperProgressFS != null) TxtWhisperProgressFS.Visibility = Visibility.Visible;
                                StartRainbowBlink(TxtWhisperProgress);
                                if (TxtWhisperProgressFS != null) StartRainbowBlink(TxtWhisperProgressFS);
                            }
                        }
                    }
                });
            }, (start, end, text) => {
                Dispatcher.InvokeAsync(() => {
                    _subtitleManager.AddSubtitle(start, end, text);
                });
            }, (srtPathResult, errorMsg) => {
                Dispatcher.InvokeAsync(async () => {
                    WhisperLoadingOverlay.Visibility = Visibility.Collapsed;
                    _isWhisperAnimatingToCC = false;
                    WhisperLoadingScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
                    WhisperLoadingScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
                    WhisperLoadingTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
                    WhisperLoadingTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
                    WhisperLoadingPanel.BeginAnimation(UIElement.OpacityProperty, null);
                    
                    WhisperLoadingScale.ScaleX = 1;
                    WhisperLoadingScale.ScaleY = 1;
                    WhisperLoadingTranslate.X = 0;
                    WhisperLoadingTranslate.Y = 0;
                    WhisperLoadingPanel.Opacity = 1;

                    StopRainbowBlink(TxtWhisperProgress);
                    StopRainbowBlink(TxtWhisperProgressFS);

                    if (srtPathResult != null)
                    {
                        // 자막 파일 먼저 로드하여 화면에 확실히 적용
                        _subtitleManager.LoadSubtitle(srtPathResult);

                        // 100% -> Complete! 표시 (전체 추출 완료 후 한 번만)
                        TxtWhisperProgress.Visibility = Visibility.Visible;
                        if (TxtWhisperProgressFS != null) TxtWhisperProgressFS.Visibility = Visibility.Visible;
                        
                        TxtWhisperProgress.Text = "100%";
                        if (TxtWhisperProgressFS != null) TxtWhisperProgressFS.Text = "100%";
                        await Task.Delay(1000);
                        
                        TxtWhisperProgress.Text = "Complete!";
                        if (TxtWhisperProgressFS != null) TxtWhisperProgressFS.Text = "Complete!";
                        await Task.Delay(2000);
                        
                        TxtWhisperProgress.Visibility = Visibility.Collapsed;
                        if (TxtWhisperProgressFS != null) TxtWhisperProgressFS.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        TxtWhisperProgress.Visibility = Visibility.Collapsed;
                        if (TxtWhisperProgressFS != null) TxtWhisperProgressFS.Visibility = Visibility.Collapsed;
                        
                        _subtitlesEnabled = false;
                        SubtitleBorder.Visibility = Visibility.Collapsed;
                        BtnWhisper.Tag = null;
                        if (BtnWhisperFS != null) BtnWhisperFS.Tag = null;
                        ShowToast(errorMsg ?? "알 수 없는 오류");
                    }
                });
            }, _whisperCts.Token);
        }

        private void MenuItemSpeed_Click(object sender, RoutedEventArgs e)
        {
            if (_decoder == null) return;
            if (sender is System.Windows.Controls.MenuItem mi && mi.Tag != null
                && double.TryParse(mi.Tag.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double speed))
            {
                UpdateSpeedUI(speed);
            }
        }

        private bool _wasPlayingBeforeDrag = false;

        private void SliderTimeline_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        {
            _isUserDraggingSlider = true;
            if (_decoder != null && _decoder.IsPlaying)
            {
                _wasPlayingBeforeDrag = true;
                _decoder.Pause();
                _waveOut?.Pause();
                UpdatePlayPauseUI(false);
            }
            else
            {
                _wasPlayingBeforeDrag = false;
            }
        }

        private void SliderTimeline_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            _isUserDraggingSlider = false;
            
            Slider? targetSlider = sender as Slider;
            if (targetSlider == null && e.OriginalSource is System.Windows.Controls.Primitives.Thumb thumb)
            {
                targetSlider = thumb.TemplatedParent as Slider;
            }
            if (targetSlider == null) targetSlider = SliderTimeline;
            
            DoSeek(targetSlider.Value);

            if (_wasPlayingBeforeDrag && _decoder != null)
            {
                _decoder.Play();
                _waveOut?.Play();
                UpdatePlayPauseUI(true);
            }
        }

        private void Decoder_RotationDetected(double rotation)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (VideoRotation != null)
                    VideoRotation.Angle = rotation;
            });
        }

        private void SliderTimeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingFromPlayer) return;

            // Keep the two sliders in sync
            if (SliderTimelineFS != null && sender == SliderTimeline)
            {
                _isUpdatingFromPlayer = true;
                SliderTimelineFS.Value = SliderTimeline.Value;
                _isUpdatingFromPlayer = false;
            }
            else if (SliderTimeline != null && sender == SliderTimelineFS)
            {
                _isUpdatingFromPlayer = true;
                SliderTimeline.Value = SliderTimelineFS.Value;
                _isUpdatingFromPlayer = false;
            }

            // Only seek on direct click (not during drag — DragCompleted handles that,
            // and not when the player is updating the slider position).
            if (!_isUserDraggingSlider)
            {
                DoSeek(e.NewValue);
            }
        }

        private void SliderTimelineFS_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingFromPlayer) return;
            // Sync to main slider — SliderTimeline_ValueChanged will handle the seek
            _isUpdatingFromPlayer = true;
            SliderTimeline.Value = SliderTimelineFS.Value;
            _isUpdatingFromPlayer = false;
        }

        private void DoSeek(double sliderValue)
        {
            if (_decoder == null || !_decoder.IsRunning)
            {
                _isSeeking = false;
                return;
            }
            _isSeeking = true;
            _decoder.Seek(sliderValue / 1000.0);
            _seekCount++;
        }

        private void Decoder_SeekInitiated()
        {
            _waveProvider?.ClearBuffer();
        }

        private void Decoder_SeekPerformed()
        {
            Dispatcher.BeginInvoke(() =>
            {
                _isSeeking = false;
            });
        }

        private void SliderVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingFromPlayer) return;

            int vol = (int)e.NewValue;
            _isUpdatingFromPlayer = true;
            if (sender == SliderVolume && SliderVolumeFS != null) SliderVolumeFS.Value = vol;
            else if (sender == SliderVolumeFS && SliderVolume != null) SliderVolume.Value = vol;
            _isUpdatingFromPlayer = false;

            if (!_isMuted) UpdateMuteIcon(vol);
            if (_waveOut != null) _waveOut.Volume = _isMuted ? 0 : (float)(vol / 100.0);
        }

        private void UpdateMuteIcon(int vol)
        {
            string geometryKey = vol switch { 0 => "MuteIcon", < 35 => "VolumeLowIcon", _ => "VolumeHighIcon" };
            var geom = (Geometry)FindResource(geometryKey);
            if (MuteIconPath != null) MuteIconPath.Data = geom;
            if (MuteIconPathFS != null) MuteIconPathFS.Data = geom;
        }

        private void BtnMute_Click(object sender, RoutedEventArgs e) => ToggleMute();

        private void ToggleMute()
        {
            if (_isMuted)
            {
                _isMuted = false;
                SliderVolume.Value  = _lastVolume;
                if (SliderVolumeFS != null) SliderVolumeFS.Value = _lastVolume;
                UpdateMuteIcon(_lastVolume);
            }
            else
            {
                _lastVolume = (int)SliderVolume.Value;
                _isMuted    = true;
                SliderVolume.Value  = 0;
                if (SliderVolumeFS != null) SliderVolumeFS.Value = 0;
                
                var muteGeom = (Geometry)FindResource("MuteIcon");
                if (MuteIconPath != null) MuteIconPath.Data = muteGeom;
                if (MuteIconPathFS != null) MuteIconPathFS.Data = muteGeom;
            }
            if (_isFullscreen) ShowFsVolumeOverlay();
        }

        private void AdjustVolume(int delta)
        {
            SliderVolume.Value = Math.Clamp(SliderVolume.Value + delta, 0, 100);
            if (_isFullscreen) ShowFsVolumeOverlay();
        }

        private void ShowFsVolumeOverlay()
        {
            if (!_isFullscreen) return;
            
            double vol = SliderVolume.Value;
            
            if (_isMuted)
            {
                TxtFsVolume.Text = "Mute";
                IconFsVolume.Data = (Geometry)FindResource("MuteIcon");
                FsVolumeFill.Height = 0;
            }
            else
            {
                TxtFsVolume.Text = $"{(int)vol}%";
                string geometryKey = vol switch { 0 => "MuteIcon", < 35 => "VolumeLowIcon", _ => "VolumeHighIcon" };
                IconFsVolume.Data = (Geometry)FindResource(geometryKey);
                
                double trackHeight = FsVolumeTrack.ActualHeight > 0 ? FsVolumeTrack.ActualHeight : 140;
                FsVolumeFill.Height = (vol / 100.0) * trackHeight;
            }
            
            OverlayFsVolume.Visibility = Visibility.Visible;
            _fsVolumeTimer.Stop();
            _fsVolumeTimer.Start();
        }

        private void BtnFullscreen_Click    (object sender, RoutedEventArgs e) => EnterFullscreen();
        private void BtnExitFullscreen_Click(object sender, RoutedEventArgs e) => ExitFullscreen();

        private void BtnFsCloseVideo_Click(object sender, RoutedEventArgs e)
        {
            if (_isFullscreen) this.Close();
        }

        private void ToggleFullscreen()
        {
            if (_isFullscreen) ExitFullscreen();
            else EnterFullscreen();
        }

        private void FitScreen()
        {
            if (_isFullscreen) ExitFullscreen();
            this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void ResizeScreen(double scale)
        {
            if (_isFullscreen) ExitFullscreen();
            if (this.WindowState == WindowState.Maximized) this.WindowState = WindowState.Normal;
            
            if (_decoder != null && _decoder.Width > 0 && _decoder.Height > 0)
            {
                this.Width = _decoder.Width * scale;
                this.Height = _decoder.Height * scale + 60; // add some height for the control bar
            }
        }

        private void ShowToast(string message)
        {
            TxtToast.Text = message;
            ToastOverlay.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private void CaptureFrame()
        {
            if (VideoElement.Source == null) 
            { 
                ShowToast("캡처할 화면이 없습니다."); 
                return; 
            }
            try
            {
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)VideoElement.ActualWidth, (int)VideoElement.ActualHeight,
                    96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                var drawingVisual = new System.Windows.Media.DrawingVisual();
                using (var context = drawingVisual.RenderOpen())
                {
                    var rect = new Rect(0, 0, VideoElement.ActualWidth, VideoElement.ActualHeight);
                    context.DrawImage(VideoElement.Source, rect);
                }
                rtb.Render(drawingVisual);
                
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                
                string downloadsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                string filename = $"JonPlayer_Snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                string fullPath = System.IO.Path.Combine(downloadsPath, filename);
                
                using (var fs = new System.IO.FileStream(fullPath, System.IO.FileMode.Create))
                {
                    encoder.Save(fs);
                }
                ShowToast($"캡처 완료: {filename}");
            }
            catch (Exception ex)
            {
                ShowToast($"캡처 실패: {ex.Message}");
            }
        }

        private async void CloseFile()
        {
            if (_decoder != null)
            {
                if (_decoder.IsPlaying) TogglePlayPause();
                
                await Task.Delay(50);
                
                _decoder.Stop();
                _decoder.Dispose();
                _decoder = null;
            }
            if (_renderer != null)
            {
                _renderer.Dispose();
                _renderer = null;
            }
            if (VideoElement != null) VideoElement.Source = null;
            if (VideoViewbox != null) VideoViewbox.Visibility = Visibility.Collapsed;
            if (ImgSplash != null) ImgSplash.Visibility = Visibility.Visible;
            if (AudioUI != null) AudioUI.Visibility = Visibility.Collapsed;
            
            _currentFilePath = null;
            this.Title = "JonPlayer";
            TxtNowPlaying.Text = "Pick Your Vibe";
            
            TxtCurrentTime.Text = "00:00:00";
            TxtTotalTime.Text = "00:00:00";
            if (TxtCurrentTimeFS != null) TxtCurrentTimeFS.Text = "00:00:00";
            if (TxtTotalTimeFS != null) TxtTotalTimeFS.Text = "00:00:00";

            SliderTimeline.Value = 0;
            if (SliderTimelineFS != null) SliderTimelineFS.Value = 0;
            SetRandomVibe();
            
            ShowToast("파일 닫기 완료");
        }

        private bool _isChangingFullscreen = false;

        private void EnterFullscreen()
        {
            if (_isFullscreen) return;

            _isChangingFullscreen = true;
            _isFullscreen = true;

            _prevWindowState = WindowState;
            _prevWindowStyle = WindowStyle;
            _prevResizeMode  = ResizeMode;
            _prevTopmost     = Topmost;

            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
            if (chrome != null)
            {
                chrome.CaptionHeight = 0;
                chrome.ResizeBorderThickness = new Thickness(0);
            }

            WindowStyle  = WindowStyle.None;
            ResizeMode   = ResizeMode.NoResize;
            
            WindowState  = WindowState.Normal;
            WindowState  = WindowState.Maximized;
            Topmost      = true;

            RowTitleBar.Height = new GridLength(0);
            RowTimeline.Height = new GridLength(0);
            RowControls.Height = new GridLength(0);

            MainGrid.Margin = new Thickness(0);
            _isChangingFullscreen = false;

            if (SliderVolumeFS   != null) SliderVolumeFS.Value   = SliderVolume.Value;
            if (SliderTimelineFS != null) SliderTimelineFS.Value  = SliderTimeline.Value;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                VideoGrid.UpdateLayout();
                PopupFsExit.IsOpen = false;
                FsBottomStrip.Visibility = Visibility.Collapsed;
                BtnFsCloseVideo.Visibility = Visibility.Visible;
                _fsMousePollTimer.Start();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ExitFullscreen()
        {
            if (!_isFullscreen) return;
            _isChangingFullscreen = true;
            _isFullscreen = false;

            RowTitleBar.Height = new GridLength(40);
            RowTimeline.Height = GridLength.Auto;
            RowControls.Height = GridLength.Auto;

            WindowStyle = _prevWindowStyle;
            WindowState = _prevWindowState;
            ResizeMode  = _prevResizeMode;
            Topmost     = _prevTopmost;
            _isChangingFullscreen = false;

            if (WindowState == WindowState.Maximized)
            {
                BtnMaximize.Content = "❐";
                BtnMaximize.ToolTip = "Restore";
            }
            else
            {
                BtnMaximize.Content = "⬜";
                BtnMaximize.ToolTip = "Maximize";
            }

            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
            if (chrome != null)
            {
                chrome.CaptionHeight = 40;
                chrome.ResizeBorderThickness = new Thickness(6);
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                MainGrid.UpdateLayout();
                this.InvalidateVisual();
            }), System.Windows.Threading.DispatcherPriority.Render);

            PopupFsExit.IsOpen = false;
            FsBottomStrip.Visibility = Visibility.Collapsed;
            BtnFsCloseVideo.Visibility = Visibility.Collapsed;
            _fsMousePollTimer.Stop();
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.OriginalSource is WpfTextBox || e.OriginalSource is WpfComboBox) return;

            bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool isShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            switch (e.Key)
            {
                case Key.F1:
                    ShortcutsOverlay.Visibility = ShortcutsOverlay.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                    e.Handled = true; break;

                case Key.Space: TogglePlayPause(); e.Handled = true; break;
                case Key.Left: 
                    if (isCtrl) SeekRelative(-30);
                    else SeekRelative(-5); 
                    e.Handled = true; break;
                    
                case Key.Right: 
                    if (isCtrl) SeekRelative(30);
                    else SeekRelative(5); 
                    e.Handled = true; break;
                    
                case Key.Up: 
                    if (PlaylistOverlay.Visibility == Visibility.Visible)
                    {
                        if (ListPlaylist.SelectedIndex > 0)
                            ListPlaylist.SelectedIndex--;
                        else if (ListPlaylist.Items.Count > 0)
                            ListPlaylist.SelectedIndex = ListPlaylist.Items.Count - 1;
                        if (ListPlaylist.SelectedItem != null) ListPlaylist.ScrollIntoView(ListPlaylist.SelectedItem);
                        StartPlaylistHideTimer();
                    }
                    else
                    {
                        AdjustVolume(5);
                    }
                    e.Handled = true; break;

                case Key.Down: 
                    if (PlaylistOverlay.Visibility == Visibility.Visible)
                    {
                        if (ListPlaylist.SelectedIndex < ListPlaylist.Items.Count - 1)
                            ListPlaylist.SelectedIndex++;
                        else if (ListPlaylist.Items.Count > 0)
                            ListPlaylist.SelectedIndex = 0;
                        if (ListPlaylist.SelectedItem != null) ListPlaylist.ScrollIntoView(ListPlaylist.SelectedItem);
                        StartPlaylistHideTimer();
                    }
                    else
                    {
                        AdjustVolume(-5);
                    }
                    e.Handled = true; break;
                
                case Key.M: ToggleMute(); e.Handled = true; break;
                case Key.F11: ToggleFullscreen(); e.Handled = true; break;

                // Subtitle position shortcuts
                case Key.W: 
                    if (isCtrl) { CloseFile(); }
                    else { SubtitleTransform.Y -= 10; }
                    e.Handled = true; 
                    break;
                case Key.S: SubtitleTransform.Y += 10; e.Handled = true; break;
                case Key.A: SubtitleTransform.X -= 10; e.Handled = true; break;
                case Key.D: SubtitleTransform.X += 10; e.Handled = true; break;
                
                // Subtitle font size shortcuts
                case Key.OemPlus:
                case Key.Add:
                    if (TxtSubtitle != null) TxtSubtitle.FontSize += 2;
                    e.Handled = true; break;
                case Key.OemMinus:
                case Key.Subtract:
                    if (TxtSubtitle != null && TxtSubtitle.FontSize > 10) TxtSubtitle.FontSize -= 2;
                    e.Handled = true; break;

                case Key.Enter: 
                    if (PlaylistOverlay.Visibility == Visibility.Visible)
                    {
                        if (ListPlaylist.SelectedItem is PlaylistItem item)
                        {
                            PlayFile(item.Path);
                            _playlistIndex = _playlist.IndexOf(item);
                            UpdateNowPlayingHighlight();
                            StartPlaylistHideTimer();
                        }
                    }
                    else
                    {
                        ToggleFullscreen();
                    }
                    e.Handled = true; break;
                
                case Key.F3: ToggleStatsOverlay(); e.Handled = true; break;
                
                case Key.Escape:
                    if (_isFullscreen) { ExitFullscreen(); e.Handled = true; }
                    break;
                    
                case Key.F: FitScreen(); e.Handled = true; break;
                
                case Key.Oem3:
                    ResizeScreen(0.5); ShowToast("창 크기: 50%"); e.Handled = true; break;
                case Key.D1:
                case Key.NumPad1: ResizeScreen(1.0); ShowToast("창 크기: 100%"); e.Handled = true; break;
                case Key.D2:
                case Key.NumPad2: ResizeScreen(2.0); ShowToast("창 크기: 200%"); e.Handled = true; break;
                
                case Key.Z:
                    if (VideoViewbox.Stretch == System.Windows.Media.Stretch.Uniform) {
                        VideoViewbox.Stretch = System.Windows.Media.Stretch.UniformToFill;
                        VideoElement.Stretch = System.Windows.Media.Stretch.UniformToFill;
                        ShowToast("화면 맞춤: 가득 채우기 (자르기)");
                    } else if (VideoViewbox.Stretch == System.Windows.Media.Stretch.UniformToFill) {
                        VideoViewbox.Stretch = System.Windows.Media.Stretch.Fill;
                        VideoElement.Stretch = System.Windows.Media.Stretch.Fill;
                        ShowToast("화면 맞춤: 강제 늘림");
                    } else {
                        VideoViewbox.Stretch = System.Windows.Media.Stretch.Uniform;
                        VideoElement.Stretch = System.Windows.Media.Stretch.Uniform;
                        ShowToast("화면 맞춤: 원본 비율 (여백)");
                    }
                    e.Handled = true; break;

                case Key.OemComma:
                    if (_decoder != null) {
                        double newSpeed = Math.Max(0.25, _decoder.PlaybackSpeed - 0.25);
                        UpdateSpeedUI(newSpeed);
                    }
                    e.Handled = true; break;

                case Key.OemPeriod:
                    if (_decoder != null) {
                        double newSpeed = Math.Min(4.0, _decoder.PlaybackSpeed + 0.25);
                        UpdateSpeedUI(newSpeed);
                    }
                    e.Handled = true; break;

                case Key.C:
                    CaptureFrame();
                    e.Handled = true; break;
                
                case Key.O:
                    if (isCtrl && isShift) { OpenFolder(); e.Handled = true; }
                    else if (isCtrl) { OpenFile(); e.Handled = true; }
                    break;
                    

                    
                case Key.F9: TogglePlaylist(); e.Handled = true; break;
                case Key.L:
                    if (isCtrl) { TogglePlaylist(); e.Handled = true; }
                    break;
            }
        }

        private void ToggleStatsOverlay()
        {
            if (OverlayStats.Visibility == Visibility.Visible)
            {
                OverlayStats.Visibility = Visibility.Collapsed;
                _statsTimer.Stop();
            }
            else
            {
                OverlayStats.Visibility = Visibility.Visible;
                if (_decoder != null && _decoder.IsPlaying) _statsTimer.Start();
            }
        }

        private void StatsTimer_Tick(object? sender, EventArgs e)
        {
            if (_decoder == null) return;

            var currentCpuTime = Process.GetCurrentProcess().TotalProcessorTime;
            var currentCheckTime = DateTime.UtcNow;
            var cpuUsedMs = (currentCpuTime - _lastCpuTime).TotalMilliseconds;
            var totalMsPassed = (currentCheckTime - _lastCpuCheckTime).TotalMilliseconds;
            
            double cpuUsage = 0;
            if (totalMsPassed > 0)
                cpuUsage = (cpuUsedMs / (Environment.ProcessorCount * totalMsPassed)) * 100.0;

            _lastCpuTime = currentCpuTime;
            _lastCpuCheckTime = currentCheckTime;

            var process = Process.GetCurrentProcess();
            double memoryMb = process.WorkingSet64 / 1024.0 / 1024.0;
            int totalThreads = process.Threads.Count;

            double avgRender = 0;
            if (_renderSamples > 0)
            {
                avgRender = _totalRenderTimeMs / _renderSamples;
                _totalRenderTimeMs = 0;
                _renderSamples = 0;
            }

            var stats = _decoder.GetStats();
            string state = _decoder.IsPlaying ? "Playing" : "Paused";
            if (!_decoder.IsRunning) state = "Stopped";

            var sb = new StringBuilder();
            
            sb.AppendLine("Video");
            sb.AppendLine("────────────────────");
            var infoParts = stats.VideoInfo?.Split(' ');
            string res = infoParts?.Length > 0 ? infoParts[0] : "";
            string codec = infoParts?.Length > 1 ? infoParts[1] : "";
            
            sb.AppendLine($"{"Codec".PadRight(12)}{codec}");
            sb.AppendLine($"{"Resolution".PadRight(12)}{res}");
            sb.AppendLine($"{"FPS".PadRight(12)}{stats.ActualFps:F1} / {stats.TargetFps:F1}");
            sb.AppendLine($"{"Dropped".PadRight(12)}{stats.DroppedFrames}");
            sb.AppendLine($"{"Bitrate".PadRight(12)}{stats.Bitrate / 1000} kbps");
            sb.AppendLine($"{"HW Accel".PadRight(12)}{(stats.IsHwAccel ? "Active (D3D11)" : "Inactive (CPU)")}");
            sb.AppendLine();
            
            sb.AppendLine("Performance");
            sb.AppendLine("────────────────────");
            sb.AppendLine($"{"Decode".PadRight(12)}{stats.AvgDecodeTimeMs:F1} ms");
            sb.AppendLine($"{"Render".PadRight(12)}{avgRender:F1} ms");
            sb.AppendLine($"{"Total".PadRight(12)}{(stats.AvgDecodeTimeMs + avgRender):F1} ms");
            sb.AppendLine();
            
            sb.AppendLine("Buffer");
            sb.AppendLine("────────────────────");
            sb.AppendLine($"{"PacketQ".PadRight(12)}{stats.PacketQueueSize}");
            sb.AppendLine($"{"AudioQ".PadRight(12)}{stats.AudioQueueSize}");
            sb.AppendLine();
            
            sb.AppendLine("Sync");
            sb.AppendLine("────────────────────");
            sb.AppendLine($"{"Video PTS".PadRight(12)}{stats.VideoPts:F0} ms");
            sb.AppendLine($"{"Audio PTS".PadRight(12)}{stats.AudioPts:F0} ms");
            sb.AppendLine($"{"Drift".PadRight(12)}{stats.SyncDelayMs:F0} ms");
            sb.AppendLine($"{"LateFrames".PadRight(12)}{stats.LateFrames}");
            sb.AppendLine();
            
            sb.AppendLine("System");
            sb.AppendLine("────────────────────");
            sb.AppendLine($"{"CPU".PadRight(12)}{cpuUsage:F1}%");
            sb.AppendLine($"{"Memory".PadRight(12)}{memoryMb:F0} MB");
            sb.AppendLine($"{"Threads".PadRight(12)}{totalThreads}");
            sb.AppendLine();
            
            sb.AppendLine("Session");
            sb.AppendLine("────────────────────");
            sb.AppendLine($"{"State".PadRight(12)}{state}");
            sb.AppendLine($"{"OpenCount".PadRight(12)}{_openCount}");
            sb.Append($"{"SeekCount".PadRight(12)}{_seekCount}");

            TxtOverlayStats.Text = sb.ToString();
        }
        private bool _isDraggingSubtitle = false;
        private System.Windows.Point _subtitleLastMousePos;

        private void SubtitleBorder_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _isDraggingSubtitle = true;
            _subtitleLastMousePos = e.GetPosition(this);
            SubtitleBorder.CaptureMouse();
        }

        private void SubtitleBorder_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isDraggingSubtitle)
            {
                _isDraggingSubtitle = false;
                SubtitleBorder.ReleaseMouseCapture();
            }
        }

        private void SubtitleBorder_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isDraggingSubtitle)
            {
                var currentPos = e.GetPosition(this);
                double dx = currentPos.X - _subtitleLastMousePos.X;
                double dy = currentPos.Y - _subtitleLastMousePos.Y;
                
                SubtitleTransform.X += dx;
                SubtitleTransform.Y += dy;
                
                _subtitleLastMousePos = currentPos;
            }
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // No custom Hwnd sync is needed as WPF native Image adapts automatically.
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // All cleanup is handled in the Closed event handler (constructor)
        }
    }
}