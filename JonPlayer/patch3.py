import re

with open('MainWindow.xaml.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# 1. Update StatsTimer_Tick to include Audio Gain
stats_old = r'''            sb\.AppendLine\(\$"\{"Threads"\.PadRight\(12\)\}\{totalThreads\}"\);
            sb\.AppendLine\(\);
            
            sb\.AppendLine\("Session"\);'''
stats_new = r'''            sb.AppendLine($"{"Threads".PadRight(12)}{totalThreads}");
            if (_audioEnhancer != null && _audioEnhancer.BaselineVolumeMultiplier != 1.0f)
            {
                double gainDb = 20 * Math.Log10(_audioEnhancer.BaselineVolumeMultiplier);
                sb.AppendLine($"{"Audio Gain".PadRight(12)}{gainDb:+0.0;-0.0;0.0} dB");
            }
            sb.AppendLine();
            
            sb.AppendLine("Session");'''
content = re.sub(stats_old, stats_new, content)

# 2. Fix Cursor Hide for _overlayWindow
cursor_hide_old = r'''            _cursorHideTimer\.Tick \+= \(s, e\) =>
            \{
                if \(_isFullscreen\) this\.Cursor = System\.Windows\.Input\.Cursors\.None;
                _cursorHideTimer\.Stop\(\);
            \};'''
cursor_hide_new = r'''            _cursorHideTimer.Tick += (s, e) =>
            {
                if (_isFullscreen || this.WindowState == WindowState.Maximized)
                {
                    this.Cursor = System.Windows.Input.Cursors.None;
                    if (_overlayWindow != null) _overlayWindow.Cursor = System.Windows.Input.Cursors.None;
                }
                _cursorHideTimer.Stop();
            };'''
content = re.sub(cursor_hide_old, cursor_hide_new, content)

# 3. Fix MouseMove for _overlayWindow restoring cursor
mouse_move_old = r'''        private void Window_MouseMove\(object sender, System\.Windows\.Input\.MouseEventArgs e\)
        \{
            if \(this\.Cursor != System\.Windows\.Input\.Cursors\.Arrow\) this\.Cursor = System\.Windows\.Input\.Cursors\.Arrow;
            _cursorHideTimer\.Stop\(\);
            _cursorHideTimer\.Start\(\);
        \}'''
mouse_move_new = r'''        private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (this.Cursor != System.Windows.Input.Cursors.Arrow) this.Cursor = System.Windows.Input.Cursors.Arrow;
            if (_overlayWindow != null && _overlayWindow.Cursor != System.Windows.Input.Cursors.Arrow) _overlayWindow.Cursor = System.Windows.Input.Cursors.Arrow;
            _cursorHideTimer.Stop();
            _cursorHideTimer.Start();
        }'''
content = re.sub(mouse_move_old, mouse_move_new, content)

# 4. Hook MouseMove to _overlayWindow
setup_overlay_old = r'''            _overlayWindow\.LocationChanged \+= \(s, e\) => SyncMainWindowToOverlay\(\);
            _overlayWindow\.SizeChanged \+= \(s, e\) => SyncMainWindowToOverlay\(\);
            _overlayWindow\.StateChanged \+= \(s, e\) =>
            \{
                if \(this\.WindowState != _overlayWindow\.WindowState\)
                    this\.WindowState = _overlayWindow\.WindowState;
            \};'''
setup_overlay_new = r'''            _overlayWindow.LocationChanged += (s, e) => SyncMainWindowToOverlay();
            _overlayWindow.SizeChanged += (s, e) => SyncMainWindowToOverlay();
            _overlayWindow.StateChanged += (s, e) =>
            {
                if (this.WindowState != _overlayWindow.WindowState)
                    this.WindowState = _overlayWindow.WindowState;
            };
            _overlayWindow.MouseMove += Window_MouseMove;'''
content = re.sub(setup_overlay_old, setup_overlay_new, content)


with open('MainWindow.xaml.cs', 'w', encoding='utf-8') as f:
    f.write(content)

print("Done patching cursor hide and audio gain!")
