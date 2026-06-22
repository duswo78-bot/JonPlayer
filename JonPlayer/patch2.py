import re

with open('MainWindow.xaml.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# 1. Update FitScreen
fitscreen_old = r'''        private void FitScreen\(\)
        \{
            if \(_isFullscreen\) ExitFullscreen\(\);
            this\.WindowState = this\.WindowState == WindowState\.Maximized \? WindowState\.Normal : WindowState\.Maximized;
        \}'''
fitscreen_new = r'''        private bool _isFitScreen = false;
        private Rect _backupNormalBounds;

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
                var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
                
                var source = PresentationSource.FromVisual(this);
                if (source != null && source.CompositionTarget != null)
                {
                    var transform = source.CompositionTarget.TransformFromDevice;
                    var logicalWorkingArea = transform.TransformBounds(new Rect(screen.WorkingArea.X, screen.WorkingArea.Y, screen.WorkingArea.Width, screen.WorkingArea.Height));
                    
                    this.Left = logicalWorkingArea.Left;
                    this.Top = logicalWorkingArea.Top;
                    this.Width = logicalWorkingArea.Width;
                    this.Height = logicalWorkingArea.Height;
                }
                else
                {
                    this.Left = screen.WorkingArea.Left;
                    this.Top = screen.WorkingArea.Top;
                    this.Width = screen.WorkingArea.Width;
                    this.Height = screen.WorkingArea.Height;
                }
                _isFitScreen = true;
            }
        }'''
content = re.sub(fitscreen_old, fitscreen_new, content)

# 2. Revert EnterFullscreen
enter_fullscreen_old = r'''        private void EnterFullscreen\(\)
        \{
            if \(_isFullscreen\) return;

            _isChangingFullscreen = true;
            _isFullscreen = true;

            _prevWindowState = WindowState;
            _prevWindowStyle = WindowStyle;
            _prevResizeMode  = ResizeMode;
            _prevTopmost     = Topmost;
            _backupBounds = new Rect\(this\.Left, this\.Top, this\.Width, this\.Height\);

            System\.Windows\.Shell\.WindowChrome\.SetWindowChrome\(this, null\);

            WindowStyle  = WindowStyle\.None;
            ResizeMode   = ResizeMode\.NoResize;
            
            WindowState  = WindowState\.Normal;
            Topmost      = true;

            var screen = System\.Windows\.Forms\.Screen\.FromHandle\(new System\.Windows\.Interop\.WindowInteropHelper\(this\)\.Handle\);
            this\.Left = screen\.WorkingArea\.Left;
            this\.Top = screen\.WorkingArea\.Top;
            this\.Width = screen\.WorkingArea\.Width;
            this\.Height = screen\.WorkingArea\.Height;

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
            _isChangingFullscreen = false;'''
content = re.sub(enter_fullscreen_old, enter_fullscreen_new, content)

# 3. Revert ExitFullscreen
exit_fullscreen_old = r'''        private void ExitFullscreen\(\)
        \{
            if \(!_isFullscreen\) return;

            _isChangingFullscreen = true;
            _isFullscreen = false;

            PopupFsExit\.IsOpen = false;

            this\.Left = _backupBounds\.Left;
            this\.Top = _backupBounds\.Top;
            this\.Width = _backupBounds\.Width;
            this\.Height = _backupBounds\.Height;

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

            WindowStyle  = _prevWindowStyle;
            ResizeMode   = _prevResizeMode;
            WindowState  = _prevWindowState;
            Topmost      = _prevTopmost;'''
content = re.sub(exit_fullscreen_old, exit_fullscreen_new, content)

with open('MainWindow.xaml.cs', 'w', encoding='utf-8') as f:
    f.write(content)

print("Done restoring EnterFullscreen and fixing FitScreen!")
