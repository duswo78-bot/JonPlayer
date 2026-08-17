using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Text.Json;
using AutoUpdaterDotNET;
using YoutubeExplode.Videos.Streams;
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
using System.Timers;
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
        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();



        private void SyncMainWindowToOverlay()
        {
            if (_overlayWindow == null || this.WindowState == WindowState.Minimized) return;

            double targetLeft = _overlayWindow.Left + 1;
            double targetTop = _overlayWindow.Top + 1;
            double targetWidth = Math.Max(this.MinWidth, _overlayWindow.Width - 2);
            double targetHeight = Math.Max(this.MinHeight, _overlayWindow.Height - 2);

            if (Math.Abs(this.Left - targetLeft) > 1.0) this.Left = targetLeft;
            if (Math.Abs(this.Top - targetTop) > 1.0) this.Top = targetTop;
            if (Math.Abs(this.Width - targetWidth) > 1.0) this.Width = targetWidth;
            if (Math.Abs(this.Height - targetHeight) > 1.0) this.Height = targetHeight;
        }
        private void UpdateVideoMargin()
        {
            if (_videoHwndHost == null) return;
            if (_isFullscreen || _isPipMode)
            {
                _videoHwndHost.Margin = new System.Windows.Thickness(0);

            }
            else
            {
                double top = RowTitleBar.ActualHeight > 0 ? RowTitleBar.ActualHeight : 40;
                double bottom = (RowTimeline.ActualHeight + RowControls.ActualHeight) > 0 ? (RowTimeline.ActualHeight + RowControls.ActualHeight) : 75;
                _videoHwndHost.Margin = new System.Windows.Thickness(0, top, 0, bottom);

            }
        }

        private void SyncOverlayWindowToMainWindow()
        {
            if (_overlayWindow == null || this.WindowState == WindowState.Minimized) return;

            double targetLeft = this.Left - 1;
            double targetTop = this.Top - 1;
            double targetWidth = (this.ActualWidth > 0 ? this.ActualWidth : this.Width) + 2;
            double targetHeight = (this.ActualHeight > 0 ? this.ActualHeight : this.Height) + 2;

            if (this.WindowState == WindowState.Maximized)
            {
                targetLeft = this.Left;
                targetTop = this.Top;
                targetWidth = this.ActualWidth > 0 ? this.ActualWidth : this.Width;
                targetHeight = this.ActualHeight > 0 ? this.ActualHeight : this.Height;
            }

            if (Math.Abs(_overlayWindow.Left - targetLeft) > 1.0) _overlayWindow.Left = targetLeft;
            if (Math.Abs(_overlayWindow.Top - targetTop) > 1.0) _overlayWindow.Top = targetTop;
            if (Math.Abs(_overlayWindow.Width - targetWidth) > 1.0) _overlayWindow.Width = targetWidth;
            if (Math.Abs(_overlayWindow.Height - targetHeight) > 1.0) _overlayWindow.Height = targetHeight;
            
            UpdateVideoMargin();
        }

        private void BeginMainWindowDrag()
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            ReleaseCapture();
            _ = SendMessage(handle, 0xA1, 2, 0); // WM_NCLBUTTONDOWN, HTCAPTION
        }


        private D3D11VideoRenderer? _renderer;
        private FFmpegMediaDecoder? _decoder;
        
        private WaveOutEvent? _waveOut;
        private bool _userWantsPlayback = true;
        private long _lastPlayPauseToggleUtcTicks;
        private const int PlayPauseToggleDebounceMs = 150;
        private BufferedWaveProvider? _waveProvider;
        private AudioEnhancerProvider? _audioEnhancer;
        private Thread? _uiUpdateThread;

        private bool _isUserDraggingSlider;
        private bool _isUpdatingFromPlayer;
        private bool _isSeeking;
        private bool _allowTimelineBackward;
        private bool _suppressTimelineSeek;
        private double _pendingSeekSubtitleMs = -1.0;

        // === SEEK UX PROTECTION ZONE ===
        // Core contract (do not accidentally break when touching PlayFile, timelines, or renderer):
        // - User clicks a point on the bar -> bar + UI clock move there *immediately*.
        // - We tell decoder to seek.
        // - On actual land, we only snap the bar *forward* or on huge errors.
        //   This prevents the bar from jumping back to the keyframe time (common video behavior).
        // Related decoder protections: _activeSeekGen cancel, small-seek queue preserve, reduced post-seek prebuffer.
        // If you change resets or open/seek paths, make sure _lastUserSeekTargetMs and related flags are cleared.
        private double _lastUserSeekTargetMs = -1.0;

        private int _streamingSeekGeneration;
        private int _openGeneration;
        private bool _isClosing;
        private long _lastFrameTicks;
        private double _initialWidth;
        private double _initialHeight;
        private long _lastAudioTicks;

        private const int AutoVolumeCenter = 50;
        private const int AutoVolumeMaxOffset = 15;
        private const double AutoVolumeMaxGainDb = 20.0;
        private const double AutoVolumeTargetMeanDb = -16.0;
        private const double AutoVolumePeakLimitDb = -0.3;

        private int   _lastVolume    = 80;
        private int   _userPreferredVolume = AutoVolumeCenter;
        private double _lastAutoGainDb = 0.0;
        private double _lastDetectedMeanDb = double.NaN;
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
        private DispatcherTimer _subtitleTimer;
        private DispatcherTimer _toastTimer;
        private DispatcherTimer _timelineTimer;

        // UI-only wall-clock for smooth independent progress bar + time display
        private Stopwatch _uiClock = new Stopwatch();
        private double _uiClockBaseMs = 0.0;
        private double _uiClockSpeed = 1.0;

        // More reliable update driver for continuous wall-time progress (avoids DispatcherTimer starvation)
        private bool _timelineRenderingHooked;

        // Threadpool-based timer for reliable timeline updates especially on audio-only (static UI)
        // where WPF CompositionTarget and DispatcherTimer can be throttled.
        private System.Timers.Timer? _preciseTimelineTimer;

        // Stats Overlay
        private int _openCount = 0;
        private int _seekCount = 0;
        private int _audioUnderrunCount = 0;
        private TimeSpan _lastCpuTime;
        private DateTime _lastCpuCheckTime;

        private static string? _lastOpenDirectory;

        private bool _isMouseOverFsStrip;
        private bool _isMouseOverFsExitBadge;
        private Point _lastPolledMousePos = new Point(double.NaN, double.NaN);
        private const double FsBottomHotZonePx = 130;
        private const double FsTopHotZonePx = 80;

        private static bool IsStreamingPath(string? path)
        {
            return !string.IsNullOrEmpty(path) &&
                   (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsAdaptiveStreamingPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return path.Contains(".mpd", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("/manifest", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("manifest(", StringComparison.OrdinalIgnoreCase);
        }


        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint uPeriod);

        private DispatcherTimer? _playlistTimer;
        private bool _isPlaylistHovered;

        private DispatcherTimer _cursorHideTimer;
        private DispatcherTimer _fsVolumeTimer;
        private DispatcherTimer _notesTimer;
        private DispatcherTimer _bassHoldTimer;
        private Random _notesRandom = new Random();
        private string[] _musicNotes = { "♩", "♪", "♫", "♬", "♭", "♮", "♯" };
        private WpfButton? _activeBassHoldButton;
        private bool _bassHoldTriggered;
        private const string BassTagOn = "On";
        private const string BassTagMax = "Max";

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

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ControlBar != null)
            {
                if (e.NewSize.Width < 950)
                {
                    double scale = e.NewSize.Width / 950.0;
                    if (scale < 0.5) scale = 0.5;
                    ControlBar.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
                }
                else
                {
                    ControlBar.LayoutTransform = null;
                }
            }
        }

        private void CheckRegistrationAndWhisper()
        {
            // 1. Check Whisper installation
            bool whisperInstalled = true; // default to true in case registry fails/not running from setup
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\JonPlayer"))
                {
                    if (key != null)
                    {
                        var val = key.GetValue("WhisperInstalled");
                        if (val != null) whisperInstalled = (int)val == 1;
                    }
                }
            }
            catch { }

            if (!whisperInstalled)
            {
                if (BtnWhisper != null) BtnWhisper.Visibility = Visibility.Collapsed;
                if (BtnWhisperFS != null) BtnWhisperFS.Visibility = Visibility.Collapsed;
            }

            // 2. Check Registration
            bool hasLaunched = false;
            bool isAptivEmployee = false;
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\JonPlayer"))
                {
                    if (key != null)
                    {
                        var valHasLaunched = key.GetValue("HasLaunchedBefore");
                        if (valHasLaunched != null) hasLaunched = (int)valHasLaunched == 1;

                        var valAptiv = key.GetValue("IsAptivEmployee");
                        if (valAptiv != null) isAptivEmployee = (int)valAptiv == 1;
                    }
                }
            }
            catch { }

            if (!hasLaunched)
            {
                var regWindow = new RegistrationWindow();
                regWindow.Owner = this;
                regWindow.ShowDialog();

                isAptivEmployee = regWindow.IsRegisteredEmployee;
            }

            if (isAptivEmployee && AptivLogoPanel != null)
            {
                AptivLogoPanel.Visibility = Visibility.Visible;
            }
        }

        public void LoadExternalFiles(string[] files)
        {
            if (files != null && files.Length > 0)
            {
                var validFiles = files.Where(f => System.IO.File.Exists(f)).ToArray();
                if (validFiles.Length > 0)
                {
                    LoadPlaylist(validFiles);
                }
            }
        }

        private Window? _overlayWindow;
        private VideoHwndHost? _videoHwndHost;

        private void UpdateSubtitleLanguage()
        {
            if (_subtitleManager.HasSubtitles)
            {
                string lang = _subtitleManager.DetectLanguage();
                string displayLang = _isTranslationEnabled ? (lang == "KR" ? "EN" : "KR") : lang;
                Dispatcher.Invoke(() => {
                    if (BtnTranslate != null) BtnTranslate.Content = displayLang;
                    if (BtnTranslateFS != null) BtnTranslateFS.Content = displayLang;
                });
            }
            else
            {
                Dispatcher.Invoke(() => {
                    if (BtnTranslate != null) BtnTranslate.Content = "KR";
                    if (BtnTranslateFS != null) BtnTranslateFS.Content = "KR";
                });
            }
        }

        public MainWindow()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            timeBeginPeriod(1);
            try { InitializeComponent(); } catch (Exception ex) { System.IO.File.WriteAllText("crash.txt", ex.ToString()); throw; }
            SetRandomVibe();
            _initialWidth = this.Width;
            _initialHeight = this.Height;

            this.Loaded += (s, e) =>
            {
                SetupOverlayWindow();
                CheckRegistrationAndWhisper();
                CheckForUpdates();

                var args = Environment.GetCommandLineArgs();
                if (args.Length > 1)
                {
                    LoadExternalFiles(args.Skip(1).ToArray());
                }
            };


            this.StateChanged += Window_StateChanged;
            this.MouseMove += Window_MouseMove;
                        this.LocationChanged += (s, e) =>
            {
                SyncOverlayWindowToMainWindow();
                // Monitor may change when the window is dragged — refresh which size presets fit.
                UpdateWindowSizePresetUi();
            };
            this.SizeChanged += (s, e) => SyncOverlayWindowToMainWindow();
            // Hook global thread messages to reliably capture shortcuts even if focus is lost or HwndHost steals it
            System.Windows.Interop.ComponentDispatcher.ThreadPreprocessMessage += ComponentDispatcher_ThreadPreprocessMessage;

            _fsMousePollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _fsMousePollTimer.Tick += FsMousePollTimer_Tick;

            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _statsTimer.Tick += StatsTimer_Tick;

            _subtitleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _subtitleTimer.Tick += SubtitleTimer_Tick;
            _subtitleTimer.Start();

            _timelineTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(20) };
            _timelineTimer.Tick += TimelineTimer_Tick;

            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _toastTimer.Tick += (s, e) => { ToastOverlay.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };

            _cursorHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _cursorHideTimer.Tick += (s, e) =>
            {
                if (ShouldAutoHideCursor())
                {
                    this.Cursor = System.Windows.Input.Cursors.None;
                    if (_overlayWindow != null) _overlayWindow.Cursor = System.Windows.Input.Cursors.None;
                    if (_videoHwndHost != null) _videoHwndHost.HideCursor = true;
                    if (_isFullscreen)
                    {
                        UpdateFsChromeVisibility(forceHideChrome: true);
                    }
                }
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

            _bassHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _bassHoldTimer.Tick += BassHoldTimer_Tick;

            _lastCpuTime = Process.GetCurrentProcess().TotalProcessorTime;
            _lastCpuCheckTime = DateTime.UtcNow;

            ApplyTheme(false);

            timeBeginPeriod(1);

            FsBottomStrip.IsVisibleChanged += (s, e) =>
            {
                var anim = new System.Windows.Media.Animation.DoubleAnimation
                {
                    To = FsBottomStrip.IsVisible ? -105 : (_isFullscreen ? -5 : 0),
                    Duration = TimeSpan.FromSeconds(0.3),
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };
                FsSubtitleShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, anim);
            };

            this.SizeChanged += MainWindow_SizeChanged;

            Closing += (s, e) => {
                _isClosing = true;
                Interlocked.Increment(ref _openGeneration);
                Interlocked.Increment(ref _streamingSeekGeneration);
                _isOpeningFile = false;
                Volatile.Write(ref _openingOwnedByGen, 0);
                _isSeeking = false;
                _currentPlaybackOpenGen = 0;
                _activePlaybackFinishedHandler = null;
                _pendingPlaylistTarget = null;

                // Stop all timers first to prevent null reference after disposal
                try { _fsMousePollTimer.Stop(); } catch { }
                try { _statsTimer.Stop(); } catch { }
                try { _toastTimer.Stop(); } catch { }
                try { _cursorHideTimer.Stop(); } catch { }
                try { _fsVolumeTimer.Stop(); } catch { }
                try { _notesTimer.Stop(); } catch { }
                try { _playlistTimer?.Stop(); } catch { }
                try { _timelineTimer.Stop(); } catch { }
                try { _preciseTimelineTimer?.Stop(); } catch { }
                try { _preciseTimelineTimer?.Dispose(); } catch { }
                _preciseTimelineTimer = null;
                if (_timelineRenderingHooked)
                {
                    CompositionTarget.Rendering -= TimelineRendering;
                    _timelineRenderingHooked = false;
                }

                CancelWhisperExtraction();
                StopStreamingLoadingBlink();

                // CRITICAL order for exit: detach render from decoder BEFORE disposing FFmpeg.
                // Old order (decoder Dispose while render thread still PullVideoFrame) → 0xC0000005.
                try { _waveOut?.Stop(); } catch { }
                try { _waveProvider?.ClearBuffer(); } catch { }

                var dec = _decoder;
                _decoder = null;
                if (dec != null)
                {
                    try { DetachDecoderEvents(dec); } catch { }
                }

                var ren = _renderer;
                _renderer = null;
                if (ren != null)
                {
                    try
                    {
                        ren.DetachDecoder();
                        ren.PrepareForDecoderTeardown();
                    }
                    catch { }
                }

                if (dec != null)
                {
                    try { dec.Stop(); } catch { }
                    try { dec.Dispose(); } catch { }
                }

                try { _waveOut?.Dispose(); } catch { }
                _waveOut = null;

                if (ren != null)
                {
                    try { ren.Dispose(); } catch { }
                }

                try { timeEndPeriod(1); } catch { }
            };
        }

        private void ComponentDispatcher_ThreadPreprocessMessage(ref System.Windows.Interop.MSG msg, ref bool handled)
        {
            if (handled) return;

            const int WM_KEYDOWN = 0x0100;
            const int WM_SYSKEYDOWN = 0x0104;
            if (msg.message != WM_KEYDOWN && msg.message != WM_SYSKEYDOWN) return;

            // Overlay PreviewKeyDown handles keys when the UI layer has focus.
            if (_overlayWindow != null && _overlayWindow.IsActive) return;

            // Do not steal keys when a TextBox/ComboBox has focus anywhere (e.g. URL input dialog for Ctrl+V paste)
            var focused = System.Windows.Input.Keyboard.FocusedElement;
            if (focused is WpfTextBox || focused is WpfComboBox) return;

            int vk = msg.wParam.ToInt32();
            Key key = KeyInterop.KeyFromVirtualKey(vk);
            if (key == Key.System) key = KeyInterop.KeyFromVirtualKey(vk);

            var args = new System.Windows.Input.KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(this),
                0,
                key)
            {
                RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent
            };

            Window_PreviewKeyDown(this, args);
            if (args.Handled) handled = true;
        }
        private void CheckForUpdates()
        {
            try
            {
                AutoUpdater.ParseUpdateInfoEvent += (args) =>
                {
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(args.RemoteData);
                        JsonElement root = doc.RootElement;
                        string tagName = root.GetProperty("tag_name").GetString();
                        string releaseUrl = root.GetProperty("html_url").GetString();
                        string downloadUrl = string.Empty;

                        if (root.TryGetProperty("assets", out JsonElement assets) && assets.GetArrayLength() > 0)
                        {
                            foreach (JsonElement asset in assets.EnumerateArray())
                            {
                                string name = asset.GetProperty("name").GetString();
                                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                                    break;
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(tagName) && tagName.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                        {
                            tagName = tagName.Substring(1);
                        }

                        args.UpdateInfo = new UpdateInfoEventArgs
                        {
                            CurrentVersion = tagName,
                            ChangelogURL = releaseUrl,
                            DownloadURL = downloadUrl,
                            Mandatory = new Mandatory { Value = false }
                        };
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to parse GitHub update info: {ex}");
                    }
                };

                AutoUpdater.Start("https://api.github.com/repos/duswo78-bot/JonPlayer/releases/latest");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to start AutoUpdater: {ex}");
            }
        }

        private void SetupOverlayWindow()
        {
            var mainGrid = (Grid)this.Content;
            mainGrid.Background = null; // Let clicks pass through empty areas to MainWindow
            this.Content = null;
            
            var videoContainer = new Grid { Background = WpfBrushes.Black };
            _videoHwndHost = new VideoHwndHost();
            _videoHwndHost.MouseLeftButtonDown += (s, e) =>
            {
                if (_isPipMode)
                {
                    BeginMainWindowDrag();
                }
            };
            _videoHwndHost.MouseDoubleClick += (s, e) =>
            {
                if (_isPipMode) TogglePipMode();
                else ToggleFullscreen();
            };
            videoContainer.Children.Add(_videoHwndHost);
            this.Content = videoContainer;

            _overlayWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = WpfBrushes.Transparent,
                ShowInTaskbar = false,
                Owner = this,
                Content = mainGrid,
                AllowDrop = true,
                Width = double.IsNaN(this.Width) ? 800 : this.Width + 2,
                Height = double.IsNaN(this.Height) ? 450 : this.Height + 2,
                MinWidth = this.MinWidth + 2,
                MinHeight = this.MinHeight + 2,
                Left = this.Left,
                Top = this.Top,
                WindowStartupLocation = this.WindowStartupLocation,
                WindowState = this.WindowState,
                Resources = this.Resources,
                DataContext = this.DataContext,
                Foreground = this.Foreground
            };

            SyncOverlayWindowToMainWindow();

            _overlayWindow.LocationChanged += (s, e) => SyncMainWindowToOverlay();
            _overlayWindow.SizeChanged += (s, e) => SyncMainWindowToOverlay();

            _overlayWindow.DragOver += Window_DragOver;
            _overlayWindow.Drop += Window_Drop;

            EventHandler activationHandler = (s, e) => {
                if (_isFullscreen) this.Topmost = true;
            };
            EventHandler deactivationHandler = (s, e) => {
                if (_isFullscreen) {
                    if (!this.IsActive && !_overlayWindow.IsActive) {
                        this.Topmost = false;
                    }
                }
            };

            this.Activated += activationHandler;
            this.Deactivated += deactivationHandler;
            _overlayWindow.Activated += activationHandler;
            _overlayWindow.Deactivated += deactivationHandler;

            System.Windows.Shell.WindowChrome.SetWindowChrome(_overlayWindow, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });

            TitleBar.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    if (this.WindowState == WindowState.Maximized) this.WindowState = WindowState.Normal;
                    else this.WindowState = WindowState.Maximized;
                }
                else if (e.LeftButton == MouseButtonState.Pressed)
                {
                    BeginMainWindowDrag();
                }
            };

            _overlayWindow.PreviewKeyDown += Window_PreviewKeyDown;
            _overlayWindow.MouseMove += OverlayWindow_MouseMove;
            _overlayWindow.Closing += (s, e) =>
            {
                if (!_isClosing) 
                {
                    this.Close();
                }
            };

            _overlayWindow.StateChanged += (os, oe) => { if (!_isFullscreen) this.WindowState = _overlayWindow.WindowState; };

            this.StateChanged += (os, oe) => { 
                if (_isFullscreen) _overlayWindow.WindowState = WindowState.Normal;
                else _overlayWindow.WindowState = this.WindowState; 
            };

            _overlayWindow.Show();


        }

        private void Decoder_FrameDecoded(IntPtr bgraData, int width, int height, int stride, bool isHardwareTexture)
        {
            Volatile.Write(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
            // Push frames logic was removed because Renderer now pulls from decoder.
            // This event handler should be kept empty or removed.
        }

        private void Decoder_PositionChanged(double ratio)
        {
            // Timeline UI is driven by independent wall-clock (GetUiClockMs).
            // Decoder master clock is still used for A/V sync, subtitles, and frame scheduling.
        }

        private void UpdateTimelineFromPlayback()
        {
            if (_isUserDraggingSlider || _isOpeningFile || _decoder == null || !_decoder.IsRunning) return;
            if (_isSeeking && (_decoder.HasVideo || !_decoder.IsSeekActive)) return;

            double durationMs = _decoder.DurationSeconds * 1000.0;
            if (durationMs <= 0 || SliderTimeline == null) return;

            double uiMs = GetUiClockMs();
            double newSliderValue = uiMs / durationMs * SliderTimeline.Maximum;

            // Only block clearly backward movement (wall clock should never go back).
            // The old guard was to protect against PTS jitter; with dedicated wall clock we relax it.
            if (!_allowTimelineBackward && newSliderValue + 0.5 < SliderTimeline.Value)
            {
                // Still allow forward jumps (e.g. after long timer delay or seek land)
                return;
            }
            _allowTimelineBackward = false;

            // Only update if there's a visible change to reduce binding/layout churn on Slider + fill converter.
            _isUpdatingFromPlayer = true;
            if (Math.Abs(SliderTimeline.Value - newSliderValue) > 0.05)
            {
                SliderTimeline.Value = newSliderValue;
                if (SliderTimelineFS != null) SliderTimelineFS.Value = newSliderValue;
            }
            _isUpdatingFromPlayer = false;

            var now = DateTime.UtcNow;
            if ((now - _lastTimeUpdate).TotalMilliseconds >= 250)
            {
                _lastTimeUpdate = now;
                TimeSpan current = TimeSpan.FromMilliseconds(Math.Max(0, uiMs));
                TimeSpan total = TimeSpan.FromMilliseconds(durationMs);
                if (TxtCurrentTime != null) TxtCurrentTime.Text = current.ToString(@"hh\:mm\:ss");
                if (TxtTotalTime != null) TxtTotalTime.Text = total.ToString(@"hh\:mm\:ss");
                if (TxtCurrentTimeFS != null) TxtCurrentTimeFS.Text = current.ToString(@"hh\:mm\:ss");
                if (TxtTotalTimeFS != null) TxtTotalTimeFS.Text = total.ToString(@"hh\:mm\:ss");
            }
        }

        private void TimelineTimer_Tick(object? sender, EventArgs e)
        {
            UpdateTimelineFromPlayback();
        }

        private void TimelineRendering(object? sender, EventArgs e)
        {
            // Primary driver: CompositionTarget.Rendering fires on every render frame.
            // This is far more reliable than DispatcherTimer for continuous wall-time progress
            // when the UI thread or dispatcher is under load from video decoding/rendering.
            UpdateTimelineFromPlayback();
        }

        // --- Independent UI clock for progress bar + displayed time (wall time driven) ---
        private double GetUiClockMs()
        {
            if (_uiClock.IsRunning)
                return _uiClockBaseMs + _uiClock.Elapsed.TotalMilliseconds * _uiClockSpeed;
            return _uiClockBaseMs;
        }

        private void StartUiClock(double baseMs)
        {
            _uiClockBaseMs = Math.Max(0, baseMs);
            _uiClockSpeed = _currentSpeed;
            _uiClock.Restart();

            // Use a threadpool System.Timers.Timer + BeginInvoke for reliable steady updates.
            // Critical for audio-only (MP3 etc.): WPF CompositionTarget fires infrequently on static UIs
            // (album art), and even DispatcherTimer can get delayed. The wall time (Stopwatch) is correct;
            // the problem was delivery of the value to Slider/Text.
            EnsurePreciseTimelineTimer();

            // Keep CompositionTarget as bonus when video is actively rendering (keeps composition hot)
            if (_decoder != null && _decoder.HasVideo && !_timelineRenderingHooked)
            {
                CompositionTarget.Rendering += TimelineRendering;
                _timelineRenderingHooked = true;
            }

            if (!_timelineTimer.IsEnabled)
                _timelineTimer.Start(); // fallback
        }

        private void PauseUiClock()
        {
            if (_uiClock.IsRunning)
            {
                _uiClockBaseMs = GetUiClockMs();
                _uiClock.Reset();
            }
            if (_timelineRenderingHooked)
            {
                CompositionTarget.Rendering -= TimelineRendering;
                _timelineRenderingHooked = false;
            }
            _timelineTimer.Stop();
            _preciseTimelineTimer?.Stop();
        }

        private void SyncUiClock(double mediaMs)
        {
            _uiClockBaseMs = Math.Max(0, mediaMs);
            _uiClock.Restart();
            if (_userWantsPlayback && _decoder != null && _decoder.IsRunning && !_decoder.IsPaused)
            {
                EnsurePreciseTimelineTimer();
                if (_decoder.HasVideo && !_timelineRenderingHooked)
                {
                    CompositionTarget.Rendering += TimelineRendering;
                    _timelineRenderingHooked = true;
                }
                if (!_timelineTimer.IsEnabled)
                    _timelineTimer.Start();
            }
        }

        private void EnsurePreciseTimelineTimer()
        {
            if (_preciseTimelineTimer == null)
            {
                _preciseTimelineTimer = new System.Timers.Timer(16); // ~60 Hz
                _preciseTimelineTimer.Elapsed += (s, e) =>
                {
                    // Post to UI thread at render priority. This runs from threadpool, independent of render activity.
                    Dispatcher.BeginInvoke(new Action(UpdateTimelineFromPlayback), DispatcherPriority.Render);
                };
                _preciseTimelineTimer.AutoReset = true;
            }
            if (!_preciseTimelineTimer.Enabled)
                _preciseTimelineTimer.Start();
        }

        private void SetUiClockSpeed(double newSpeed)
        {
            double current = GetUiClockMs();
            _uiClockBaseMs = Math.Max(0, current);
            _uiClock.Restart();
            _uiClockSpeed = newSpeed;
        }

        private void SubtitleTimer_Tick(object? sender, EventArgs e)
        {
            if (_decoder == null || !_decoder.IsRunning) return;

            if (_decoder.IsPaused) return;

            UpdateTimelineFromPlayback();   // uses independent UI wall clock for smooth bar/time

            // Subtitles and precise timing still use decoder's media clock (for A/V accuracy)
            double mediaClockMs = (_isUserDraggingSlider || _isSeeking) && _pendingSeekSubtitleMs >= 0.0
                ? _pendingSeekSubtitleMs
                : _decoder.GetCurrentTimeMs();
            TimeSpan current = TimeSpan.FromMilliseconds(mediaClockMs);
            TimeSpan total = TimeSpan.FromMilliseconds(_decoder.DurationSeconds * 1000.0);

            UpdateSubtitleAt((int)current.TotalMilliseconds);

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
        }

        private void UpdateSubtitleAt(int timeMs)
        {
            if (_subtitlesEnabled && _subtitleManager.HasSubtitles)
            {
                if (BtnTranslate.Visibility != Visibility.Visible)
                {
                    BtnTranslate.Visibility = Visibility.Visible;
                    if (BtnTranslateFS != null) BtnTranslateFS.Visibility = Visibility.Visible;
                }

                string subText = _subtitleManager.GetSubtitleText(timeMs);
                if (!string.IsNullOrEmpty(subText))
                {
                    string detectedLang = _subtitleManager.DetectLanguage();
                    string displayLang = _isTranslationEnabled ? (detectedLang == "KR" ? "EN" : "KR") : detectedLang;
                    if (BtnTranslate.Content?.ToString() != displayLang)
                    {
                        BtnTranslate.Content = displayLang;
                        if (BtnTranslateFS != null) BtnTranslateFS.Content = displayLang;
                    }

                    if (_isTranslationEnabled)
                    {
                        if (_translationCache.TryGetValue(subText, out string translated))
                        {
                            if (TxtSubtitle.Text != translated) TxtSubtitle.Text = translated;
                        }
                        else
                        {
                            if (TxtSubtitle.Text != subText) TxtSubtitle.Text = subText;
                            _ = FetchAndApplyTranslationAsync(subText, timeMs);
                        }
                    }
                    else
                    {
                        if (TxtSubtitle.Text != subText) TxtSubtitle.Text = subText;
                    }
                    SubtitleBorder.Visibility = Visibility.Visible;
                }
                else
                {
                    SubtitleBorder.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                if (BtnTranslate.Visibility == Visibility.Visible)
                {
                    BtnTranslate.Visibility = Visibility.Collapsed;
                    if (BtnTranslateFS != null) BtnTranslateFS.Visibility = Visibility.Collapsed;
                    _isTranslationEnabled = false;
                    BtnTranslate.Tag = null;
                    if (BtnTranslateFS != null) BtnTranslateFS.Tag = null;
                }
                SubtitleBorder.Visibility = Visibility.Collapsed;
            }
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
                if (_isPipMode) TogglePipMode();
                else ToggleFullscreen();
                e.Handled = true;
                return;
            }

            if (_isPipMode && e.ButtonState == MouseButtonState.Pressed)
            {
                BeginMainWindowDrag();
                e.Handled = true;
                return;
            }

            // MP3/audio-only: MouseLayer covers album art and toggled pause on any click.
            if (AudioUI != null && AudioUI.Visibility == Visibility.Visible)
            {
                if (e.ClickCount == 1 && _decoder != null)
                {
                    TogglePlayPause("audio-click");
                    e.Handled = true;
                }
                return;
            }

            if (e.ClickCount == 1 && _decoder != null)
            {
                TogglePlayPause("video-click");
                e.Handled = true;
            }
        }

        private void MainGrid_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            SetPipHoverOverlayVisible(true);
        }

        private void MainGrid_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            SetPipHoverOverlayVisible(false);
        }

        private bool ShouldAutoHideCursor()
            => _isFullscreen || (WindowState == WindowState.Maximized && !_isPipMode);

        private void NotifyMouseActivity()
        {
            if (this.Cursor != System.Windows.Input.Cursors.Arrow) this.Cursor = System.Windows.Input.Cursors.Arrow;
            if (_overlayWindow != null && _overlayWindow.Cursor != System.Windows.Input.Cursors.Arrow) _overlayWindow.Cursor = System.Windows.Input.Cursors.Arrow;
            if (_videoHwndHost != null && _videoHwndHost.HideCursor) _videoHwndHost.HideCursor = false;

            if (!ShouldAutoHideCursor()) return;

            _cursorHideTimer.Stop();
            _cursorHideTimer.Start();
        }

        private void RestoreVisibleCursor()
        {
            this.Cursor = System.Windows.Input.Cursors.Arrow;
            if (_overlayWindow != null) _overlayWindow.Cursor = System.Windows.Input.Cursors.Arrow;
            if (_videoHwndHost != null) _videoHwndHost.HideCursor = false;
            _cursorHideTimer.Stop();
        }

        private void OverlayWindow_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            NotifyMouseActivity();
        }

        private bool TryGetVideoGridMousePos(out Point pos, out double w, out double h)
        {
            pos = default;
            w = h = 0;
            try
            {
                if (VideoGrid == null) return false;
                w = VideoGrid.ActualWidth;
                h = VideoGrid.ActualHeight;
                if (w <= 0 || h <= 0) return false;
                pos = Mouse.GetPosition(VideoGrid);
                return pos.X >= 0 && pos.X <= w && pos.Y >= 0 && pos.Y <= h;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsInFsBottomHotZone(Point pos, double h) => pos.Y > h - FsBottomHotZonePx;

        private static bool IsInFsTopHotZone(Point pos) => pos.Y < FsTopHotZonePx;

        private void UpdateFsChromeVisibility(bool forceHideChrome = false)
        {
            if (!_isFullscreen) return;

            if (!TryGetVideoGridMousePos(out Point pos, out _, out double h))
            {
                if (forceHideChrome)
                {
                    if (!_isMouseOverFsExitBadge) HideFsExitBadge();
                    if (!_isMouseOverFsStrip) HideFsBottomStrip();
                }
                return;
            }

            bool inBottomZone = IsInFsBottomHotZone(pos, h);
            bool inTopZone = IsInFsTopHotZone(pos);
            bool keepBottom = _isMouseOverFsStrip || inBottomZone;
            bool keepTop = _isMouseOverFsExitBadge || inTopZone;

            if (keepTop && !forceHideChrome)
            {
                ShowFsExitBadge();
            }
            else if (!keepTop)
            {
                HideFsExitBadge();
            }

            if (keepBottom && !forceHideChrome)
            {
                ShowFsBottomStrip();
            }
            else if (!keepBottom)
            {
                HideFsBottomStrip();
            }
        }

        // 전체화면 마우스 위치 추적 및 팝업 표시
        private void FsMousePollTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isFullscreen) return;

            try
            {
                Point pos = Mouse.GetPosition(VideoGrid);
                bool mouseMoved = double.IsNaN(_lastPolledMousePos.X)
                    || Math.Abs(pos.X - _lastPolledMousePos.X) > 0.5
                    || Math.Abs(pos.Y - _lastPolledMousePos.Y) > 0.5;
                if (mouseMoved)
                {
                    _lastPolledMousePos = pos;
                    NotifyMouseActivity();
                }
                else if (_isMouseOverFsStrip || _isMouseOverFsExitBadge)
                {
                    // Hovering on chrome without moving still counts as activity.
                    NotifyMouseActivity();
                }

                UpdateFsChromeVisibility();
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

        private void HideFsBottomStrip()
        {
            if (_isFullscreen)
            {
                FsBottomStrip.Visibility = Visibility.Collapsed;
            }
        }

        private void RestartFsHideTimer()
        {
            // Auto hide timer is handled by mouse position polling
        }

        private void FsExitBadge_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _isMouseOverFsExitBadge = true;
            NotifyMouseActivity();
            ShowFsExitBadge();
        }

        private void FsExitBadge_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _isMouseOverFsExitBadge = false;
            UpdateFsChromeVisibility();
        }

        private void FsBottomStrip_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _isMouseOverFsStrip = true;
            NotifyMouseActivity();
            ShowFsBottomStrip();
        }

        private void FsBottomStrip_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _isMouseOverFsStrip = false;
            UpdateFsChromeVisibility();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void BtnMaximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnClosePip_Click(object sender, RoutedEventArgs e) => Close();

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
                SetBrush("KeyTextBrush", 0x00, 0x40, 0xA0); // 더 진한 파란색으로 단축키 텍스트 표시

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
                SetBrush("KeyTextBrush", 0x1D, 0xB9, 0x54); // 기존 다크모드 초록색

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
        private void BtnOpenUrl_Click(object sender, RoutedEventArgs e) => OpenUrl();

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

        // === PLAYLIST PREV/NEXT NAV PROTECTION ZONE ===
        // Core contract (do not accidentally break when touching playlist nav, PlayFile, or finished callbacks):
        // - All user-initiated prev/next/double-click/enter/delete-next etc. MUST route through
        //   NavigateToPlaylistIndex (which handles pending + highlight + decide whether to PlayFile).
        // - _playlistIndex updated *immediately* for responsive UI/highlight/title.
        // - If load in progress (_isOpeningFile), record _pendingPlaylistTarget, bump _openGeneration
        //   (aborts in-flight load before it commits), update highlight, *do not* call PlayFile yet.
        // - TryApplyPendingPlaylistTarget is the single point that drains/starts a pending target.
        //   It is called from: PlayFile finally (on success), abort mismatch paths (after release _isOpening),
        //   and finished handler (when suppressing auto-next).
        // - Direct mutations of _playlistIndex (delete shift, move reorder) MUST be followed by
        //   UpdateNowPlayingHighlight() and must not leave pending inconsistent.
        // - Index normalization + boundary guards in PlayPrev/PlayNext.
        // - Combined with openGeneration + captured-gen PlaybackFinished + early gen checks in PlayFile
        //   this prevents overlapping loads, stale auto-advance fighting user nav, and state desync.
        // This ensures prev/next actually switches the video reliably and stably.
        // DO NOT break this zone without updating call sites + this comment (like SEEK UX PROTECTION).

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
            NotifyMouseActivity();
        }

        private void Window_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
            var files = e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            
            var allFiles = new System.Collections.Generic.List<string>();
            foreach (var f in files)
            {
                if (System.IO.Directory.Exists(f))
                {
                    try
                    {
                        allFiles.AddRange(System.IO.Directory.GetFiles(f, "*.*", System.IO.SearchOption.AllDirectories));
                    }
                    catch { /* ignore access exceptions */ }
                }
                else
                {
                    allFiles.Add(f);
                }
            }
            LoadPlaylist(allFiles.ToArray());
        }

        private string? _currentFilePath;
        private bool _isOpeningFile = false;
        // Which openGeneration currently owns the _isOpeningFile lock (0 = none).
        // Prevents a superseded PlayFile from leaving the flag stuck true, which blocks
        // all subsequent playlist auto-advance / prev / next (pending never drains).
        private int _openingOwnedByGen = 0;
        private volatile int _currentPlaybackOpenGen = 0;
        private Action? _activePlaybackFinishedHandler;
        // === PLAYLIST RAPID NAV + FINISH RACE PROTECTION ===
        // Rapid previous after playing several playlist items in sequence could cause:
        // - Stale Decoder_PlaybackFinished (from ended/abandoned item) to call PlayNext and overlap PlayFile
        // - Concurrent dispose of decoder/renderer + new creation -> crash / DisplayFPS=0
        // Protections: captured 'finishedGen' per subscription, early gen checks before destructive switch,
        // bumping _openGeneration from Play* to abort superseded loads, _currentPlaybackOpenGen + live checks.
        // New approach per user: Do NOT process every rapid press as separate loads (unstable).
        // Accept user input immediately (update highlight + pending), but only *act* (PlayFile) after the
        // previous internal process (Open/decoder/renderer init) has fully settled. Coalesces rapid presses.
        // Opening lock ownership: each PlayFile stamps _openingOwnedByGen; only that gen may clear
        // _isOpeningFile. finally always releases if still owned and then drains _pendingPlaylistTarget.
        private int? _pendingPlaylistTarget = null;
        private YouTubeStreamingService _streamingService = new YouTubeStreamingService();
        private bool _isYoutubeDownloadInProgress;

        private void PlayPrev()
        {
            if (_playlist.Count == 0) return;

            // Safety: ensure index is valid before computing prev target.
            if (_playlistIndex < 0 || _playlistIndex >= _playlist.Count)
            {
                _playlistIndex = 0;
            }

            int target;
            if (_isShuffle)
            {
                if (_playlist.Count > 1)
                {
                    int nextIndex;
                    do {
                        nextIndex = Random.Shared.Next(_playlist.Count);
                    } while (nextIndex == _playlistIndex);
                    target = nextIndex;
                }
                else
                {
                    target = _playlistIndex;
                }
            }
            else if (_playlistIndex > 0)
            {
                target = _playlistIndex - 1;
            }
            else if (_isRepeat)
            {
                target = _playlist.Count - 1;
            }
            else
            {
                return;
            }

            // Use the navigator: accepts rapid inputs (updates UI + pending) but never starts
            // a new PlayFile while another is opening. Internal load process gets to complete.
            NavigateToPlaylistIndex(target);
        }

        private void BtnPrev_Click(object sender, RoutedEventArgs e) => PlayPrev();
        private void BtnNext_Click(object sender, RoutedEventArgs e) => PlayNext();

        private bool PlayNext()
        {
            if (_playlist.Count == 0) return false;

            // Safety: ensure index is valid before computing next target.
            if (_playlistIndex < 0 || _playlistIndex >= _playlist.Count)
            {
                _playlistIndex = 0;
            }

            int target;
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
                    target = nextIndex;
                }
                else
                {
                    target = _playlistIndex;
                }
            }
            else if (_playlistIndex < _playlist.Count - 1)
            {
                target = _playlistIndex + 1;
            }
            else if (_isRepeat)
            {
                target = 0;
            }
            else
            {
                return false;
            }

            // Centralized navigator: coalesces rapid presses. Internal process (decoder open,
            // renderer setup, etc.) completes before acting on next user request.
            NavigateToPlaylistIndex(target);
            return true;
        }

        /// <summary>
        /// Centralized playlist navigation request.
        /// - Always updates highlight and _playlistIndex immediately (responsive UI).
        /// - If a load is in progress, records as _pendingPlaylistTarget and does NOT start overlapping PlayFile.
        /// - The pending target is applied only after the current PlayFile fully settles (in finally) or
        ///   when a natural PlaybackFinished occurs and we decide not to auto-advance.
        /// This prioritizes internal stability (one PlayFile/Open/Dispose cycle at a time) over blindly
        /// honoring every rapid button press as a separate action.
        /// </summary>
        private void NavigateToPlaylistIndex(int targetIndex)
        {
            if (targetIndex < 0 || targetIndex >= _playlist.Count || _playlist.Count == 0)
                return;

            if (!_isOpeningFile)
            {
                _pendingPlaylistTarget = null;
                _playlistIndex = targetIndex;
                UpdateNowPlayingHighlight();
                PlayFile(_playlist[targetIndex].Path);
                return;
            }

            // Busy with a previous internal load (decoder Open + renderer creation etc.).
            // Accept the user's input (update desired target + UI), bump generation so the
            // *current* in-flight load aborts before it fully presents/attaches (avoids flicker
            // to wrong intermediate item), but do NOT launch another PlayFile now.
            // When the aborted load's continuation hits its gen-mismatch returns, it will release
            // the flag and TryApplyPending will launch the final desired target cleanly.
            Interlocked.Increment(ref _openGeneration);
            _pendingPlaylistTarget = targetIndex;
            _playlistIndex = targetIndex;
            UpdateNowPlayingHighlight();
            // Internal process gets priority; next input will be accepted after this one settles/aborts.
        }

        private void TryApplyPendingPlaylistTarget()
        {
            if (!_pendingPlaylistTarget.HasValue)
                return;

            int target = _pendingPlaylistTarget.Value;
            _pendingPlaylistTarget = null;

            if (target >= 0 && target < _playlist.Count)
            {
                // Always start the load for a pending target when applying (e.g. after aborting an
                // intermediate load due to newer nav). The optimistic _playlistIndex may already
                // be set, but we still need to launch PlayFile for it now that previous process ended.
                _playlistIndex = target;
                if (_isOpeningFile)
                {
                    // Rare: re-pend it so a later apply or the current's abort path will pick it.
                    _pendingPlaylistTarget = target;
                }
                else
                {
                    PlayFile(_playlist[target].Path);
                }
                UpdateNowPlayingHighlight();
            }
        }

        /// <summary>
        /// Release the PlayFile opening lock only if <paramref name="openGeneration"/> still owns it.
        /// Safe when a newer PlayFile has already taken ownership.
        /// </summary>
        private void ReleaseOpeningIfOwned(int openGeneration)
        {
            if (Volatile.Read(ref _openingOwnedByGen) == openGeneration)
            {
                Volatile.Write(ref _openingOwnedByGen, 0);
                _isOpeningFile = false;
            }
        }

        /// <summary>
        /// End of an open attempt: drop our lock if we still hold it, then start any pending nav.
        /// Critical when NavigateToPlaylistIndex bumped gen while we were busy — without this,
        /// _isOpeningFile stays true forever and playlist next/prev only update the highlight.
        /// </summary>
        private void CompleteOpenAttempt(int openGeneration)
        {
            ReleaseOpeningIfOwned(openGeneration);
            // Drain pending whenever nothing is actively opening. Covers: success, abort, and
            // "superseded by Navigate-only (pending set, no new PlayFile yet)".
            if (!_isOpeningFile)
                TryApplyPendingPlaylistTarget();
        }

        private void Decoder_PlaybackFinished()
        {
            // Legacy no-arg path (kept for safety). Treat as possibly-stale; real protection uses captured-gen path.
            Decoder_PlaybackFinishedCaptured(Volatile.Read(ref _openGeneration));
        }

        private void Decoder_PlaybackFinishedCaptured(int finishedGen)
        {
            // Use BeginInvoke (async) instead of Invoke (sync) to prevent deadlock
            // when decoder thread waits for UI and UI waits for decoder.Stop()/Join()
            Dispatcher.BeginInvoke(new Action(() =>
            {
                int liveGen = Volatile.Read(ref _openGeneration);
                // Key hardening for rapid prev after sequential playlist playback:
                // Only the finish whose captured gen exactly matches the last *successfully started*
                // playback is allowed to trigger auto-PlayNext. Late finishes from videos that were
                // abandoned via Prev (or fast nav) will have mismatched gen and are ignored.
                // This prevents stale PlaybackFinished from starting overlapping PlayFile while
                // user is mashing previous (or during finish+prev races), which led to concurrent
                // dispose/renderer/decoder creation and crashes (incl. DisplayFPS=0 symptoms).
                if (finishedGen != _currentPlaybackOpenGen || finishedGen != liveGen)
                {
                    return;
                }
                _userWantsPlayback = false;
                UpdatePlayPauseUI(false);

                bool hasPendingUserNav = _pendingPlaylistTarget.HasValue;

                if (!hasPendingUserNav && PlayNext())
                {
                    // Normal auto-advance: PlayNext → NavigateToPlaylistIndex may either start
                    // PlayFile immediately or only record pending if a load is in progress.
                    // If only pending was set, drain it now that this playback is done.
                    if (_pendingPlaylistTarget.HasValue && !_isOpeningFile)
                        TryApplyPendingPlaylistTarget();
                }
                else
                {
                    // Either end of list/repeat off, or user has expressed explicit pending navigation
                    // (rapid prev after sequential play). Prioritize user's last input over auto-next.
                    // Do not start conflicting PlayFile here.
                    StopStreamingLoadingBlink();
                    if (VideoViewbox != null) VideoViewbox.Visibility = Visibility.Collapsed;
                    if (AudioUI != null) AudioUI.Visibility = Visibility.Collapsed;
                    if (ImgSplash != null) ImgSplash.Visibility = Visibility.Visible;
                    
                    _currentPlaybackOpenGen = 0;
                    _activePlaybackFinishedHandler = null;
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

                    PauseUiClock();
                    _uiClockBaseMs = 0.0;
                    
                    SetRandomVibe();
                    Title = "JonPlayer";

                    if (_decoder != null)
                    {
                        var oldDec = _decoder;
                        _decoder = null;
                        oldDec.Stop();
                        DetachDecoderEvents(oldDec);
                        DisposeRendererSafe();
                        DisposeDecoderInBackground(oldDec);
                    }

                    // Apply any pending user navigation now that this playback has ended cleanly
                    // and we avoided auto-advance. This is the "process after previous is done" point.
                    TryApplyPendingPlaylistTarget();
                }
            }));
        }

        private void DetachDecoderEvents(FFmpegMediaDecoder decoder)
        {
            if (decoder == null) return;
            if (_activePlaybackFinishedHandler != null)
            {
                decoder.PlaybackFinished -= _activePlaybackFinishedHandler;
                _activePlaybackFinishedHandler = null;
            }
            decoder.PlaybackFinished -= Decoder_PlaybackFinished;
            decoder.FrameDecoded -= Decoder_FrameDecoded;
            decoder.AudioDataAvailable -= Decoder_AudioDataAvailable;
            decoder.PositionChanged -= Decoder_PositionChanged;
            decoder.RotationDetected -= Decoder_RotationDetected;
            decoder.SeekInitiated -= Decoder_SeekInitiated;
            decoder.SeekPerformed -= Decoder_SeekPerformed;
        }

        private void DisposeRendererSafe()
        {
            if (_renderer == null)
            {
                return;
            }
            try
            {
                // Stop frame pulls before joining render thread / freeing D3D.
                _renderer.DetachDecoder();
                _renderer.PrepareForDecoderTeardown();
                _renderer.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Error("DisposeRendererSafe failed", ex);
            }
            finally
            {
                _renderer = null;
            }
        }

        /// <summary>
        /// Safe order for next-track / close:
        /// 1) Detach render from decoder (no more PullVideoFrame)
        /// 2) Drop held frames while decoder native memory still valid
        /// 3) Stop + dispose decoder (frees SW pools / D3D11VA)
        /// 4) Dispose renderer (join render thread, free device)
        /// Wrong order (dispose decoder while render still maps NV12/HW) → AV 0xC0000005.
        /// </summary>
        private async Task TeardownActivePlaybackAsync(FFmpegMediaDecoder decoder)
        {
            DetachDecoderEvents(decoder);

            var renderer = _renderer;
            if (renderer != null)
            {
                try
                {
                    renderer.DetachDecoder();
                    renderer.PrepareForDecoderTeardown(); // black + free held frames while decoder alive
                }
                catch (Exception ex)
                {
                    Logger.Error("Renderer detach/teardown prep failed", ex);
                }
            }

            try
            {
                decoder.Stop();
            }
            catch (Exception ex)
            {
                Logger.Error("Decoder Stop failed during teardown", ex);
            }

            await DisposeDecoderAsync(decoder);

            // Renderer dispose after decoder: in-flight GPU copies already finished in Prepare + Join.
            if (renderer != null && ReferenceEquals(_renderer, renderer))
            {
                DisposeRendererSafe();
            }
            else if (renderer != null)
            {
                try { renderer.Dispose(); } catch { /* superseded */ }
            }
        }

        private void AbortDecoderLoad(FFmpegMediaDecoder decoder)
        {
            DetachDecoderEvents(decoder);
            DisposeRendererSafe();
            try { decoder.Dispose(); } catch { /* ignore */ }
        }

        private static Task DisposeDecoderAsync(FFmpegMediaDecoder decoder)
        {
            return Task.Run(() =>
            {
                try
                {
                    decoder.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Error("Failed to dispose decoder", ex);
                }
            });
        }

        private static void DisposeDecoderInBackground(FFmpegMediaDecoder decoder)
        {
            _ = DisposeDecoderAsync(decoder);
        }

        private async void PlayFile(string path, double startRatio = 0.0)
        {
            if (_isClosing) return;

            // Apply current user HW/SW preference (F4 toggle) before every open. Keeps existing fallback logic inside decoder.
            FFmpegMediaDecoder.EnableHwAccel = !_forceSoftwareDecode;

            bool isUrl = IsStreamingPath(path);
            string displayName = GetNowPlayingDisplayName(path);
            int openGeneration = Interlocked.Increment(ref _openGeneration);
            if (!isUrl || startRatio <= 0.0)
            {
                Interlocked.Increment(ref _streamingSeekGeneration);
            }
            _isOpeningFile = true;
            Volatile.Write(ref _openingOwnedByGen, openGeneration);
            _isSeeking = false;
            _pendingSeekSubtitleMs = -1.0;
            _lastUserSeekTargetMs = -1.0;
            _allowTimelineBackward = true;

            PauseUiClock();
            _uiClockBaseMs = startRatio * 1000.0; // will be corrected in StartUiClock after decoder ready

            Dispatcher.Invoke(() =>
            {
                _isUpdatingFromPlayer = true;
                double sliderPos = startRatio * 1000.0;
                if (SliderTimeline != null) SliderTimeline.Value = sliderPos;
                if (SliderTimelineFS != null) SliderTimelineFS.Value = sliderPos;
                TimeSpan startTime = TimeSpan.FromSeconds(startRatio * (_decoder?.DurationSeconds ?? 0.0));
                if (TxtCurrentTime != null) TxtCurrentTime.Text = startTime.ToString(@"hh\:mm\:ss");
                if (TxtCurrentTimeFS != null) TxtCurrentTimeFS.Text = startTime.ToString(@"hh\:mm\:ss");
                _isUpdatingFromPlayer = false;
            });

            FFmpegMediaDecoder? newDecoder = null;
            try
            {
                _currentFilePath = path;

                // If this PlayFile corresponds to the item the user last requested via rapid nav,
                // consume the pending so we don't re-apply it after we settle.
                if (_playlistIndex >= 0 && _playlistIndex < _playlist.Count &&
                    _playlist[_playlistIndex].Path == path)
                {
                    _pendingPlaylistTarget = null;
                }

                bool canUseCcButton = !isUrl || !string.IsNullOrEmpty(GetCurrentYoutubeUrl(path));
                if (BtnWhisper != null) BtnWhisper.IsEnabled = canUseCcButton;
                if (BtnWhisperFS != null) BtnWhisperFS.IsEnabled = canUseCcButton;
                _openCount++;
                _audioUnderrunCount = 0;
                _lastAutoGainDb = 0.0;
                _lastDetectedMeanDb = double.NaN;

                TxtNowPlaying.Text = isUrl ? $"Loading... {displayName}" : "Loading...";
                
                if (ImgSplash != null) ImgSplash.Visibility = Visibility.Visible;
                if (isUrl) StartStreamingLoadingBlink();
                else StopStreamingLoadingBlink();

                // 이전 재생 즉시 정지 (오디오 + 자막 + AI 추출)
                // CRITICAL: do NOT ResetFrameResources / free decoder native pools while the
                // render thread may still PullVideoFrame — that caused 0xC0000005 on next-track.
                _waveOut?.Stop();
                try { _waveProvider?.ClearBuffer(); } catch { /* ignore */ }
                _subtitleManager.Clear();
                _subtitlesEnabled = false;
                Interlocked.Increment(ref _subtitleLoadGeneration);
                if (TxtSubtitle != null) TxtSubtitle.Text = "";
                if (SubtitleBorder != null) SubtitleBorder.Visibility = Visibility.Collapsed;
                CancelWhisperExtraction();

                var oldDecoder = _decoder;
                _decoder = null;
                
                if (oldDecoder != null)
                {
                    await TeardownActivePlaybackAsync(oldDecoder);
                }

                // Abort superseded switch (e.g. rapid prev/next during previous PlayFile's async window).
                // Prevents this continuation from disposing or overwriting renderer/decoder created
                // by a later user nav intent.
                if (openGeneration != Volatile.Read(ref _openGeneration))
                {
                    // Superseded by later user nav while we were disposing old. Release so the final
                    // desired target can be started cleanly by TryApply (called below or from other paths).
                    _activePlaybackFinishedHandler = null;
                    _currentPlaybackOpenGen = 0;
                    CompleteOpenAttempt(openGeneration);
                    return;
                }

                newDecoder = new FFmpegMediaDecoder();
                newDecoder.SetSpeed(_currentSpeed);

                // Re-check before touching shared renderer (created here and tied to this gen's decoder for HW).
                // A racing higher-gen PlayFile (rapid prev) may have already swapped renderer for its target.
                if (openGeneration != Volatile.Read(ref _openGeneration))
                {
                    newDecoder.Dispose();
                    _activePlaybackFinishedHandler = null;
                    _currentPlaybackOpenGen = 0;
                    CompleteOpenAttempt(openGeneration);
                    return;
                }

                // Teardown already disposed the previous renderer. If anything remains, join safely.
                if (_renderer != null)
                {
                    DisposeRendererSafe();
                }
                
                if (_videoHwndHost != null)
                {
                    _renderer = new D3D11VideoRenderer(_videoHwndHost.Handle, newDecoder);
                    _renderer.StretchMode = _currentVideoStretch;
                    if (_isEnhancedShaderEnabled)
                        _renderer.EnableEnhancedShader(true);
                    _renderer.ClearDisplay(); // black before first decoded frame
                    newDecoder.SetD3D11Device(_renderer.D3D11DevicePtr, _renderer.D3D11ContextPtr);
                }
                newDecoder.FrameDecoded += Decoder_FrameDecoded;
                newDecoder.AudioDataAvailable += Decoder_AudioDataAvailable;
                newDecoder.PositionChanged += Decoder_PositionChanged;
                // Use captured-gen handler so that PlaybackFinished carries the generation of THIS
                // playback. Rapid previous (or next) after sequential play would otherwise let stale
                // finish from abandoned file pass the guard and start conflicting PlayFile.
                int capturedGenForFinish = openGeneration;
                Action finishHandler = () => Decoder_PlaybackFinishedCaptured(capturedGenForFinish);
                newDecoder.PlaybackFinished += finishHandler;
                _activePlaybackFinishedHandler = finishHandler;
                newDecoder.RotationDetected += Decoder_RotationDetected;
                newDecoder.SeekInitiated += Decoder_SeekInitiated;
                newDecoder.SeekPerformed += Decoder_SeekPerformed;
                newDecoder.GetAudioBufferedDurationMs = () => _waveProvider?.BufferedDuration.TotalMilliseconds ?? 0;
                newDecoder.GetAudioHardwareLatencyMs = () => _waveOut?.DesiredLatency ?? 0;

                Dispatcher.Invoke(() => {
                    if (VideoRotation != null) VideoRotation.Angle = 0;
                });

                // YouTube adaptive: 별도 오디오 URL이 있으면 함께 전달
                string? separateAudioUrl = null;
                if (_playlistIndex >= 0 && _playlistIndex < _playlist.Count && _playlist[_playlistIndex].Path == path)
                {
                    separateAudioUrl = _playlist[_playlistIndex].AudioPath;
                }
                if (separateAudioUrl == null && isUrl)
                {
                    separateAudioUrl = _streamingService?.LastAudioUrl;
                }
                await Task.Run(() => newDecoder.Open(path, separateAudioUrl, isUrl ? startRatio : 0.0));

                if (_isClosing)
                {
                    AbortDecoderLoad(newDecoder);
                    _currentPlaybackOpenGen = 0;
                    CompleteOpenAttempt(openGeneration);
                    return;
                }

                // 오디오 전용(MP3 등) 파일의 경우 불필요한 D3D11 renderer 생성 방지 (메모리 절약)
                if (!newDecoder.HasVideo && _renderer != null)
                {
                    _renderer.Dispose();
                    _renderer = null;
                }

                if (openGeneration != Volatile.Read(ref _openGeneration))
                {
                    AbortDecoderLoad(newDecoder);
                    _currentPlaybackOpenGen = 0;
                    CompleteOpenAttempt(openGeneration);
                    return;
                }

                
                _decoder = newDecoder;
                _currentPlaybackOpenGen = openGeneration;
                InitAudioPlayer();

                if (_isClosing || openGeneration != Volatile.Read(ref _openGeneration))
                {
                    AbortDecoderLoad(newDecoder);
                    _decoder = null;
                    _currentPlaybackOpenGen = 0;
                    CompleteOpenAttempt(openGeneration);
                    return;
                }
                
                if (!isUrl && _decoder.DurationSeconds > 10.0)
                {
                    SetVolumeSlider(_userPreferredVolume);
                    _ = RunRandomIntervalVolumeScanAsync(path, _decoder.DurationSeconds);
                }
                
                Dispatcher.Invoke(() => {
                    if (VideoElement != null && _decoder != null)
                    {
                        VideoElement.Width = _decoder.Width > 0 ? _decoder.Width : 1920;
                        VideoElement.Height = _decoder.Height > 0 ? _decoder.Height : 1080;
                    }
                });
                
                _renderer?.ResetPresentationPacing();
                _renderer?.ClearDisplay();
                try { _waveProvider?.ClearBuffer(); } catch { /* ignore */ }
                _userWantsPlayback = true;
                _decoder.Play();

                double initMs = startRatio > 0.0 ? startRatio * (_decoder.DurationSeconds * 1000.0) : 0.0;
                StartUiClock(initMs);
                // WaveOut may start; decoder still suppresses PCM until first video frame is shown.
                _waveOut?.Play();

                if (startRatio > 0.0 && !isUrl)
                {
                    _decoder.Seek(startRatio);
                }

                Dispatcher.Invoke(() => {
                    _isSeeking = false;
                    _pendingSeekSubtitleMs = -1.0;
                    _lastUserSeekTargetMs = -1.0;
                    _allowTimelineBackward = true;
                    _isUpdatingFromPlayer = true;
                    double sliderPos = startRatio * 1000.0;
                    if (SliderTimeline != null)
                    {
                        SliderTimeline.Value = sliderPos;
                        SliderTimeline.IsEnabled = true;
                        SliderTimeline.IsHitTestVisible = true;
                    }
                    if (SliderTimelineFS != null)
                    {
                        SliderTimelineFS.Value = sliderPos;
                        SliderTimelineFS.IsEnabled = true;
                        SliderTimelineFS.IsHitTestVisible = true;
                    }
                    double durationSec = _decoder?.DurationSeconds ?? 0.0;
                    TimeSpan startTime = TimeSpan.FromSeconds(startRatio * durationSec);
                    if (TxtCurrentTime != null) TxtCurrentTime.Text = startTime.ToString(@"hh\:mm\:ss");
                    if (TxtCurrentTimeFS != null) TxtCurrentTimeFS.Text = startTime.ToString(@"hh\:mm\:ss");
                    if (TxtTotalTime != null) TxtTotalTime.Text = TimeSpan.FromSeconds(durationSec).ToString(@"hh\:mm\:ss");
                    if (TxtTotalTimeFS != null) TxtTotalTimeFS.Text = TimeSpan.FromSeconds(durationSec).ToString(@"hh\:mm\:ss");
                    _isUpdatingFromPlayer = false;

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

                    string? currentYoutubeUrl = GetCurrentYoutubeUrl(path);
                    if (!string.IsNullOrEmpty(currentYoutubeUrl))
                    {
                        _ = LoadStreamingSubtitlesAsync(currentYoutubeUrl, path, false);
                    }
                    else
                    {
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

                                    BlinkSubtitleButtons();

                                    break;
                                }
                            }
                        }
                    }
                });

                var name = displayName;
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
                    StopStreamingLoadingBlink();
                    if (VideoViewbox != null) VideoViewbox.Visibility = Visibility.Visible;
                    if (ImgSplash != null) ImgSplash.Visibility = Visibility.Collapsed;
                    if (AudioUI != null) AudioUI.Visibility = Visibility.Collapsed;
                    if (MouseLayer != null) MouseLayer.IsHitTestVisible = true;
                }
                else
                {
                    StopStreamingLoadingBlink();
                    if (VideoViewbox != null) VideoViewbox.Visibility = Visibility.Collapsed;
                    if (ImgSplash != null) ImgSplash.Visibility = Visibility.Collapsed;
                    if (AudioUI != null) AudioUI.Visibility = Visibility.Visible;
                    if (MouseLayer != null) MouseLayer.IsHitTestVisible = true;

                    try
                    {
                        var tfile = TagLib.File.Create(path);
                        TxtAudioTitle.Text = RepairLegacyKoreanTagText(tfile.Tag.Title, name);
                        
                        string artist = string.Join(", ", tfile.Tag.Performers.Select(p => RepairLegacyKoreanTagText(p, string.Empty)).Where(p => !string.IsNullOrWhiteSpace(p)));
                        TxtAudioArtist.Text = string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist;
                        
                        TxtAudioAlbum.Text = RepairLegacyKoreanTagText(tfile.Tag.Album, "Unknown Album");

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
                UpdateWindowSizePresetUi();

                // Final supersession check after the long post-Play UI setup window. If the user
                // navigated away during Dispatcher.Invoke / subtitle / tag work, abandon this open
                // so we do not leave a mismatched gen while claiming success.
                if (_isClosing || openGeneration != Volatile.Read(ref _openGeneration))
                {
                    if (_decoder == newDecoder)
                    {
                        _decoder = null;
                        _currentPlaybackOpenGen = 0;
                        AbortDecoderLoad(newDecoder);
                        newDecoder = null;
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                if (newDecoder != null)
                {
                    AbortDecoderLoad(newDecoder);
                    if (_decoder == newDecoder)
                        _decoder = null;
                }
                else
                {
                    DisposeRendererSafe();
                }
                if (openGeneration == Volatile.Read(ref _openGeneration))
                {
                    WpfMessageBox.Show($"파일을 열 수 없습니다.\n{ex.Message}", "JonPlayer", MessageBoxButton.OK, MessageBoxImage.Warning);
                    _pendingPlaylistTarget = null;
                }
            }
            finally
            {
                if (openGeneration == Volatile.Read(ref _openGeneration))
                {
                    StopStreamingLoadingBlink();

                    // Reset wall clock elapsed exactly when the file becomes ready (after decoder/renderer init).
                    // This prevents any tiny advance that accumulated between StartUiClock and here
                    // (updates were anyway blocked by _isOpeningFile). Especially relevant for
                    // F4 HW<->SW reloads which go through PlayFile.
                    if (_userWantsPlayback && _decoder != null)
                    {
                        _uiClock.Restart();
                    }
                }

                // Always release our opening ownership (if we still hold it) and drain pending nav.
                // Previously, a gen mismatch in finally left _isOpeningFile stuck true forever, so
                // playlist auto-next / manual next only updated the highlight and never called PlayFile.
                CompleteOpenAttempt(openGeneration);
            }
        }

        private static bool TryParseVolumeDetect(string stderr, out double meanDb, out double maxDb)
        {
            meanDb = -99.0;
            maxDb = -99.0;

            var meanMatch = System.Text.RegularExpressions.Regex.Match(stderr, @"mean_volume:\s*([-\d\.]+)\s*dB");
            if (meanMatch.Success)
            {
                double.TryParse(meanMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out meanDb);
            }

            var maxMatch = System.Text.RegularExpressions.Regex.Match(stderr, @"max_volume:\s*([-\d\.]+)\s*dB");
            if (maxMatch.Success)
            {
                double.TryParse(maxMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out maxDb);
            }

            return meanDb > -90.0 || maxDb > -90.0;
        }

        private async Task RunRandomIntervalVolumeScanAsync(string filePath, double durationSeconds)
        {
            int scanGeneration = Volatile.Read(ref _openGeneration);

            try
            {
                if (_decoder == null || _isClosing) return;

                string ffmpegPath = await YouTubeStreamingService.EnsureFFmpegCliAsync();
                
                int numSamples = 5;
                double sampleDuration = 5.0;
                if (durationSeconds < 30.0)
                {
                    numSamples = 1;
                    sampleDuration = durationSeconds;
                }

                var random = new Random();
                var tasks = new System.Collections.Generic.List<Task<(double Mean, double Max)>>();

                for (int i = 0; i < numSamples; i++)
                {
                    double startTime = 0.0;
                    if (numSamples > 1)
                    {
                        startTime = random.NextDouble() * (durationSeconds - sampleDuration);
                    }

                    double sampleStart = startTime;
                    tasks.Add(Task.Run(async () =>
                    {
                        using var process = new System.Diagnostics.Process();
                        process.StartInfo.FileName = ffmpegPath;
                        process.StartInfo.Arguments = "-ss " + sampleStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " -t " + sampleDuration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " -i \"" + filePath + "\" -vn -af volumedetect -f null -";
                        process.StartInfo.UseShellExecute = false;
                        process.StartInfo.RedirectStandardError = true;
                        process.StartInfo.CreateNoWindow = true;

                        process.Start();
                        string stderr = await process.StandardError.ReadToEndAsync();
                        await process.WaitForExitAsync();

                        TryParseVolumeDetect(stderr, out double meanDb, out double maxDb);
                        return (meanDb, maxDb);
                    }));
                }

                var results = await Task.WhenAll(tasks);

                if (scanGeneration != Volatile.Read(ref _openGeneration) || _isClosing)
                    return;

                double meanSum = 0.0;
                int meanCount = 0;
                double worstMax = -99.0;

                foreach (var (mean, max) in results)
                {
                    if (mean > -90.0)
                    {
                        meanSum += mean;
                        meanCount++;
                    }
                    if (max > worstMax) worstMax = max;
                }

                if (meanCount == 0) return;

                double avgMean = meanSum / meanCount;
                double gainDb = AutoVolumeTargetMeanDb - avgMean;

                if (gainDb > 0.0 && worstMax + gainDb > AutoVolumePeakLimitDb)
                    gainDb = Math.Max(0.0, AutoVolumePeakLimitDb - worstMax);

                gainDb = Math.Clamp(gainDb, -AutoVolumeMaxGainDb, AutoVolumeMaxGainDb);

                double detectedMean = avgMean;
                double detectedMax = worstMax;
                Dispatcher.Invoke(() =>
                {
                    if (scanGeneration != Volatile.Read(ref _openGeneration) || _isClosing) return;
                    ApplyNormalizedVolume(gainDb, detectedMean, detectedMax);
                });
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to run random interval volume scan", ex);
            }
        }

        private void SetVolumeSlider(int vol)
        {
            vol = (int)Math.Clamp(vol, 0, 100);

            _isUpdatingFromPlayer = true;
            if (SliderVolume != null) SliderVolume.Value = vol;
            if (SliderVolumeFS != null) SliderVolumeFS.Value = vol;
            _isUpdatingFromPlayer = false;

            if (!_isMuted) UpdateMuteIcon(vol);
            if (_waveOut != null) _waveOut.Volume = _isMuted ? 0 : (float)(vol / 100.0);
        }

        private void ApplyNormalizedVolume(double gainDb, double meanDb, double maxPeakDb)
        {
            _lastAutoGainDb = gainDb;
            _lastDetectedMeanDb = meanDb;
            if (_isMuted) return;

            // 사용자 기준 볼륨(초기 50%) 중심으로 ±15% 범위 내 자동 조정.
            double offset = Math.Clamp(
                gainDb / AutoVolumeMaxGainDb * AutoVolumeMaxOffset,
                -AutoVolumeMaxOffset,
                AutoVolumeMaxOffset);
            int center = _userPreferredVolume;
            int minVol = Math.Max(0, center - AutoVolumeMaxOffset);
            int maxVol = Math.Min(100, center + AutoVolumeMaxOffset);
            int newVol = (int)Math.Clamp(Math.Round(center + offset), minVol, maxVol);

            SetVolumeSlider(newVol);

            Logger.Info($"[VolumeNormalizer] mean {meanDb:F1}dB / max {maxPeakDb:F1}dB → auto gain {gainDb:+0.0;-0.0;0.0}dB → slider {newVol}% (pref {center}% ±{AutoVolumeMaxOffset}%)");
        }

        private string GetNowPlayingDisplayName(string path)
        {
            if (_playlistIndex >= 0 && _playlistIndex < _playlist.Count)
            {
                var item = _playlist[_playlistIndex];
                if (string.Equals(item.Path, path, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(item.Name))
                {
                    return item.Name;
                }
            }

            if (IsStreamingPath(path)) return path;

            string fileName = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(fileName) ? path : fileName;
        }

        private static string RepairLegacyKoreanTagText(string? value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;

            string text = value.Trim();
            if (ContainsHangul(text) || !LooksLikeSingleByteMojibake(text)) return text;

            try
            {
                byte[] bytes = Encoding.Latin1.GetBytes(text);
                string repaired = Encoding.GetEncoding(949).GetString(bytes).Trim();
                return ContainsHangul(repaired) ? repaired : text;
            }
            catch
            {
                return text;
            }
        }

        private static bool ContainsHangul(string text)
        {
            return text.Any(ch => ch >= '\uac00' && ch <= '\ud7a3');
        }

        private static bool LooksLikeSingleByteMojibake(string text)
        {
            return text.Any(ch => ch >= '\u00a0' && ch <= '\u00ff') || text.Contains('\ufffd');
        }

        private void StartStreamingLoadingBlink()
        {
            if (ImgSplash == null) return;

            ImgSplash.Visibility = Visibility.Visible;
            var blink = new DoubleAnimation
            {
                From = 0.28,
                To = 1.0,
                Duration = TimeSpan.FromSeconds(1.4),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            ImgSplash.BeginAnimation(UIElement.OpacityProperty, blink);
        }

        private void StopStreamingLoadingBlink()
        {
            if (ImgSplash == null) return;

            ImgSplash.BeginAnimation(UIElement.OpacityProperty, null);
            ImgSplash.Opacity = 1.0;
        }

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause("button");
        private void BtnStop_Click        (object sender, RoutedEventArgs e)
        {
            Interlocked.Increment(ref _openGeneration);
            Interlocked.Increment(ref _streamingSeekGeneration);
            Interlocked.Increment(ref _subtitleLoadGeneration);
            _isOpeningFile = false;
            Volatile.Write(ref _openingOwnedByGen, 0);
            _isSeeking = false;
            _pendingSeekSubtitleMs = -1.0;
            _lastUserSeekTargetMs = -1.0;
            _currentPlaybackOpenGen = 0;
            _activePlaybackFinishedHandler = null;
            _pendingPlaylistTarget = null;
            StopStreamingLoadingBlink();
            _userWantsPlayback = false;

            var oldDecoder = _decoder;
            _decoder = null;
            if (oldDecoder != null)
            {
                oldDecoder.Stop();
                DetachDecoderEvents(oldDecoder);
                DisposeRendererSafe();
                DisposeDecoderInBackground(oldDecoder);
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
            PauseUiClock();
            _uiClockBaseMs = 0;
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

        // Centralized guard used by seek paths to avoid operating on decoder during
        // mode switch (F4) or file open. Rapid seeks + HW<->SW switches were causing
        // races with decoder/renderer recreation leading to crashes.
        private bool IsSeekBlocked() => _isOpeningFile || _decoder == null || !_decoder.IsRunning;

        private void SeekRelative(double offsetSeconds)
        {
            if (IsSeekBlocked()) return;
            
            double totalSeconds = _decoder.DurationSeconds;
            if (totalSeconds <= 0) return;
            
            double currentRatio = SliderTimeline.Value / 1000.0;
            double currentSeconds = currentRatio * totalSeconds;
            double targetSeconds = Math.Clamp(currentSeconds + offsetSeconds, 0, totalSeconds);
            double targetRatio = targetSeconds / totalSeconds;
            _pendingSeekSubtitleMs = targetSeconds * 1000.0;
            _isSeeking = true;
            UpdateSubtitleAt((int)_pendingSeekSubtitleMs);
            if (BeginStreamingSeek(targetRatio)) return;

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

        private async void SpeedLabel_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            string? youtubeUrl = GetCurrentYoutubeUrl();
            if (!string.IsNullOrEmpty(youtubeUrl))
            {
                await DownloadCurrentYoutubeVideoAsync(youtubeUrl);
            }

            e.Handled = true;
        }

        private async Task DownloadCurrentYoutubeVideoAsync(string youtubeUrl)
        {
            if (_isYoutubeDownloadInProgress)
            {
                ShowToast("스트리밍 다운로드가 이미 진행 중입니다.");
                return;
            }

            _isYoutubeDownloadInProgress = true;
            try
            {
                ShowToast("스트리밍 다운로드 정보를 가져옵니다...");
                var downloadInfo = await _streamingService.GetBestDownloadInfoAsync(youtubeUrl);
                if (downloadInfo == null)
                {
                    ShowToast("다운로드 가능한 스트림이 없습니다.");
                    return;
                }

                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save YouTube Video",
                    FileName = $"{downloadInfo.Title}.{downloadInfo.Extension}",
                    Filter = $"{downloadInfo.Extension.ToUpperInvariant()} Video|*.{downloadInfo.Extension}|All Files (*.*)|*.*",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
                };

                if (dialog.ShowDialog(this) != true) return;

                DateTime lastProgressToast = DateTime.MinValue;
                var progress = new Progress<double>(percent =>
                {
                    if ((DateTime.UtcNow - lastProgressToast).TotalSeconds >= 1 || percent >= 100.0)
                    {
                        lastProgressToast = DateTime.UtcNow;
                        ShowToast($"스트리밍 다운로드 중.. ({downloadInfo.Quality})");
                    }
                });

                ShowToast($"스트리밍 다운로드 시작 ({downloadInfo.Quality})");
                await _streamingService.DownloadBestVideoAsync(youtubeUrl, dialog.FileName, progress);
                ShowToast($"스트리밍 다운로드 완료: {Path.GetFileName(dialog.FileName)}");
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to download YouTube video", ex);
                ShowToast($"스트리밍 다운로드 실패: {ex.Message}");
            }
            finally
            {
                _isYoutubeDownloadInProgress = false;
            }
        }

        private void UpdateSpeedUI(double speed)
        {
            // Rebase UI clock at current displayed time so speed change doesn't jump the bar
            if (_uiClock.IsRunning || _userWantsPlayback)
            {
                SetUiClockSpeed(speed);
            }
            else
            {
                _uiClockSpeed = speed;
            }

            _currentSpeed = speed;
            if (_decoder != null) 
            {
                _decoder.SetSpeed(speed);
                _waveProvider?.ClearBuffer();
            }
            
            // Format without trailing .00 if it's an integer, etc.
            string formattedSpeed = (speed % 1 == 0) ? $"{speed:F1}" : $"{speed:F2}";
            string label = $"{formattedSpeed}x ▾";
            
            BtnSpeed.Content = label;
            if (BtnSpeedFS != null) BtnSpeedFS.Content = label;
            ShowToast($"재생 속도: {formattedSpeed}x");
        }

        private SubtitleManager _subtitleManager = new SubtitleManager();
        private bool _subtitlesEnabled = false;
        private int _subtitleLoadGeneration;

        // Translation logic
        private bool _isTranslationEnabled = false;
        private Dictionary<string, string> _translationCache = new Dictionary<string, string>();
        private HashSet<string> _translatingSet = new HashSet<string>();
        private static readonly System.Net.Http.HttpClient _translateHttpClient = new System.Net.Http.HttpClient();

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

        private string? GetCurrentYoutubeUrl(string? path = null)
        {
            if (_playlistIndex >= 0 && _playlistIndex < _playlist.Count)
            {
                var item = _playlist[_playlistIndex];
                if (!string.IsNullOrEmpty(item.YoutubeUrl) &&
                    (path == null || item.Path == path || _currentFilePath == item.Path))
                {
                    return item.YoutubeUrl;
                }
            }

            if (!string.IsNullOrEmpty(_currentFilePath) &&
                (_currentFilePath.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                 _currentFilePath.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)))
            {
                return _currentFilePath;
            }

            return null;
        }

        private void BlinkSubtitleButtons()
        {
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
        }

        private bool AreSubtitlesOn()
        {
            string? tag = BtnWhisper?.Tag?.ToString() ?? BtnWhisperFS?.Tag?.ToString();
            return _subtitlesEnabled || tag == "On" || tag == "StreamOn";
        }

        private void TurnSubtitlesOff(string toastMessage)
        {
            System.Threading.Interlocked.Increment(ref _subtitleLoadGeneration);
            CancelWhisperExtraction();
            _subtitleManager.Clear();
            _subtitlesEnabled = false;
            if (TxtSubtitle != null) TxtSubtitle.Text = "";
            if (SubtitleBorder != null) SubtitleBorder.Visibility = Visibility.Collapsed;
            if (BtnWhisper != null) BtnWhisper.Tag = null;
            if (BtnWhisperFS != null) BtnWhisperFS.Tag = null;
            ShowToast(toastMessage);
        }

        private async Task<bool> LoadStreamingSubtitlesAsync(string youtubeUrl, string pathSnapshot, bool showToasts)
        {
            int loadGeneration = Volatile.Read(ref _subtitleLoadGeneration);
            try
            {
                if (showToasts) ShowToast("스트리밍 자막을 가져옵니다...");
                _subtitleManager.Clear();
                await _streamingService.FetchSubtitlesAsync(youtubeUrl, _subtitleManager, CancellationToken.None);

                if (loadGeneration != Volatile.Read(ref _subtitleLoadGeneration)) return false;
                if (_currentFilePath != pathSnapshot) return false;

                if (!_subtitleManager.HasSubtitles)
                {
                    if (BtnWhisper != null) BtnWhisper.Tag = null;
                    if (BtnWhisperFS != null) BtnWhisperFS.Tag = null;
                    if (showToasts) ShowToast("사용 가능한 스트리밍 자막이 없습니다.");
                    return false;
                }

                _subtitlesEnabled = true;
                SubtitleBorder.Visibility = Visibility.Visible;
                if (BtnWhisper != null) BtnWhisper.Tag = "StreamOn";
                if (BtnWhisperFS != null) BtnWhisperFS.Tag = "StreamOn";
                BlinkSubtitleButtons();
                if (showToasts) ShowToast("스트리밍 자막을 켰습니다.");
                return true;
            }
            catch (Exception ex)
            {
                if (BtnWhisper != null) BtnWhisper.Tag = null;
                if (BtnWhisperFS != null) BtnWhisperFS.Tag = null;
                if (showToasts) ShowToast("스트리밍 자막을 가져오는데 실패했습니다.");
                Logger.Error("Failed to fetch streaming subtitles", ex);
                return false;
            }
        }

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

        private int GetBassEnhancementLevel()
        {
            string? tag = BtnBass?.Tag?.ToString() ?? BtnBassFS?.Tag?.ToString();
            return tag switch
            {
                BassTagOn => AudioEnhancerProvider.EnhancementNormal,
                BassTagMax => AudioEnhancerProvider.EnhancementMax,
                _ => AudioEnhancerProvider.EnhancementOff,
            };
        }

        private void ApplyBassEnhancementLevel(int level, bool showToast = true)
        {
            string? tag = level switch
            {
                AudioEnhancerProvider.EnhancementNormal => BassTagOn,
                AudioEnhancerProvider.EnhancementMax => BassTagMax,
                _ => null,
            };

            if (BtnBass != null) BtnBass.Tag = tag;
            if (BtnBassFS != null) BtnBassFS.Tag = tag;
            if (_audioEnhancer != null) _audioEnhancer.EnhancementLevel = level;

            if (!showToast) return;

            string message = level switch
            {
                AudioEnhancerProvider.EnhancementNormal => "Bass Boost: ON",
                AudioEnhancerProvider.EnhancementMax => "Bass Boost: MAX",
                _ => "Bass Boost: OFF",
            };
            ShowToast(message);
        }

        private void StopBassHoldTracking()
        {
            _bassHoldTimer?.Stop();
            _activeBassHoldButton = null;
        }

        private void BassHoldTimer_Tick(object? sender, EventArgs e)
        {
            _bassHoldTimer.Stop();
            if (_activeBassHoldButton?.IsPressed != true) return;

            _bassHoldTriggered = true;
            ApplyBassEnhancementLevel(AudioEnhancerProvider.EnhancementMax);
        }

        private void BtnBass_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _bassHoldTriggered = false;
            _activeBassHoldButton = sender as WpfButton;
            _bassHoldTimer.Stop();
            _bassHoldTimer.Start();
        }

        private void BtnBass_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            StopBassHoldTracking();
        }

        private void BtnBass_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (ReferenceEquals(_activeBassHoldButton, sender))
            {
                StopBassHoldTracking();
            }
        }

        private void BtnBass_Click(object sender, RoutedEventArgs e)
        {
            StopBassHoldTracking();

            if (_bassHoldTriggered)
            {
                _bassHoldTriggered = false;
                return;
            }

            int nextLevel = GetBassEnhancementLevel() == AudioEnhancerProvider.EnhancementOff
                ? AudioEnhancerProvider.EnhancementNormal
                : AudioEnhancerProvider.EnhancementOff;
            ApplyBassEnhancementLevel(nextLevel);
        }

        private void ResetBassBoostToOff()
        {
            ApplyBassEnhancementLevel(AudioEnhancerProvider.EnhancementOff, showToast: false);
        }

        private void BtnHq_Click(object sender, RoutedEventArgs e) => ToggleEnhancedShader();

        private async void BtnWhisper_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFilePath)) {
                ShowToast("먼저 동영상을 열어주세요.");
                return;
            }

            // If CC+ is ON, turn it off
            if (AreSubtitlesOn())
            {
                TurnSubtitlesOff("자막을 껐습니다.");
                return;
            }

            string? currentYoutubeUrl = GetCurrentYoutubeUrl();
            if (!string.IsNullOrEmpty(currentYoutubeUrl))
            {
                System.Threading.Interlocked.Increment(ref _subtitleLoadGeneration);
                _isWhisperAnimatingToCC = true;
                if (BtnWhisper != null) BtnWhisper.Tag = "StreamOn";
                if (BtnWhisperFS != null) BtnWhisperFS.Tag = "StreamOn";
                await LoadStreamingSubtitlesAsync(currentYoutubeUrl, _currentFilePath, true);
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

            string tempWavPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "temp_audio.wav");
            int activeOpenGen = Volatile.Read(ref _openGeneration);
            string activeFilePath = _currentFilePath;
            
            await WhisperExtractor.ExtractSubtitlesAsync(_currentFilePath, tempWavPath, (status, progress) => {
                Dispatcher.InvokeAsync(() => {
                    if (activeOpenGen != Volatile.Read(ref _openGeneration) || activeFilePath != _currentFilePath) return;
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
                    if (activeOpenGen != Volatile.Read(ref _openGeneration) || activeFilePath != _currentFilePath) return;
                    _subtitleManager.AddSubtitle(start, end, text);
                });
            }, (srtPathResult, errorMsg) => {
                Dispatcher.InvokeAsync(async () => {
                    if (activeOpenGen != Volatile.Read(ref _openGeneration) || activeFilePath != _currentFilePath) return;
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
                        UpdateSubtitleLanguage();

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

        private void SliderTimeline_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        {
            if (IsSeekBlocked()) return;
            _isUserDraggingSlider = true;
            if (_decoder != null && _decoder.DurationSeconds > 0)
            {
                _pendingSeekSubtitleMs = (SliderTimeline.Value / 1000.0) * _decoder.DurationSeconds * 1000.0;
            }
        }

        private void SliderTimeline_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            _suppressTimelineSeek = true;

            Slider? targetSlider = sender as Slider;
            if (targetSlider == null && e.OriginalSource is System.Windows.Controls.Primitives.Thumb thumb)
            {
                targetSlider = thumb.TemplatedParent as Slider;
            }
            if (targetSlider == null) targetSlider = SliderTimeline;
            
            DoSeek(targetSlider.Value);
            _isUserDraggingSlider = false;
            _pendingSeekSubtitleMs = -1.0;
            _lastUserSeekTargetMs = -1.0;

            Dispatcher.BeginInvoke(new Action(() => _suppressTimelineSeek = false), DispatcherPriority.ApplicationIdle);
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
            if (_isUserDraggingSlider)
            {
                if (_decoder != null && _decoder.DurationSeconds > 0)
                {
                    double targetRatio = e.NewValue / 1000.0;
                    _pendingSeekSubtitleMs = targetRatio * _decoder.DurationSeconds * 1000.0;
                    UpdateSubtitleAt((int)_pendingSeekSubtitleMs);
                }
            }
            else if (!_suppressTimelineSeek && !IsSeekBlocked())
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
            // === USER SEEK UX CONTRACT (protected logic) ===
            // When user clicks/drags to a position:
            // 1. Immediately update UI clock + slider to that exact target (optimistic, responsive bar).
            // 2. Record _lastUserSeekTargetMs so SeekPerformed knows not to snap back to keyframe/early land.
            // 3. Issue decoder.Seek (which may land on keyframe for video).
            // 4. On land, only correct UI if landed significantly later (or huge error). Never jump bar backwards.
            // This is the core of "bar shows what user asked, content catches up from nearest keyframe".
            // DO NOT break this without updating all call sites and comments.
            // Related optimizations: small-seek queue preserve, post-seek reduced prebuffer, seek gen cancel.
            if (IsSeekBlocked())
            {
                _isSeeking = false;
                _pendingSeekSubtitleMs = -1.0;
                _lastUserSeekTargetMs = -1.0;
                return;
            }
            _isSeeking = true;
            double targetRatio = sliderValue / 1000.0;
            double durationMs = _decoder.DurationSeconds * 1000.0;
            double targetMs = targetRatio * durationMs;
            _pendingSeekSubtitleMs = targetMs;
            _lastUserSeekTargetMs = targetMs;
            UpdateSubtitleAt((int)targetMs);

            _isUpdatingFromPlayer = true;
            SyncUiClock(targetMs);
            double targetSliderVal = targetRatio * SliderTimeline.Maximum;
            SliderTimeline.Value = targetSliderVal;
            if (SliderTimelineFS != null) SliderTimelineFS.Value = targetSliderVal;
            _isUpdatingFromPlayer = false;

            if (BeginStreamingSeek(targetRatio)) return;

            _decoder.Seek(targetRatio);
            _seekCount++;
        }

        private bool BeginStreamingSeek(double targetRatio)
        {
            if (string.IsNullOrEmpty(_currentFilePath) || !IsStreamingPath(_currentFilePath)) return false;

            PlaylistItem? item = _playlistIndex >= 0 && _playlistIndex < _playlist.Count ? _playlist[_playlistIndex] : null;
            bool isYoutube = !string.IsNullOrEmpty(item?.YoutubeUrl);
            bool isAdaptive = isYoutube ||
                              !string.IsNullOrEmpty(item?.AudioPath) ||
                              IsAdaptiveStreamingPath(_currentFilePath) ||
                              IsAdaptiveStreamingPath(item?.Path) ||
                              IsAdaptiveStreamingPath(item?.AudioPath);

            if (!isAdaptive) return false;
            if (_isOpeningFile) return true;

            _seekCount++;
            _waveProvider?.ClearBuffer();

            double clampedRatio = Math.Clamp(targetRatio, 0.0, 1.0);
            int seekGeneration = Interlocked.Increment(ref _streamingSeekGeneration);

            if (isYoutube && item != null)
            {
                RestartYoutubeStreamAtRatio(item, clampedRatio, seekGeneration);
            }
            else
            {
                PlayFile(_currentFilePath, clampedRatio);
            }
            return true;
        }

        private async void RestartYoutubeStreamAtRatio(PlaylistItem item, double targetRatio, int seekGeneration)
        {
            try
            {
                var streamUrl = await _streamingService.GetStreamUrlAsync(item.YoutubeUrl!);
                if (seekGeneration != Volatile.Read(ref _streamingSeekGeneration)) return;

                if (string.IsNullOrEmpty(streamUrl))
                {
                    _isSeeking = false;
                    _pendingSeekSubtitleMs = -1.0;
                    WpfMessageBox.Show("스트리밍 주소를 다시 가져올 수 없습니다.", "JonPlayer", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                item.Path = streamUrl;
                item.AudioPath = _streamingService.LastAudioUrl;
                _currentFilePath = streamUrl;

                long watchdogStartTicks = DateTime.UtcNow.Ticks;
                bool isAdaptiveAttempt = !string.IsNullOrEmpty(item.AudioPath);
                PlayFile(streamUrl, targetRatio);

                if (isAdaptiveAttempt)
                {
                    _ = FallbackYoutubeSeekToMuxedIfStalledAsync(item, targetRatio, seekGeneration, watchdogStartTicks);
                }
            }
            catch (Exception ex)
            {
                if (seekGeneration != Volatile.Read(ref _streamingSeekGeneration)) return;
                bool fallbackStarted = await TryStartYoutubeMuxedFallbackAsync(item, targetRatio, seekGeneration);
                if (!fallbackStarted)
                {
                    _isSeeking = false;
                    _pendingSeekSubtitleMs = -1.0;
                    WpfMessageBox.Show($"스트리밍 seek를 시작할 수 없습니다.\n{ex.Message}", "JonPlayer", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private async Task FallbackYoutubeSeekToMuxedIfStalledAsync(PlaylistItem item, double targetRatio, int seekGeneration, long watchdogStartTicks)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (seekGeneration != Volatile.Read(ref _streamingSeekGeneration)) return;

            long lastFrameTicks = Volatile.Read(ref _lastFrameTicks);
            long lastAudioTicks = Volatile.Read(ref _lastAudioTicks);
            if (lastFrameTicks > watchdogStartTicks || lastAudioTicks > watchdogStartTicks) return;

            await TryStartYoutubeMuxedFallbackAsync(item, targetRatio, seekGeneration);
        }

        private async Task<bool> TryStartYoutubeMuxedFallbackAsync(PlaylistItem item, double targetRatio, int seekGeneration)
        {
            try
            {
                if (string.IsNullOrEmpty(item.YoutubeUrl)) return false;

                var muxedUrl = await _streamingService.GetMuxedStreamUrlAsync(item.YoutubeUrl);
                if (seekGeneration != Volatile.Read(ref _streamingSeekGeneration)) return true;
                if (string.IsNullOrEmpty(muxedUrl)) return false;

                item.Path = muxedUrl;
                item.AudioPath = null;
                _currentFilePath = muxedUrl;
                PlayFile(muxedUrl, targetRatio);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to start YouTube muxed seek fallback", ex);
                return false;
            }
        }

        private void Decoder_SeekInitiated()
        {
            _waveOut?.Stop();
            _waveProvider?.ClearBuffer();
            _renderer?.PrepareForSeek();
            _renderer?.ResetPresentationPacing();
        }

        private void Decoder_SeekPerformed()
        {
            // === LAND HANDLING (must respect user target) ===
            // See DoSeek contract above. Subtitles use actual media time.
            // Timeline/bar prefers user target unless landed is much later.
            Dispatcher.BeginInvoke(() =>
            {
                _allowTimelineBackward = true;
                _isSeeking = false;
                _pendingSeekSubtitleMs = -1.0;
                _renderer?.ResetPresentationPacing();
                _waveProvider?.ClearBuffer();
                _decoder?.ReleasePostSeekPlayback();

                if (_decoder != null)
                {
                    int actualMs = (int)_decoder.GetCurrentTimeMs();
                    UpdateSubtitleAt(actualMs);
                }

                double landedMs = _decoder != null ? _decoder.GetCurrentTimeMs() : 0;

                if (_decoder != null)
                {
                    // For user-initiated seeks, strongly prefer keeping the bar at the exact position the user clicked.
                    // Only snap if the landed position is significantly *later* than requested (or huge error).
                    // This matches how most commercial players handle click-to-seek UX: the timeline reflects user intent.
                    bool shouldSnap = _lastUserSeekTargetMs < 0 
                        || (landedMs > _lastUserSeekTargetMs + 1500);   // snap only if landed much later
                    if (shouldSnap)
                    {
                        SyncUiClock(landedMs);
                        _isUpdatingFromPlayer = true;
                        double durationMs = _decoder.DurationSeconds * 1000.0;
                        double sliderVal = durationMs > 0 ? landedMs / durationMs * SliderTimeline.Maximum : 0;
                        if (SliderTimeline != null) SliderTimeline.Value = sliderVal;
                        if (SliderTimelineFS != null) SliderTimelineFS.Value = sliderVal;
                        _isUpdatingFromPlayer = false;
                    }
                    // else keep user target. The actual video will start from the closest possible (after keyframe forward-decode).
                }
                _lastUserSeekTargetMs = -1;

                UpdateTimelineFromPlayback();

                if (_userWantsPlayback && _waveOut != null)
                {
                    if (!_isMuted)
                        _waveOut.Volume = (float)(SliderVolume.Value / 100.0);
                    _waveOut.Play();
                }
            });
        }

        private void SliderVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int vol = (int)e.NewValue;

            if (!_isUpdatingFromPlayer && !_isMuted)
                _userPreferredVolume = vol;

            if (_isUpdatingFromPlayer) return;

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
            if (MuteIconPathPip != null) MuteIconPathPip.Data = geom;
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
                if (MuteIconPathPip != null) MuteIconPathPip.Data = muteGeom;
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
            if (_isPipMode)
            {
                ExitPipMode();
                EnterFullscreen();
                return;
            }

            if (_isFullscreen) ExitFullscreen();
            else EnterFullscreen();
        }

        private bool _isPipMode = false;
        private bool _forceSoftwareDecode = false;  // F4 toggle: true = force SW CPU, false = HW accel (default, existing logic)
        private WindowStyle _backupWindowStyle;
        private bool _backupTopmost;
        private Rect _backupBounds;
        private double _backupMinWidth;
        private double _backupMinHeight;
        private double _subtitleNormalFontSize = 26;
        private Thickness _subtitleNormalMargin = new Thickness(20, 0, 20, 20);
        private double _subtitleNormalMaxWidth = 1000;
        private Thickness _statsNormalMargin = new Thickness(10);
        private Thickness _statsNormalPadding = new Thickness(10);
        private double _statsNormalFontSize = 12;
        private double _statsNormalLineHeight = 18;

        private void BtnPip_Click(object sender, RoutedEventArgs e) => TogglePipMode();

        private void TogglePipMode()
        {
            if (_isPipMode) ExitPipMode();
            else EnterPipMode();
        }

        private bool _isEnhancedShaderEnabled = false;

        private void ToggleEnhancedShader()
        {
            _isEnhancedShaderEnabled = !_isEnhancedShaderEnabled;
            ApplyEnhancedShaderState();
            ShowToast($"HQ+ Enhanced: {(_isEnhancedShaderEnabled ? "ON" : "OFF")}");
        }

        /// <summary>
        /// Push HQ+ (enhanced HLSL) state to the live renderer and both control-bar toggles.
        /// Safe to call after renderer recreate (PlayFile / HW toggle).
        /// </summary>
        private void ApplyEnhancedShaderState()
        {
            if (_renderer != null)
                _renderer.EnableEnhancedShader(_isEnhancedShaderEnabled);

            object? tag = _isEnhancedShaderEnabled ? "On" : null;
            if (BtnHq != null) BtnHq.Tag = tag;
            if (BtnHqFS != null) BtnHqFS.Tag = tag;
        }

        private void ToggleHwAccel()
        {
            if (_isOpeningFile) return;

            _forceSoftwareDecode = !_forceSoftwareDecode;
            FFmpegMediaDecoder.EnableHwAccel = !_forceSoftwareDecode;

            string mode = _forceSoftwareDecode ? "SW (CPU)" : "HW (D3D11VA)";
            if (_forceSoftwareDecode && _decoder != null && _decoder.HasVideo
                && _decoder.Width * _decoder.Height > 1920 * 1080)
            {
                ShowToast($"Decode: {mode} — 4K SW는 HW보다 느림 (F4)");
            }
            else
            {
                ShowToast($"Decode: {mode} (F4)");
            }

            // Reload current *video* file to apply new decode mode (preserves approx position).
            // Audio-only files don't use HW path, so no reload needed.
            if (!string.IsNullOrEmpty(_currentFilePath) && _decoder != null && _decoder.HasVideo && _decoder.IsRunning)
            {
                double ratio = 0.0;
                try
                {
                    if (_decoder.DurationSeconds > 0.05)
                    {
                        double posMs = _decoder.GetCurrentTimeMs();
                        ratio = Math.Clamp(posMs / (_decoder.DurationSeconds * 1000.0), 0.0, 0.98);
                    }
                }
                catch { /* ignore */ }

                PlayFile(_currentFilePath, ratio);
            }
        }

        private void EnterPipMode()
        {
            if (_isPipMode || _isFullscreen) return;
            _isPipMode = true;

            _backupWindowStyle = this.WindowStyle;
            _backupTopmost = this.Topmost;
            _backupBounds = new Rect(this.Left, this.Top, this.Width, this.Height);
            _backupMinWidth = this.MinWidth;
            _backupMinHeight = this.MinHeight;
            CaptureOverlayDefaults();

            if (_isDraggingSubtitle)
            {
                _isDraggingSubtitle = false;
                SubtitleBorder.ReleaseMouseCapture();
            }

            this.MinWidth = 0;
            this.MinHeight = 0;
            if (_overlayWindow != null)
            {
                _overlayWindow.MinWidth = 0;
                _overlayWindow.MinHeight = 0;
            }

            ApplyPipOverlayLayout(false);

            this.WindowStyle = WindowStyle.None;
            this.Topmost = true;
            this.Width = 400;
            this.Height = 225;
            
            if (_overlayWindow != null) _overlayWindow.IsHitTestVisible = true;

            var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            this.Left = screen.WorkingArea.Right - this.Width - 20;
            this.Top = screen.WorkingArea.Bottom - this.Height - 20;

            if (BtnPipOverlay != null) BtnPipOverlay.ToolTip = "Exit PIP Mode (P)";
            SetPipHoverOverlayVisible(MainGrid.IsMouseOver);

            // sync pip mute icon
            int volForIcon = _isMuted ? 0 : (int)(SliderVolume?.Value ?? 50);
            UpdateMuteIcon(volForIcon);
        }

        private void ExitPipMode()
        {
            if (!_isPipMode) return;
            _isPipMode = false;

            this.WindowStyle = _backupWindowStyle;
            this.Topmost = _backupTopmost;
            this.MinWidth = _backupMinWidth;
            this.MinHeight = _backupMinHeight;
            if (_overlayWindow != null)
            {
                _overlayWindow.MinWidth = _backupMinWidth;
                _overlayWindow.MinHeight = _backupMinHeight;
            }
            this.Left = _backupBounds.Left;
            this.Top = _backupBounds.Top;
            this.Width = _backupBounds.Width;
            this.Height = _backupBounds.Height;

            RowTitleBar.Height = new GridLength(40);
            RowTimeline.Height = GridLength.Auto;
            RowControls.Height = GridLength.Auto;
            if (BtnPlayPausePip != null) BtnPlayPausePip.Visibility = Visibility.Collapsed;
            if (PipTopBar != null) PipTopBar.Visibility = Visibility.Collapsed;
            if (PipBottomBar != null) PipBottomBar.Visibility = Visibility.Collapsed;
            if (BtnMutePip != null) BtnMutePip.Visibility = Visibility.Collapsed;
            if (BtnPipOverlay != null) BtnPipOverlay.Visibility = Visibility.Collapsed;

            RestoreNormalPlayerLayout();

            if (_overlayWindow != null) _overlayWindow.IsHitTestVisible = true;

            if (BtnPip != null) BtnPip.ToolTip = "PIP Mode (P)";
        }

        private void SetPipHoverOverlayVisible(bool visible)
        {
            if (!_isPipMode) return;

            RowTitleBar.Height = new GridLength(0);
            RowTimeline.Height = new GridLength(0);
            RowControls.Height = new GridLength(0);

            // Top controls (X , mute, pip icon) and bottom only appear when mouse is near / hovering
            if (PipTopBar != null) PipTopBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (BtnMutePip != null) BtnMutePip.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (BtnPipOverlay != null) BtnPipOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (PipBottomBar != null) PipBottomBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (BtnPlayPausePip != null) BtnPlayPausePip.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

            ApplyPipOverlayLayout(visible);
            MainGrid.UpdateLayout();
            SyncOverlayWindowToMainWindow();
        }

        private void CaptureOverlayDefaults()
        {
            if (TxtSubtitle != null)
            {
                _subtitleNormalFontSize = TxtSubtitle.FontSize;
                _subtitleNormalMaxWidth = TxtSubtitle.MaxWidth;
            }
            if (SubtitleBorder != null) _subtitleNormalMargin = SubtitleBorder.Margin;
            if (OverlayStats != null)
            {
                _statsNormalMargin = OverlayStats.Margin;
                _statsNormalPadding = OverlayStats.Padding;
            }
            if (TxtOverlayStatsLeft != null)
            {
                _statsNormalFontSize = TxtOverlayStatsLeft.FontSize;
                _statsNormalLineHeight = TxtOverlayStatsLeft.LineHeight;
            }
        }

        private void ApplyPipControlBarLayout(bool controlsVisible)
        {
            double pipWidth = Math.Max(0, this.ActualWidth > 0 ? this.ActualWidth : this.Width);
            if (ControlBarGrid != null) ControlBarGrid.MinWidth = controlsVisible ? Math.Min(260, pipWidth) : 0;
            if (ControlBarLeftPanel != null) ControlBarLeftPanel.Visibility = controlsVisible ? Visibility.Collapsed : Visibility.Visible;
            if (ControlBarRightPanel != null) ControlBarRightPanel.Visibility = controlsVisible ? Visibility.Collapsed : Visibility.Visible;
            if (ControlBarCenterPanel != null) ControlBarCenterPanel.Margin = controlsVisible ? new Thickness(0, 4, 0, 4) : new Thickness(0, 8, 0, 8);
        }

        private void ApplyPipOverlayLayout(bool controlsVisible)
        {
            double pipWidth = Math.Max(240, this.ActualWidth > 0 ? this.ActualWidth : this.Width);

            ApplyPipControlBarLayout(controlsVisible);

            if (SubtitleBorder != null)
            {
                SubtitleBorder.Margin = new Thickness(8, 0, 8, controlsVisible ? 8 : 6);
                SubtitleBorder.Padding = new Thickness(8, 4, 8, 4);
                SubtitleTransform.X = 0;
                SubtitleTransform.Y = 0;
                FsSubtitleShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
                FsSubtitleShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
                FsSubtitleShift.X = 0;
                FsSubtitleShift.Y = 0;
            }
            if (TxtSubtitle != null)
            {
                TxtSubtitle.FontSize = 12;
                TxtSubtitle.MaxWidth = Math.Max(180, pipWidth - 24);
            }

            if (OverlayStats != null)
            {
                OverlayStats.Margin = new Thickness(4);
                OverlayStats.Padding = new Thickness(5);
                OverlayStats.MaxWidth = Math.Max(180, pipWidth - 12);
                OverlayStats.MaxHeight = Math.Max(80, (this.ActualHeight > 0 ? this.ActualHeight : this.Height) - 8);
            }
            if (TxtOverlayStatsLeft != null)
            {
                TxtOverlayStatsLeft.FontSize = 8;
                TxtOverlayStatsLeft.LineHeight = 10;
                TxtOverlayStatsRight.FontSize = 8;
                TxtOverlayStatsRight.LineHeight = 10;
            }
        }

        private void RestoreNormalPlayerLayout()
        {
            if (ControlBarGrid != null) ControlBarGrid.MinWidth = 800;
            if (ControlBarLeftPanel != null) ControlBarLeftPanel.Visibility = Visibility.Visible;
            if (ControlBarRightPanel != null) ControlBarRightPanel.Visibility = Visibility.Visible;
            if (ControlBarCenterPanel != null) ControlBarCenterPanel.Margin = new Thickness(0, 8, 0, 8);

            if (SubtitleBorder != null)
            {
                SubtitleBorder.Margin = _subtitleNormalMargin;
                SubtitleBorder.Padding = new Thickness(16, 8, 16, 8);
            }
            if (TxtSubtitle != null)
            {
                TxtSubtitle.FontSize = _subtitleNormalFontSize;
                TxtSubtitle.MaxWidth = _subtitleNormalMaxWidth;
            }
            if (OverlayStats != null)
            {
                OverlayStats.Margin = _statsNormalMargin;
                OverlayStats.Padding = _statsNormalPadding;
                OverlayStats.MaxWidth = double.PositiveInfinity;
                OverlayStats.MaxHeight = double.PositiveInfinity;
            }
            if (TxtOverlayStatsLeft != null)
            {
                TxtOverlayStatsLeft.FontSize = _statsNormalFontSize;
                TxtOverlayStatsLeft.LineHeight = _statsNormalLineHeight;
                TxtOverlayStatsRight.FontSize = _statsNormalFontSize;
                TxtOverlayStatsRight.LineHeight = _statsNormalLineHeight;
            }
        }

        private bool _isFitScreen = false;
        private Rect _backupNormalBounds;

        // Current video stretch/zoom mode. Applied to D3D11 renderer (real output) and kept for WPF fallbacks.
        // Z key cycles: Uniform (원본 비율 + 여백) -> UniformToFill (가득 채우기/자르기) -> Fill (강제 늘림) -> Uniform
        private System.Windows.Media.Stretch _currentVideoStretch = System.Windows.Media.Stretch.Uniform;

        private async void BtnTranslate_Click(object sender, RoutedEventArgs e)
        {
            _isTranslationEnabled = !_isTranslationEnabled;
            BtnTranslate.Tag = null;
            if (BtnTranslateFS != null) BtnTranslateFS.Tag = null;
            
            if (_subtitleManager.HasSubtitles)
            {
                string detectedLang = _subtitleManager.DetectLanguage();
                string displayLang = _isTranslationEnabled ? (detectedLang == "KR" ? "EN" : "KR") : detectedLang;
                BtnTranslate.Content = displayLang;
                if (BtnTranslateFS != null) BtnTranslateFS.Content = displayLang;
            }
        }

        private async System.Threading.Tasks.Task FetchAndApplyTranslationAsync(string text, int timeMs)
        {
            if (_translationCache.ContainsKey(text) || _translatingSet.Contains(text)) return;
            _translatingSet.Add(text);

            string translated = await GetTranslatedSubtitleAsync(text);
            _translationCache[text] = translated;
            _translatingSet.Remove(text);

            Dispatcher.Invoke(() => {
                if (_isTranslationEnabled && _subtitlesEnabled && TxtSubtitle.Text == text)
                {
                    TxtSubtitle.Text = translated; 
                }
            });
        }

        private async System.Threading.Tasks.Task<string> GetTranslatedSubtitleAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            
            try
            {
                bool isKorean = System.Text.RegularExpressions.Regex.IsMatch(text, @"[가-힣]");
                string tl = isKorean ? "en" : "ko";
                


                string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={tl}&dt=t&q={Uri.EscapeDataString(text)}";
                
                string json = await _translateHttpClient.GetStringAsync(url);
                using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                    {
                        var sentences = doc.RootElement[0];
                        if (sentences.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            System.Text.StringBuilder sb = new System.Text.StringBuilder();
                            foreach (var sentence in sentences.EnumerateArray())
                            {
                                if (sentence.ValueKind == System.Text.Json.JsonValueKind.Array && sentence.GetArrayLength() > 0)
                                {
                                    sb.Append(sentence[0].GetString());
                                }
                            }
                            return sb.ToString();
                        }
                    }
                }
            }
            catch { }
            return text;
        }

        private void FitScreen()
        {
            if (_isFullscreen) ExitFullscreen();
            
            if (_isFitScreen)
            {
                _isFitScreen = false;
                this.Left = _backupNormalBounds.Left;
                this.Top = _backupNormalBounds.Top;
                this.Width = _backupNormalBounds.Width;
                this.Height = _backupNormalBounds.Height;
            }
            else
            {
                _backupNormalBounds = new Rect(this.Left, this.Top, this.Width, this.Height);
                var logicalWorkingArea = GetCurrentScreenWorkingAreaLogical();
                this.Left = logicalWorkingArea.Left;
                this.Top = logicalWorkingArea.Top;
                this.Width = logicalWorkingArea.Width;
                this.Height = logicalWorkingArea.Height;
                _isFitScreen = true;
            }
        }

        /// <summary>
        /// Working area of the monitor that currently contains this window, in WPF DIPs.
        /// </summary>
        private Rect GetCurrentScreenWorkingAreaLogical()
        {
            var screen = System.Windows.Forms.Screen.FromHandle(
                new System.Windows.Interop.WindowInteropHelper(this).Handle);

            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                var transform = source.CompositionTarget.TransformFromDevice;
                var tl = transform.Transform(new Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
                var br = transform.Transform(new Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
                return new Rect(tl, br);
            }

            return new Rect(
                screen.WorkingArea.Left,
                screen.WorkingArea.Top,
                screen.WorkingArea.Width,
                screen.WorkingArea.Height);
        }

        /// <summary>Chrome height added on top of scaled video height (title + timeline + controls).</summary>
        private const double WindowSizePresetChromeHeight = 60;

        /// <summary>
        /// Target client size for a video-relative window preset (50% / 100% / 200%).
        /// Returns false when there is no active video size to base the preset on.
        /// </summary>
        private bool TryGetWindowSizePreset(double scale, out double width, out double height)
        {
            width = 0;
            height = 0;

            if (_isPipMode)
            {
                width = 400 * scale;
                height = 225 * scale;
                return true;
            }

            if (_decoder != null && _decoder.Width > 0 && _decoder.Height > 0)
            {
                width = _decoder.Width * scale;
                height = _decoder.Height * scale + WindowSizePresetChromeHeight;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Whether a size preset fits entirely inside the current monitor working area.
        /// Large videos often only allow 50% (or none) on typical screens.
        /// </summary>
        private bool CanFitWindowSizePreset(double scale)
        {
            if (!TryGetWindowSizePreset(scale, out double width, out double height))
                return false;

            var wa = GetCurrentScreenWorkingAreaLogical();
            // 1 DIP tolerance for float / DPI rounding
            return width <= wa.Width + 1.0 && height <= wa.Height + 1.0;
        }

        /// <summary>
        /// Dim unavailable preset key badges and rewrite the F1 help line so only
        /// screen-fitting scales look / act enabled.
        /// </summary>
        private void UpdateWindowSizePresetUi()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(UpdateWindowSizePresetUi));
                return;
            }

            bool hasVideo = !_isPipMode
                && _decoder != null
                && _decoder.Width > 0
                && _decoder.Height > 0;

            // No media: show full legend as documentation, all badges full opacity.
            bool can50 = !hasVideo || CanFitWindowSizePreset(0.5);
            bool can100 = !hasVideo || CanFitWindowSizePreset(1.0);
            bool can200 = !hasVideo || CanFitWindowSizePreset(2.0);

            if (BadgeSize50 != null) BadgeSize50.Opacity = can50 ? 1.0 : 0.32;
            if (BadgeSize100 != null) BadgeSize100.Opacity = can100 ? 1.0 : 0.32;
            if (BadgeSize200 != null) BadgeSize200.Opacity = can200 ? 1.0 : 0.32;

            if (TxtWindowSizePresets == null) return;

            if (!hasVideo)
            {
                TxtWindowSizePresets.Text = "창 크기 50% / 100% / 200% (현재 화면 기준)";
                TxtWindowSizePresets.Opacity = 1.0;
                return;
            }

            if (!can50 && !can100 && !can200)
            {
                TxtWindowSizePresets.Text = "창 크기 프리셋 없음 (영상 > 현재 화면)";
                TxtWindowSizePresets.Opacity = 0.65;
                return;
            }

            if (can50 && can100 && can200)
            {
                TxtWindowSizePresets.Text = "창 크기 50% / 100% / 200%";
                TxtWindowSizePresets.Opacity = 1.0;
                return;
            }

            // Only list scales that fit the current monitor (e.g. large video → "50%" only).
            var available = new System.Text.StringBuilder("창 크기 ");
            bool first = true;
            if (can50) { available.Append("50%"); first = false; }
            if (can100) { if (!first) available.Append(" / "); available.Append("100%"); first = false; }
            if (can200) { if (!first) available.Append(" / "); available.Append("200%"); }
            available.Append(" (현재 화면 기준)");
            TxtWindowSizePresets.Text = available.ToString();
            TxtWindowSizePresets.Opacity = 1.0;
        }

        /// <summary>
        /// Apply a video-relative window size preset if it fits the current screen.
        /// Returns true when the window was resized; false when blocked (toast already shown).
        /// </summary>
        private bool TryApplyWindowSizePreset(double scale)
        {
            if (!TryGetWindowSizePreset(scale, out double width, out double height))
            {
                ShowToast("재생 중인 영상이 없습니다");
                return false;
            }

            if (!CanFitWindowSizePreset(scale))
            {
                int pct = (int)Math.Round(scale * 100);
                ShowToast($"창 크기 {pct}%: 현재 화면에 맞지 않음");
                return false;
            }

            ResizeScreenTo(width, height);
            return true;
        }

        private void ResizeScreen(double scale)
        {
            TryApplyWindowSizePreset(scale);
        }

        private void ResizeScreenTo(double width, double height)
        {
            if (_isPipMode)
            {
                this.Width = width;
                this.Height = height;
                return;
            }

            if (_isFullscreen) ExitFullscreen();
            if (this.WindowState == WindowState.Maximized) this.WindowState = WindowState.Normal;

            this.Width = width;
            this.Height = height;

            // Keep the resized window fully on the current monitor when possible.
            var wa = GetCurrentScreenWorkingAreaLogical();
            if (this.Left + width > wa.Right)
                this.Left = Math.Max(wa.Left, wa.Right - width);
            if (this.Top + height > wa.Bottom)
                this.Top = Math.Max(wa.Top, wa.Bottom - height);
            if (this.Left < wa.Left) this.Left = wa.Left;
            if (this.Top < wa.Top) this.Top = wa.Top;
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
            if (_decoder == null || !_decoder.IsPlaying && !_decoder.IsRunning) 
            {
                ShowToast("캡처할 영상이 없습니다.");
                return;
            }

            try
            {
                var source = PresentationSource.FromVisual(this);
                if (source == null) { ShowToast("캡처할 수 없는 상태입니다."); return; }

                double windowWidth = VideoGrid.ActualWidth;
                double windowHeight = VideoGrid.ActualHeight;
                double videoWidth = _decoder.Width;
                double videoHeight = _decoder.Height;

                double viewX = 0, viewY = 0, viewW = windowWidth, viewH = windowHeight;

                if (videoWidth > 0 && videoHeight > 0)
                {
                    double windowAspect = windowWidth / windowHeight;
                    double videoAspect = videoWidth / videoHeight;

                    if (windowAspect > videoAspect)
                    {
                        viewW = windowHeight * videoAspect;
                        viewX = (windowWidth - viewW) / 2.0;
                    }
                    else
                    {
                        viewH = windowWidth / videoAspect;
                        viewY = (windowHeight - viewH) / 2.0;
                    }
                }

                Point physicalTopLeft = VideoGrid.PointToScreen(new Point(viewX, viewY));
                Point physicalBottomRight = VideoGrid.PointToScreen(new Point(viewX + viewW, viewY + viewH));

                int x = (int)physicalTopLeft.X;
                int y = (int)physicalTopLeft.Y;
                int width = (int)(physicalBottomRight.X - physicalTopLeft.X);
                int height = (int)(physicalBottomRight.Y - physicalTopLeft.Y);

                if (width <= 0 || height <= 0) return;

                using (var bmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(x, y, 0, 0, bmp.Size, System.Drawing.CopyPixelOperation.SourceCopy);
                    }

                    string downloadsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    string filename = $"JonPlayer_Snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                    string fullPath = System.IO.Path.Combine(downloadsPath, filename);

                    bmp.Save(fullPath, System.Drawing.Imaging.ImageFormat.Png);
                    ShowToast($"캡처 완료: {filename}");
                }
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
                if (_decoder.IsPlaying) TogglePlayPause("close-file");
                
                await Task.Delay(50);
                
                var decoder = _decoder;
                _decoder = null;
                await TeardownActivePlaybackAsync(decoder);
            }
            if (VideoElement != null) VideoElement.Source = null;
            if (VideoViewbox != null) VideoViewbox.Visibility = Visibility.Collapsed;
            StopStreamingLoadingBlink();
            if (ImgSplash != null) ImgSplash.Visibility = Visibility.Visible;
            if (AudioUI != null) AudioUI.Visibility = Visibility.Collapsed;
            
            _currentFilePath = null;
            this.Title = "JonPlayer";
            TxtNowPlaying.Text = "Pick Your Vibe";
            _currentPlaybackOpenGen = 0;
            _activePlaybackFinishedHandler = null;
            _pendingPlaylistTarget = null;
            _isOpeningFile = false;
            Volatile.Write(ref _openingOwnedByGen, 0);
            UpdateWindowSizePresetUi();
            
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

            TitleBar.Opacity = 1.0;
            TimelineBar.Opacity = 1.0;
            ControlBar.Opacity = 1.0;
            TitleBar.BeginAnimation(UIElement.OpacityProperty, null);
            TimelineBar.BeginAnimation(UIElement.OpacityProperty, null);
            ControlBar.BeginAnimation(UIElement.OpacityProperty, null);

            _isChangingFullscreen = true;
            _isFullscreen = true;

            _prevWindowState = WindowState;
            _prevWindowStyle = WindowStyle;
            _prevResizeMode  = ResizeMode;
            _prevTopmost     = Topmost;

            System.Windows.Shell.WindowChrome.SetWindowChrome(this, null);

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
                if (_overlayWindow != null)
                {
                    var source = PresentationSource.FromVisual(this);
                    if (source != null)
                    {
                        var transform = source.CompositionTarget.TransformFromDevice;
                        Point physicalTopLeft = this.PointToScreen(new Point(0, 0));
                        Point logicalTopLeft = transform.Transform(physicalTopLeft);

                        _overlayWindow.Left = logicalTopLeft.X;
                        _overlayWindow.Top = logicalTopLeft.Y;
                        _overlayWindow.Width = this.ActualWidth;
                        _overlayWindow.Height = this.ActualHeight;
                    }
                }

                VideoGrid.UpdateLayout();
                PopupFsExit.IsOpen = false;
                FsBottomStrip.Visibility = Visibility.Collapsed;
                BtnFsCloseVideo.Visibility = Visibility.Visible;
                if (FsSubtitleShift != null)
                {
                    FsSubtitleShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
                    FsSubtitleShift.Y = -5;
                }
                _lastPolledMousePos = new Point(double.NaN, double.NaN);
                _fsMousePollTimer.Start();
                NotifyMouseActivity();
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

            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 40,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0, 0, 0, 1),
                UseAeroCaptionButtons = false,
                CornerRadius = new CornerRadius(0),
                NonClientFrameEdges = System.Windows.Shell.NonClientFrameEdges.None
            });

            Dispatcher.BeginInvoke(new Action(() =>
            {
                MainGrid.UpdateLayout();
                this.InvalidateVisual();
            }), System.Windows.Threading.DispatcherPriority.Render);

            PopupFsExit.IsOpen = false;
            FsBottomStrip.Visibility = Visibility.Collapsed;
            BtnFsCloseVideo.Visibility = Visibility.Collapsed;
            _fsMousePollTimer.Stop();
            RestoreVisibleCursor();
            if (FsSubtitleShift != null)
            {
                FsSubtitleShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
                FsSubtitleShift.Y = 0;
            }
        }

        private void BtnToggleMediaShortcuts_Click(object sender, RoutedEventArgs e)
        {
            if (MediaShortcutsPanel.Visibility == Visibility.Collapsed)
            {
                MediaShortcutsPanel.Visibility = Visibility.Visible;
                BasicShortcutsPanel.Visibility = Visibility.Collapsed;
                BtnToggleMediaShortcuts.Content = "◀ Basic Shortcuts";
            }
            else
            {
                MediaShortcutsPanel.Visibility = Visibility.Collapsed;
                BasicShortcutsPanel.Visibility = Visibility.Visible;
                BtnToggleMediaShortcuts.Content = "Media Filters ▶";
            }
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Handled) return;
            if (e.OriginalSource is WpfTextBox || e.OriginalSource is WpfComboBox) return;

            // Extra guard using current focus (in case OriginalSource check misses child windows/dialogs)
            var focused = System.Windows.Input.Keyboard.FocusedElement;
            if (focused is WpfTextBox || focused is WpfComboBox) return;

            bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool isShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            Key actualKey = e.Key == Key.System ? e.SystemKey : (e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key);

            switch (actualKey)
            {
                case Key.F1:
                    if (ShortcutsOverlay.Visibility != Visibility.Visible)
                    {
                        UpdateWindowSizePresetUi();
                        ShortcutsOverlay.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        ShortcutsOverlay.Visibility = Visibility.Collapsed;
                    }
                    e.Handled = true; break;

                case Key.Space:
                    if (!e.IsRepeat)
                    {
                        TogglePlayPause("space");
                    }
                    e.Handled = true;
                    break;
                case Key.Left: 
                    if (isCtrl) SeekRelative(-30);
                    else SeekRelative(-10); 
                    e.Handled = true; break;
                    
                case Key.Right: 
                    if (isCtrl) SeekRelative(30);
                    else SeekRelative(10); 
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
                
                case Key.M: 
                    if (isCtrl) { VideoScale.ScaleX = VideoScale.ScaleX == 1 ? -1 : 1; ShowToast("Mirror Mode (H-Flip): " + (VideoScale.ScaleX == -1 ? "ON" : "OFF")); }
                    else { ToggleMute(); }
                    e.Handled = true; break;

                case Key.F11: ToggleFullscreen(); e.Handled = true; break;

                case Key.MediaPlayPause:
                    if (!e.IsRepeat)
                    {
                        TogglePlayPause("media-key");
                    }
                    e.Handled = true;
                    break;
                case Key.MediaNextTrack: PlayNext(); e.Handled = true; break;
                case Key.MediaPreviousTrack: PlayPrev(); e.Handled = true; break;
                case Key.VolumeMute: ToggleMute(); e.Handled = true; break;
                case Key.VolumeUp: AdjustVolume(5); e.Handled = true; break;
                case Key.VolumeDown: AdjustVolume(-5); e.Handled = true; break;

                // --- Video & Audio Filter Shortcuts ---
                case Key.W: 
                    if (isCtrl) { CloseFile(); }
                    else if (isShift && !_isPipMode) { SubtitleTransform.Y -= 10; }
                    e.Handled = true; break;
                case Key.S: 
                    if (isShift && !_isPipMode) { SubtitleTransform.Y += 10; }
                    e.Handled = true; break;
                case Key.D:
                    if (isShift && !_isPipMode) { SubtitleTransform.X += 10; e.Handled = true; }
                    break;
                case Key.A:
                    if (isShift && !_isPipMode) { SubtitleTransform.X -= 10; e.Handled = true; }
                    break;
                case Key.R:
                    if (isShift && _decoder != null) { _decoder.SetAudioFilters(1.0, 0.0); ResetBassBoostToOff(); ShowToast("Audio Filters Reset"); e.Handled = true; }
                    break;
                case Key.F:
                    if (!isCtrl && !isShift) { FitScreen(); e.Handled = true; }
                    break;
                case Key.P:
                    if (!isCtrl && !isShift) { TogglePipMode(); e.Handled = true; }
                    break;
                case Key.H:
                    if (!isCtrl && !isShift) { ToggleEnhancedShader(); e.Handled = true; }
                    break;
                case Key.Q:
                    if (!isCtrl && !isShift && _decoder != null) { _decoder.SetVideoFilters(0.0, 1.0, 1.0); ShowToast("Video Colors Reset"); e.Handled = true; }
                    break;
                case Key.V:
                    if (isCtrl) { VideoScale.ScaleY = VideoScale.ScaleY == 1 ? -1 : 1; ShowToast("Vertical Flip: " + (VideoScale.ScaleY == -1 ? "ON" : "OFF")); e.Handled = true; }
                    else if (isShift && _decoder != null) { _decoder.SetAudioFilters(vocal: _decoder.AudioVocalGain == 0 ? 10.0 : 0.0); ShowToast($"Vocal Boost: {(_decoder.AudioVocalGain > 0 ? "ON" : "OFF")}"); e.Handled = true; }
                    break;
                case Key.B:
                    if (isShift) { BtnBass_Click(null, null); e.Handled = true; }
                    break;
                case Key.O:
                    if (isShift && !isCtrl && _decoder != null) { _decoder.SetAudioFilters(volume: _decoder.AudioVolumeLevel == 1.0 ? 3.0 : 1.0); ShowToast($"Overdrive Volume: {(_decoder.AudioVolumeLevel > 1.0 ? "ON" : "OFF")}"); e.Handled = true; }
                    else if (isCtrl && isShift) { OpenFolder(); e.Handled = true; }
                    else if (isCtrl) { OpenFile(); e.Handled = true; }
                    break;
                case Key.U:
                    if (isCtrl) { OpenUrl(); e.Handled = true; }
                    break;
                case Key.N:
                    if (isShift) { ShowToast("Denoise filter not available in SW/D3D11 yet."); e.Handled = true; }
                    break;
                
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
                            _playedIndices.Clear();
                            NavigateToPlaylistIndex(_playlist.IndexOf(item));
                            StartPlaylistHideTimer();
                        }
                    }
                    else
                    {
                        ToggleFullscreen();
                    }
                    e.Handled = true; break;
                
                case Key.F3: ToggleStatsOverlay(); e.Handled = true; break;

                case Key.F4:
                    ToggleHwAccel();
                    e.Handled = true;
                    break;
                
                case Key.Escape:
                    if (_isPipMode) { ExitPipMode(); e.Handled = true; break; }
                    if (_isFullscreen) { ExitFullscreen(); e.Handled = true; break; }
                    if (this.WindowState == WindowState.Maximized)
                    {
                        this.WindowState = WindowState.Normal;
                        ShowToast("창 복원");
                    }
                    else
                    {
                        this.Width = _initialWidth;
                        this.Height = _initialHeight;
                        // Center on current screen
                        var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
                        this.Left = screen.WorkingArea.Left + (screen.WorkingArea.Width - _initialWidth) / 2;
                        this.Top = screen.WorkingArea.Top + (screen.WorkingArea.Height - _initialHeight) / 2;
                        ShowToast("초기 크기로 복원");
                    }
                    e.Handled = true;
                    break;
                    
                    // Key.F merged above
                
                case Key.Oem3:
                    if (TryApplyWindowSizePreset(0.5)) ShowToast("창 크기: 50%");
                    e.Handled = true;
                    break;
                case Key.D1:
                case Key.NumPad1:
                    if (TryApplyWindowSizePreset(1.0)) ShowToast("창 크기: 100%");
                    e.Handled = true;
                    break;
                case Key.D2:
                case Key.NumPad2:
                    if (TryApplyWindowSizePreset(2.0)) ShowToast("창 크기: 200%");
                    e.Handled = true;
                    break;
                
                case Key.Z:
                    // Toggle video stretch/zoom mode. This affects the actual D3D renderer output (aspect correction in viewport).
                    // WPF Viewbox/Image updates kept for any fallback/legacy uses.
                    System.Windows.Media.Stretch newStretch;
                    string toastMsg;
                    if (_currentVideoStretch == System.Windows.Media.Stretch.Uniform) {
                        newStretch = System.Windows.Media.Stretch.UniformToFill;
                        toastMsg = "화면 맞춤: 가득 채우기 (자르기)";
                    } else if (_currentVideoStretch == System.Windows.Media.Stretch.UniformToFill) {
                        newStretch = System.Windows.Media.Stretch.Fill;
                        toastMsg = "화면 맞춤: 강제 늘림";
                    } else {
                        newStretch = System.Windows.Media.Stretch.Uniform;
                        toastMsg = "화면 맞춤: 원본 비율 (여백)";
                    }
                    _currentVideoStretch = newStretch;

                    if (VideoViewbox != null) VideoViewbox.Stretch = newStretch;
                    if (VideoElement != null) VideoElement.Stretch = newStretch;
                    if (_renderer != null) _renderer.StretchMode = newStretch;

                    ShowToast(toastMsg);
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
                
                    // Key.O merged above
                    

                    
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
                if (_decoder != null && _decoder.IsRunning) _statsTimer.Start();
            }
        }

        private DateTime _lastTimeUpdate = DateTime.MinValue;

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
            // WorkingSet64: 프로세스 실제 물리 메모리 사용량 (Task Manager와 유사). 오디오 전용 시 불필요한 D3D renderer 해제로 더 정확해짐.
            double memoryMb = process.WorkingSet64 / 1024.0 / 1024.0;
            int totalThreads = process.Threads.Count;

            double avgRender = _renderer?.LastRenderTimeMs ?? 0.0;

            var stats = _decoder.GetStats();
            string state = _decoder.IsPlaying ? "Playing" : "Paused";
            if (!_decoder.IsRunning) state = "Stopped";

            bool decodeHw = stats.IsRealHwAccel || _decoder.LastDecodedFrameIsHardware;
            stats.RendererMode = _renderer?.LastRendererMode ?? "—";
            if (!decodeHw)
            {
                stats.DecoderMode = stats.IsHwAccel ? "SW Fallback" : "Software";
            }

            string decodeMode = _forceSoftwareDecode ? "SW (CPU)" : "HW (D3D11VA)";
            stats.GpuUploadTimeMs = _renderer?.LastGpuUploadTimeMs ?? 0.0;
            if (_decoder.IsPlaying && _waveProvider != null && _waveProvider.BufferedDuration.TotalMilliseconds < 20.0)
            {
                _audioUnderrunCount++;
            }
            stats.AudioUnderrunCount = _audioUnderrunCount;
            
            var sbLeft = new StringBuilder();
            var sbRight = new StringBuilder();
            
            bool hasVideo = _decoder.HasVideo && !string.Equals(stats.VideoInfo, "No Video", StringComparison.OrdinalIgnoreCase);
            var infoParts = stats.VideoInfo?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string res = hasVideo && infoParts?.Length > 0 ? infoParts[0] : "No Video";
            string codec = hasVideo && infoParts?.Length > 1 ? infoParts[1] : "";
            if (!hasVideo)
            {
                var audioParts = stats.AudioInfo?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                codec = audioParts?.Length > 0 ? audioParts[^1] : "";
            }

            if (_isPipMode)
            {
                sbLeft.AppendLine(hasVideo ? "Video" : "Audio");
                sbLeft.AppendLine($"Codec  {codec}");
                if (hasVideo)
                {
                    sbLeft.AppendLine($"Size   {res}");
                    sbLeft.AppendLine($"FPS    {stats.VideoDecodeFps:F1}/{stats.TargetFps:F1}");
                    sbLeft.AppendLine($"Drop   {stats.DroppedFrames}");
                    sbLeft.AppendLine($"Decode {decodeMode}");
                }
                else
                {
                    sbLeft.AppendLine($"Audio  {stats.AudioInfo}");
                }
                sbLeft.AppendLine($"Decode {decodeMode}");
                sbLeft.AppendLine($"Queue  V{stats.VideoPacketQueueSize} A{stats.AudioPacketQueueSize}");
                if (hasVideo)
                {
                    sbLeft.AppendLine($"A/Vclk {stats.AvDiffMs:F0} ms");
                    sbLeft.AppendLine($"RawV-A {stats.AvSyncMs:F0} ms");
                    sbLeft.AppendLine($"Offset {stats.SyncDelayMs:F0} ms");
                    sbLeft.AppendLine("Master=Audio");
                }
                sbLeft.AppendLine($"CPU    {cpuUsage:F1}%");
                sbLeft.Append($"State  {state}");
                TxtOverlayStatsLeft.Text = sbLeft.ToString();
                TxtOverlayStatsRight.Text = "";
                return;
            }

            sbLeft.AppendLine(hasVideo ? "Video" : "Audio");
            sbLeft.AppendLine("────────────────────");
            sbLeft.AppendLine($"{"Codec".PadRight(12)}{codec}");
            if (hasVideo)
            {
                sbLeft.AppendLine($"{"Auto Gain".PadRight(12)}{_lastAutoGainDb:+0.0;-0.0;0.0} dB");
                if (!double.IsNaN(_lastDetectedMeanDb))
                    sbLeft.AppendLine($"{"Mean Vol".PadRight(12)}{_lastDetectedMeanDb:F1} dB");
                sbLeft.AppendLine($"{"Resolution".PadRight(12)}{res}");
                sbLeft.AppendLine($"{"Reader FPS".PadRight(12)}{stats.ReaderFps:F1}");
                sbLeft.AppendLine($"{"V.DecodeFPS".PadRight(12)}{stats.VideoDecodeFps:F1} / {stats.TargetFps:F1}");
                sbLeft.AppendLine($"{"A.DecodeFPS".PadRight(12)}{stats.AudioDecodeFps:F1}");
                sbLeft.AppendLine($"{"DisplayFPS".PadRight(12)}{_renderer?.PresentedFps ?? 0:F1}");
                sbLeft.AppendLine($"{"Dropped".PadRight(12)}{stats.DroppedFrames}");
                sbLeft.AppendLine($"{"A.Underrun".PadRight(12)}{stats.AudioUnderrunCount}");
            }
            else
            {
                sbLeft.AppendLine($"{"Format".PadRight(12)}{stats.AudioInfo}");
            }
            sbLeft.AppendLine($"{"Bitrate".PadRight(12)}{stats.Bitrate / 1000} kbps");
            if (hasVideo)
            {
                sbLeft.AppendLine($"{"Dec Mode".PadRight(12)}{stats.DecoderMode}");
                sbLeft.AppendLine($"{"Ren Mode".PadRight(12)}{stats.RendererMode}");
                sbLeft.AppendLine($"{"Decode Pref".PadRight(12)}{decodeMode}");
            }
            sbLeft.AppendLine();
            
            sbLeft.AppendLine("System");
            sbLeft.AppendLine("────────────────────");
            sbLeft.AppendLine($"{"CPU".PadRight(12)}{cpuUsage:F1}%");
            sbLeft.AppendLine($"{"Memory".PadRight(12)}{memoryMb:F0} MB");
            sbLeft.AppendLine($"{"Threads".PadRight(12)}{totalThreads}");
            sbLeft.AppendLine($"{"Decode".PadRight(12)}{decodeMode}");
            sbLeft.AppendLine($"{"Auto Gain".PadRight(12)}{_lastAutoGainDb:+0.0;-0.0;0.0} dB");
            if (!double.IsNaN(_lastDetectedMeanDb))
                sbLeft.AppendLine($"{"Mean Vol".PadRight(12)}{_lastDetectedMeanDb:F1} dB (tgt {AutoVolumeTargetMeanDb:F0})");
            int pref = _userPreferredVolume;
            sbLeft.AppendLine($"{"Slider".PadRight(12)}{(int)(SliderVolume?.Value ?? 0)}% (pref {pref}% ±{AutoVolumeMaxOffset}%)");
            sbLeft.AppendLine();

            sbLeft.AppendLine("Status Session");
            sbLeft.AppendLine("────────────────────");
            sbLeft.AppendLine($"{"State".PadRight(12)}{state}");
            sbLeft.AppendLine($"{"OpenCount".PadRight(12)}{_openCount}");
            sbLeft.AppendLine($"{"SeekCount".PadRight(12)}{_seekCount}");


            sbRight.AppendLine("Performance");
            sbRight.AppendLine("────────────────────");
            sbRight.AppendLine($"{"V.Decode".PadRight(12)}{stats.VideoDecodeTimeMs:F1} ms");
            sbRight.AppendLine($"{"A.Decode".PadRight(12)}{stats.AudioDecodeTimeMs:F1} ms");
            if (!stats.IsRealHwAccel && stats.GpuUploadTimeMs > 0.0) sbRight.AppendLine($"{"GPU Upld".PadRight(12)}{stats.GpuUploadTimeMs:F1} ms");
            if (!stats.IsRealHwAccel && stats.SwsConvertTimeMs > 0.0) sbRight.AppendLine($"{"SwsScale".PadRight(12)}{stats.SwsConvertTimeMs:F1} ms");
            sbRight.AppendLine($"{"Render".PadRight(12)}{avgRender:F1} ms");
            sbRight.AppendLine($"{"PoolWait".PadRight(12)}{stats.SurfacePoolWaitTimeMs:F1} ms");
            sbRight.AppendLine($"{"Total".PadRight(12)}{(stats.AvgDecodeTimeMs + avgRender):F1} ms");
            sbRight.AppendLine();

            sbRight.AppendLine("Queue");
            sbRight.AppendLine("────────────────────");
            sbRight.AppendLine($"{"V.PacketQ".PadRight(12)}{stats.VideoPacketQueueSize}");
            sbRight.AppendLine($"{"A.PacketQ".PadRight(12)}{stats.AudioPacketQueueSize}");
            sbRight.AppendLine($"{"V.FrameQ".PadRight(12)}{stats.VideoFrameQueueSize}");
            sbRight.AppendLine($"{"A.FrameQ".PadRight(12)}{stats.AudioFrameQueueSize}");
            sbRight.AppendLine();

            if (hasVideo)
            {
                sbRight.AppendLine("Sync");
                sbRight.AppendLine("────────────────────");
                sbRight.AppendLine($"{"V.Disp PTS".PadRight(12)}{stats.VideoPts:F0} ms");
                sbRight.AppendLine($"{"V.Dec PTS".PadRight(12)}{stats.VideoDecodePts:F0} ms");
                sbRight.AppendLine($"{"Audio PTS".PadRight(12)}{stats.AudioPts:F0} ms");
                sbRight.AppendLine($"{"MasterClk".PadRight(12)}{stats.MasterClock:F0} ms");
                sbRight.AppendLine($"{"A/V clock".PadRight(12)}{stats.AvDiffMs:F0} ms");
                sbRight.AppendLine($"{"Raw V-A".PadRight(12)}{stats.AvSyncMs:F0} ms");
                sbRight.AppendLine($"{"Offset".PadRight(12)}{stats.SyncDelayMs:F0} ms");
                sbRight.AppendLine($"{"DecodeLead".PadRight(12)}{stats.DecodeLeadMs:F0} ms");
                sbRight.AppendLine($"{"LateFrames".PadRight(12)}{stats.LateFrames}");
            }

            TxtOverlayStatsLeft.Text = sbLeft.ToString();
            TxtOverlayStatsRight.Text = sbRight.ToString();
        }
        private bool _isDraggingSubtitle = false;
        private System.Windows.Point _subtitleLastMousePos;

        private void SubtitleBorder_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isPipMode) return;

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
            if (_isPipMode) return;

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


        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // All cleanup is handled in the Closed event handler (constructor)
        }
    }
}

