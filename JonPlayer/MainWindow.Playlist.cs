using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Point = System.Windows.Point;
using WpfButton = System.Windows.Controls.Button;

namespace JonPlayer
{
    public partial class MainWindow
    {
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

        private void OpenUrl()
        {
            var dlg = new InputWindow();
            dlg.Owner = this;
            dlg.Resources = this.Resources;
            if (dlg.ShowDialog() == true)
            {
                var url = dlg.InputUrl;
                if (!string.IsNullOrEmpty(url))
                {
                    if (IsStreamingPath(url))
                    {
                        if (TxtNowPlaying != null)
                            TxtNowPlaying.Text = $"Loading... {url}";
                        StartStreamingLoadingBlink();
                    }
                    LoadPlaylist(new[] { url });
                }
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
            if (_isClosing) return;

            for (int i = 0; i < _playlist.Count; i++)
                _playlist[i].IsCurrentlyPlaying = (i == _playlistIndex);

            // Update count badge
            if (TxtPlaylistCount != null)
                TxtPlaylistCount.Text = _playlist.Count.ToString();

            // Scroll to current item — guard against null during close / early calls
            if (_playlistIndex >= 0 && _playlistIndex < _playlist.Count && ListPlaylist != null)
                ListPlaylist.ScrollIntoView(_playlist[_playlistIndex]);
        }

        private static readonly HashSet<string> _allowedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm",
            ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a"
        };

        private async void LoadPlaylist(string[] files)
        {
            if (_isClosing) return;

            _playlist.Clear();
            _playedIndices.Clear();
            _pendingPlaylistTarget = null;

            var youtube = new YoutubeExplode.YoutubeClient();

            foreach (var f in files)
            {
                if (_isClosing) return;

                string path = f;
                string? audioPath = null;
                string? youtubeUrl = null;
                bool isYoutube = f.Contains("youtube.com") || f.Contains("youtu.be");
                string title = isYoutube ? f : System.IO.Path.GetFileName(f);

                if (isYoutube)
                {
                    try
                    {
                        var video = await youtube.Videos.GetAsync(f);
                        if (_isClosing) return;
                        title = video.Title;
                        var streamUrl = await _streamingService.GetStreamUrlAsync(f);
                        if (_isClosing) return;
                        if (streamUrl != null)
                        {
                            path = streamUrl;
                            audioPath = _streamingService.LastAudioUrl;
                            youtubeUrl = f;
                        }
                        else
                        {
                            System.Windows.MessageBox.Show("해당 스트리밍 영상의 스트리밍 주소를 가져올 수 없습니다.\n(제한된 영상이거나 정책 변경으로 인해 차단되었을 수 있습니다.)", "스트리밍 오류", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Windows.MessageBox.Show($"스트리밍 영상을 불러오는데 실패했습니다:\n{ex.Message}", "스트리밍 오류", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                        continue;
                    }
                }

                bool isUrl = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase);

                string ext = System.IO.Path.GetExtension(f);
                if (isUrl || _allowedExts.Contains(ext))
                {
                    _playlist.Add(new PlaylistItem { Name = isUrl && !isYoutube ? f : title, Path = path, AudioPath = audioPath, YoutubeUrl = youtubeUrl });
                }
            }

            if (ListPlaylist != null)
                ListPlaylist.ItemsSource = _playlist;

            if (_isClosing) return;

            if (_playlist.Count > 0)
            {
                if (_isClosing) return;
                // Use navigator so initial load also respects the "finish previous before next" rule
                // (though for fresh load it is usually not opening).
                NavigateToPlaylistIndex(0);

                if (_playlist.Count > 1)
                {
                    if (BtnPlaylistToggle != null) BtnPlaylistToggle.IsEnabled = true;
                    if (BtnPlaylistToggleFS != null) BtnPlaylistToggleFS.IsEnabled = true;
                    ShowPlaylistBriefly();
                }
            }
            else
            {
                StopStreamingLoadingBlink();
            }

            if (TxtPlaylistCount != null)
                TxtPlaylistCount.Text = _playlist.Count.ToString();
        }

        private void ListPlaylist_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ListPlaylist != null && ListPlaylist.SelectedItem is PlaylistItem item)
            {
                _playedIndices.Clear();
                NavigateToPlaylistIndex(_playlist.IndexOf(item));
            }
        }

        private void ListPlaylist_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (ListPlaylist != null && e.Key == Key.Delete && ListPlaylist.SelectedItem is PlaylistItem item)
            {
                int idx = _playlist.IndexOf(item);
                if (idx != -1)
                {
                    _playlist.RemoveAt(idx);
                    if (idx == _playlistIndex)
                    {
                        if (_playlist.Count > 0)
                        {
                            NavigateToPlaylistIndex(idx % _playlist.Count);
                        }
                        else
                        {
                            _playlistIndex = -1;
                            _pendingPlaylistTarget = null;
                            UpdateNowPlayingHighlight();
                            CloseFile();
                        }
                    }
                    else if (idx < _playlistIndex)
                    {
                        _playlistIndex--;
                    }
                }
                UpdateNowPlayingHighlight();
                if (_playlist.Count <= 1)
                {
                    if (BtnPlaylistToggle != null) BtnPlaylistToggle.IsEnabled = false;
                    if (BtnPlaylistToggleFS != null) BtnPlaylistToggleFS.IsEnabled = false;
                    if (PlaylistOverlay != null) PlaylistOverlay.Visibility = Visibility.Collapsed;
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
            if (PlaylistOverlay == null) return;

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
            if (PlaylistOverlay != null)
            {
                PlaylistOverlay.Visibility = Visibility.Visible;
                StartPlaylistHideTimer();
            }
        }

        private void StartPlaylistHideTimer()
        {
            if (PlaylistOverlay == null) return;

            _playlistTimer?.Stop();
            _playlistTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _playlistTimer.Tick += (s, e) =>
            {
                if (PlaylistOverlay != null && !_isPlaylistHovered && !PlaylistOverlay.IsKeyboardFocusWithin)
                {
                    PlaylistOverlay.Visibility = Visibility.Collapsed;
                }
                _playlistTimer?.Stop();
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
                if (BtnPlaylistToggle != null) BtnPlaylistToggle.IsEnabled = _playlist.Count > 1;
                if (BtnPlaylistToggleFS != null) BtnPlaylistToggleFS.IsEnabled = _playlist.Count > 1;
                UpdateNowPlayingHighlight();
            }
        }

        private void BtnPlaylistClose_Click(object sender, RoutedEventArgs e)
        {
            if (PlaylistOverlay != null)
                PlaylistOverlay.Visibility = Visibility.Collapsed;
        }

        private void PlaylistOverlay_DragStart(object sender, MouseButtonEventArgs e)
        {
            if (PlaylistOverlay == null || VideoGrid == null) return;
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
            if (!_isPlaylistDragging || VideoGrid == null || PlaylistOverlay == null) return;
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
            if (NextVideoOverlay != null)
                NextVideoOverlay.Visibility = Visibility.Collapsed;

            if (_playlist.Count > 1 && _playlistIndex >= 0 && _playlistIndex < _playlist.Count - 1)
            {
                NavigateToPlaylistIndex(_playlistIndex + 1);
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
            UpdateNowPlayingHighlight();
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
            UpdateNowPlayingHighlight();
        }
    }
}