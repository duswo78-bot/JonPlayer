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
using System.Diagnostics;

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
        private FFmpegVideoDecoder? _decoder;

        private bool _isUserDraggingSlider;
        private bool _isUpdatingFromPlayer;

        private int   _lastVolume    = 80;
        private bool  _isMuted;
        private bool  _isLightTheme = false;
        private double _currentSpeed  = 1.0f;

        private bool        _isFullscreen;
        private WindowState _prevWindowState;
        private WindowStyle _prevWindowStyle;
        private ResizeMode  _prevResizeMode;

        private DispatcherTimer _fsMousePollTimer;
        private DispatcherTimer _statsTimer;

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

        public MainWindow()
        {
            InitializeComponent();

            this.StateChanged += Window_StateChanged;

            _fsMousePollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _fsMousePollTimer.Tick += FsMousePollTimer_Tick;

            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _statsTimer.Tick += StatsTimer_Tick;

            _lastCpuTime = Process.GetCurrentProcess().TotalProcessorTime;
            _lastCpuCheckTime = DateTime.UtcNow;

            ApplyTheme(false);
        }

        private void Decoder_FrameDecoded(IntPtr bgraPointer, int width, int height, int stride)
        {
            _renderTimer.Restart();
            _renderer?.ResetSize(width, height);
            _renderer?.RenderFrame(bgraPointer, stride);
            _renderTimer.Stop();

            _totalRenderTimeMs += _renderTimer.Elapsed.TotalMilliseconds;
            _renderSamples++;
        }

        private void Decoder_PositionChanged(double ratio)
        {
            if (_isUserDraggingSlider) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isUserDraggingSlider || _decoder == null || !_decoder.IsRunning) return;
                _isUpdatingFromPlayer = true;
                SliderTimeline.Value = ratio * SliderTimeline.Maximum;
                if (SliderTimelineFS != null) SliderTimelineFS.Value = SliderTimeline.Value;
                _isUpdatingFromPlayer = false;
            }));
        }

        private void Decoder_TimeUpdated(TimeSpan current, TimeSpan total)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_decoder == null || !_decoder.IsRunning) return;
                TxtCurrentTime.Text = current.ToString(@"hh\:mm\:ss");
                TxtTotalTime.Text = total.ToString(@"hh\:mm\:ss");
                if (TxtCurrentTimeFS != null) TxtCurrentTimeFS.Text = TxtCurrentTime.Text;
                if (TxtTotalTimeFS != null) TxtTotalTimeFS.Text = TxtTotalTime.Text;
            }));
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
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
                SetBrush("TextBrush", 0x1C, 0x1C, 0x1E);
                SetBrush("TextMutedBrush", 0x8E, 0x8E, 0x93);
                SetBrush("AccentBrush", 0x00, 0x7A, 0xFF);
                SetBrush("HoverBrush", 0xE5, 0xE5, 0xEA);
                SetBrush("ActiveBrush", 0xD1, 0xD1, 0xD6);
                SetBrush("DividerBrush", 0xD8, 0xD8, 0xDC);

                var knob = MakeKnob(0xF2, 0xF2, 0xF7, 0xE5, 0xE5, 0xEA, 0xC7, 0xC7, 0xCC, 0xAE, 0xAE, 0xB2);
                System.Windows.Application.Current.Resources["KnobBgBrush"] = knob;

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

                var knob = MakeKnob(0x4E, 0x4E, 0x52, 0x24, 0x24, 0x26, 0x5E, 0x5E, 0x62, 0x2C, 0x2C, 0x2F);
                System.Windows.Application.Current.Resources["KnobBgBrush"] = knob;

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
            System.Windows.Application.Current.Resources[key] = brush;
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

        private void OpenFile()
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Open Media File",
                Filter = "Video Files (*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.ts;*.m2ts)|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.ts;*.m2ts|Audio Files (*.mp3;*.flac;*.wav;*.aac;*.ogg;*.m4a)|*.mp3;*.flac;*.wav;*.aac;*.ogg;*.m4a|All Files (*.*)|*.*",
            };
            
            if (!string.IsNullOrEmpty(_lastOpenDirectory))
                dlg.InitialDirectory = _lastOpenDirectory;
            else
                dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            if (dlg.ShowDialog() == true)
            {
                _lastOpenDirectory = Path.GetDirectoryName(dlg.FileName);
                PlayFile(dlg.FileName);
            }
        }

        private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) e.Effects = System.Windows.DragDropEffects.Copy;
            else e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
            var files = e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            PlayFile(files[0]);
        }

        private string? _currentFilePath;

        private void PlayFile(string path)
        {
            _currentFilePath = path;
            _openCount++;

            if (_renderer == null)
            {
                _renderer = new D3D11VideoRenderer();
                VideoElement.Source = _renderer.D3DImage;
            }

            if (_decoder == null)
            {
                _decoder = new FFmpegVideoDecoder();
                _decoder.FrameDecoded += Decoder_FrameDecoded;
                _decoder.PositionChanged += Decoder_PositionChanged;
                _decoder.TimeUpdated += Decoder_TimeUpdated;
            }

            try
            {
                _decoder.PlaybackFinished -= Decoder_PlaybackFinished;
                _decoder.PlaybackFinished += Decoder_PlaybackFinished;
                _decoder.Open(path);
                _decoder.Play();

                var name = Path.GetFileName(path);
                TxtNowPlaying.Text = name;
                Title = $"JonPlayer — {name}";

                if (VideoElement != null) VideoElement.Visibility = Visibility.Visible;
                if (ImgSplash != null) ImgSplash.Visibility = Visibility.Collapsed;

                UpdatePlayPauseUI(true);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"파일을 열 수 없습니다.\n{ex.Message}", "JonPlayer", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnPlayPause_Click   (object sender, RoutedEventArgs e) => TogglePlayPause();
        private void BtnStop_Click        (object sender, RoutedEventArgs e)
        {
            _decoder?.Stop();
            UpdatePlayPauseUI(false);
            if (VideoElement != null) VideoElement.Visibility = Visibility.Collapsed;
            if (ImgSplash != null) ImgSplash.Visibility = Visibility.Visible;
            _isUpdatingFromPlayer = true;
            SliderTimeline.Value = 0;
            if (SliderTimelineFS != null) SliderTimelineFS.Value = 0;
            _isUpdatingFromPlayer = false;
            TxtCurrentTime.Text = "00:00:00";
            if (TxtCurrentTimeFS != null) TxtCurrentTimeFS.Text = "00:00:00";
            TxtNowPlaying.Text = "No file loaded";
            Title = "JonPlayer";
        }
        private void BtnSkipBack_Click    (object sender, RoutedEventArgs e) => SeekRelative(-10);
        private void BtnSkipForward_Click (object sender, RoutedEventArgs e) => SeekRelative( 10);
        
        private void BtnPrev_Click(object sender, RoutedEventArgs e)
        {
            WpfMessageBox.Show("플레이리스트 다중 재생 기능은 향후 업데이트될 예정입니다.", "JonPlayer", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            WpfMessageBox.Show("플레이리스트 다중 재생 기능은 향후 업데이트될 예정입니다.", "JonPlayer", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void TogglePlayPause()
        {
            if (_decoder == null) return;
            if (_decoder.IsPlaying)
            {
                _decoder.Pause();
                UpdatePlayPauseUI(false);
            }
            else
            {
                if (!_decoder.IsRunning)
                {
                    if (!string.IsNullOrEmpty(_currentFilePath))
                        PlayFile(_currentFilePath);
                    return;
                }
                
                _decoder.Play();
                UpdatePlayPauseUI(true);
            }
        }

        private void UpdatePlayPauseUI(bool isPlaying)
        {
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

        private void SeekRelative(double offsetSeconds)
        {
            if (_decoder == null || !_decoder.IsRunning) return;
            if (TimeSpan.TryParse(TxtTotalTime.Text, out TimeSpan total))
            {
                double totalSeconds = total.TotalSeconds;
                if (totalSeconds <= 0) return;
                double currentRatio = SliderTimeline.Value / 1000.0;
                double currentSeconds = currentRatio * totalSeconds;
                double targetSeconds = Math.Clamp(currentSeconds + offsetSeconds, 0, totalSeconds);
                _decoder.Seek(targetSeconds / totalSeconds);
                _seekCount++;
            }
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

        private void MenuItemSpeed_Click(object sender, RoutedEventArgs e)
        {
            if (_decoder == null) return;
            if (sender is System.Windows.Controls.MenuItem mi && mi.Tag != null
                && double.TryParse(mi.Tag.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double speed))
            {
                _currentSpeed = speed;
                _decoder.SetSpeed(speed);
                string label = $"{mi.Header} ▾";
                BtnSpeed.Content = label;
                if (BtnSpeedFS != null) BtnSpeedFS.Content = label;
            }
        }

        private void SliderTimeline_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is System.Windows.Controls.Primitives.Thumb)
            {
                _isUserDraggingSlider = true;
                return;
            }

            if (sender is Slider slider)
            {
                double ratio = e.GetPosition(slider).X / slider.ActualWidth;
                double targetValue = Math.Clamp(ratio * slider.Maximum, 0, slider.Maximum);
                
                _isUserDraggingSlider = true;
                slider.Value = targetValue;
                DoSeek(targetValue);
                _isUserDraggingSlider = false;

                e.Handled = true;
            }
        }

        private void SliderTimeline_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            _isUserDraggingSlider = false;
            if (sender is Slider slider) DoSeek(slider.Value);
        }

        private void SliderTimelineFS_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is System.Windows.Controls.Primitives.Thumb)
            {
                _isUserDraggingSlider = true;
                return;
            }

            if (sender is Slider slider)
            {
                double ratio = e.GetPosition(slider).X / slider.ActualWidth;
                double targetValue = Math.Clamp(ratio * slider.Maximum, 0, slider.Maximum);
                
                _isUserDraggingSlider = true;
                slider.Value = targetValue;
                DoSeek(targetValue);
                _isUserDraggingSlider = false;

                e.Handled = true;
            }
        }

        private void SliderTimelineFS_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            _isUserDraggingSlider = false;
            if (sender is Slider slider) DoSeek(slider.Value);
        }

        private void Decoder_PlaybackFinished()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                BtnStop_Click(this, new RoutedEventArgs());
            }));
        }

        private void SliderTimeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingFromPlayer) return;
            if (SliderTimelineFS != null)
            {
                _isUpdatingFromPlayer = true;
                SliderTimelineFS.Value = SliderTimeline.Value;
                _isUpdatingFromPlayer = false;
            }
        }

        private void SliderTimelineFS_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingFromPlayer) return;
            _isUpdatingFromPlayer = true;
            SliderTimeline.Value = SliderTimelineFS.Value;
            _isUpdatingFromPlayer = false;
        }

        private void DoSeek(double sliderValue)
        {
            _decoder?.Seek(sliderValue / 1000.0);
            _seekCount++;
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
        }

        private void AdjustVolume(int delta) => SliderVolume.Value = Math.Clamp(SliderVolume.Value + delta, 0, 100);

        private void BtnFullscreen_Click    (object sender, RoutedEventArgs e) => EnterFullscreen();
        private void BtnExitFullscreen_Click(object sender, RoutedEventArgs e) => ExitFullscreen();

        private void ToggleFullscreen()
        {
            if (_isFullscreen) ExitFullscreen();
            else EnterFullscreen();
        }

        private void EnterFullscreen()
        {
            if (_isFullscreen) return;

            _isFullscreen = true;

            _prevWindowState = WindowState;
            _prevWindowStyle = WindowStyle;
            _prevResizeMode  = ResizeMode;

            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = new Thickness(-1),
                CornerRadius = new CornerRadius(0)
            });

            WindowStyle  = WindowStyle.None;
            ResizeMode   = ResizeMode.NoResize;
            
            WindowState  = WindowState.Normal;
            WindowState  = WindowState.Maximized;

            RowTitleBar.Height = new GridLength(0);
            RowTimeline.Height = new GridLength(0);
            RowControls.Height = new GridLength(0);

            MainGrid.Margin = new Thickness(0);

            if (SliderVolumeFS   != null) SliderVolumeFS.Value   = SliderVolume.Value;
            if (SliderTimelineFS != null) SliderTimelineFS.Value  = SliderTimeline.Value;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                VideoGrid.UpdateLayout();
                PopupFsExit.IsOpen = false;
                FsBottomStrip.Visibility = Visibility.Collapsed;
                _fsMousePollTimer.Start();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ExitFullscreen()
        {
            if (!_isFullscreen) return;
            _isFullscreen = false;

            RowTitleBar.Height = new GridLength(40);
            RowTimeline.Height = GridLength.Auto;
            RowControls.Height = GridLength.Auto;

            WindowStyle = _prevWindowStyle;
            WindowState = _prevWindowState;
            ResizeMode  = _prevResizeMode;

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

            try
            {
                WindowChrome.SetWindowChrome(this, new WindowChrome
                {
                    CaptionHeight         = 40,
                    ResizeBorderThickness = new Thickness(6),
                    GlassFrameThickness   = new Thickness(-1),
                    CornerRadius          = new CornerRadius(0)
                });
            }
            catch { }

            PopupFsExit.IsOpen = false;
            FsBottomStrip.Visibility = Visibility.Collapsed;
            _fsMousePollTimer.Stop();
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.OriginalSource is WpfTextBox || e.OriginalSource is WpfComboBox) return;

            switch (e.Key)
            {
                case Key.Space: TogglePlayPause(); e.Handled = true; break;
                case Key.Left: SeekRelative(-10); e.Handled = true; break;
                case Key.Right: SeekRelative( 10); e.Handled = true; break;
                case Key.Up: AdjustVolume( 5); e.Handled = true; break;
                case Key.Down: AdjustVolume(-5); e.Handled = true; break;
                case Key.M: ToggleMute(); e.Handled = true; break;
                case Key.F11: ToggleFullscreen(); e.Handled = true; break;
                case Key.F3: ToggleStatsOverlay(); e.Handled = true; break;
                case Key.Escape:
                    if (_isFullscreen) { ExitFullscreen(); e.Handled = true; }
                    break;
                case Key.O:
                    if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    { OpenFile(); e.Handled = true; }
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
            sb.AppendLine($"{"Dropped".PadRight(12)}0");
            sb.AppendLine();
            
            sb.AppendLine("Performance");
            sb.AppendLine("────────────────────");
            sb.AppendLine($"{"Decode".PadRight(12)}{stats.AvgDecodeTimeMs:F1} ms");
            sb.AppendLine($"{"Render".PadRight(12)}{avgRender:F1} ms");
            sb.AppendLine($"{"Total".PadRight(12)}{(stats.AvgDecodeTimeMs + avgRender):F1} ms");
            sb.AppendLine();
            
            sb.AppendLine("Buffer");
            sb.AppendLine("────────────────────");
            sb.AppendLine($"{"PacketQ".PadRight(12)}0");
            sb.AppendLine($"{"FrameQ".PadRight(12)}0");
            sb.AppendLine();
            
            sb.AppendLine("Sync");
            sb.AppendLine("────────────────────");
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

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // No custom Hwnd sync is needed as WPF native Image adapts automatically.
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _fsMousePollTimer.Stop();

            if (_decoder != null)
            {
                _decoder.Dispose();
                _decoder = null;
            }

            if (_renderer != null)
            {
                _renderer.Dispose();
                _renderer = null;
            }
        }
    }
}