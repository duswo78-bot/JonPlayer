import re

with open('MainWindow.xaml.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# 1. Remove _mainWindowSyncHookAttached and _isSyncing
content = re.sub(r'        private bool _mainWindowSyncHookAttached;\s*', '', content)
content = re.sub(r'        private bool _isSyncing = false;\s*', '', content)

# 2. Remove AttachMainWindowSyncHook and MainWindowSyncHook
content = re.sub(r'        private void AttachMainWindowSyncHook\(\)\s*\{[^{}]*\}\s*', '', content)
content = re.sub(r'        private IntPtr MainWindowSyncHook\(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled\)\s*\{[^{}]*\}\s*', '', content)

# 3. Replace SyncOverlayWindowToMainWindow and SyncMainWindowToOverlay
sync_pattern = r'        private void SyncOverlayWindowToMainWindow\(\)\s*\{[^{}]*\}\s*'
content = re.sub(sync_pattern, '', content)
content = re.sub(r'        private void SyncMainWindowToOverlay\(\)\s*\{[^{}]*\}\s*', '', content)

sync_new = r'''        private void SyncMainWindowToOverlay()
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
        }
'''
content = content.replace('        private void BeginMainWindowDrag()', sync_new + '\n        private void BeginMainWindowDrag()')

# 4. Remove AttachMainWindowSyncHook call from constructor/SetupOverlayWindow
content = content.replace('this.SourceInitialized += (s, e) => AttachMainWindowSyncHook();\n', '')
content = content.replace('AttachMainWindowSyncHook();\n', '')

# 5. Fix _overlayWindow creation to use 1 pixel padding
content = content.replace('Width = double.IsNaN(this.Width) ? 800 : this.Width,', 'Width = double.IsNaN(this.Width) ? 800 : this.Width + 2,')
content = content.replace('Height = double.IsNaN(this.Height) ? 450 : this.Height,', 'Height = double.IsNaN(this.Height) ? 450 : this.Height + 2,')
content = content.replace('MinWidth = this.MinWidth,', 'MinWidth = this.MinWidth + 2,')
content = content.replace('MinHeight = this.MinHeight,', 'MinHeight = this.MinHeight + 2,')
content = content.replace('Left = double.IsNaN(this.Left) ? 0 : this.Left,', 'Left = double.IsNaN(this.Left) ? 0 : this.Left - 1,')
content = content.replace('Top = double.IsNaN(this.Top) ? 0 : this.Top,', 'Top = double.IsNaN(this.Top) ? 0 : this.Top - 1,')

# Add missing event hooks for _overlayWindow if they aren't there
if '_overlayWindow.LocationChanged +=' not in content:
    content = content.replace('_overlayWindow.DragOver +=', '_overlayWindow.LocationChanged += (s, e) => SyncMainWindowToOverlay();\n            _overlayWindow.SizeChanged += (s, e) => SyncMainWindowToOverlay();\n\n            _overlayWindow.DragOver +=')

# 6. Fix EnterFullscreen/ExitFullscreen Taskbar issue
enter_fullscreen_old = r'''        private void EnterFullscreen\(\)
        \{
            if \(_isFullscreen\) return;

            _isChangingFullscreen = true;
            _isFullscreen = true;

            _prevWindowState = WindowState;
            _prevWindowStyle = WindowStyle;
            _prevResizeMode  = ResizeMode;
            _prevTopmost     = Topmost;

            System\.Windows\.Shell\.WindowChrome\.SetWindowChrome\(this, null\);

            WindowStyle  = WindowStyle\.None;
            ResizeMode   = ResizeMode\.NoResize;
            
            WindowState  = WindowState\.Normal;
            WindowState  = WindowState\.Maximized;
            Topmost      = true;

            RowTitleBar\.Height = new GridLength\(0\);
            RowTimeline\.Height = new GridLength\(0\);
            RowControls\.Height = new GridLength\(0\);

            MainGrid\.Margin = new Thickness\(0\);
            _isChangingFullscreen = false;'''
enter_fullscreen_new = r'''        private void EnterFullscreen()
        {
            if (_isFullscreen) return;

            _isChangingFullscreen = true;
            _isFullscreen = true;

            _prevWindowState = WindowState;
            _prevWindowStyle = WindowStyle;
            _prevResizeMode  = ResizeMode;
            _prevTopmost     = Topmost;
            _backupBounds = new Rect(this.Left, this.Top, this.Width, this.Height);

            System.Windows.Shell.WindowChrome.SetWindowChrome(this, null);

            WindowStyle  = WindowStyle.None;
            ResizeMode   = ResizeMode.NoResize;
            
            WindowState  = WindowState.Normal;
            Topmost      = true;

            var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            this.Left = screen.WorkingArea.Left;
            this.Top = screen.WorkingArea.Top;
            this.Width = screen.WorkingArea.Width;
            this.Height = screen.WorkingArea.Height;

            RowTitleBar.Height = new GridLength(0);
            RowTimeline.Height = new GridLength(0);
            RowControls.Height = new GridLength(0);

            MainGrid.Margin = new Thickness(0);
            _isChangingFullscreen = false;'''
content = re.sub(enter_fullscreen_old, enter_fullscreen_new, content, count=1)

exit_fullscreen_old = r'''        private void ExitFullscreen\(\)
        \{
            if \(!_isFullscreen\) return;

            _isChangingFullscreen = true;
            _isFullscreen = false;

            PopupFsExit\.IsOpen = false;

            WindowStyle  = _prevWindowStyle;
            ResizeMode   = _prevResizeMode;
            WindowState  = _prevWindowState;
            Topmost      = _prevTopmost;'''
exit_fullscreen_new = r'''        private void ExitFullscreen()
        {
            if (!_isFullscreen) return;

            _isChangingFullscreen = true;
            _isFullscreen = false;

            PopupFsExit.IsOpen = false;

            this.Left = _backupBounds.Left;
            this.Top = _backupBounds.Top;
            this.Width = _backupBounds.Width;
            this.Height = _backupBounds.Height;

            WindowStyle  = _prevWindowStyle;
            ResizeMode   = _prevResizeMode;
            WindowState  = _prevWindowState;
            Topmost      = _prevTopmost;'''
content = re.sub(exit_fullscreen_old, exit_fullscreen_new, content, count=1)

with open('MainWindow.xaml.cs', 'w', encoding='utf-8') as f:
    f.write(content)

print("Done modifications!")
