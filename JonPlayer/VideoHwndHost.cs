using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace JonPlayer;

public class VideoHwndHost : HwndHost
{
	public struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	public struct PAINTSTRUCT
	{
		public IntPtr hdc;

		public bool fErase;

		public RECT rcPaint;

		public bool fRestore;

		public bool fIncUpdate;

		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
		public byte[] rgbReserved;
	}

	private const int WS_CHILD = 1073741824;

	private const int WS_VISIBLE = 268435456;

	private const int WS_CLIPCHILDREN = 33554432;

	private const int WS_CLIPSIBLINGS = 67108864;

	private IntPtr _hwnd = IntPtr.Zero;

	private const int WM_ERASEBKGND = 20;

	private const int WM_PAINT = 15;

	private const int BLACK_BRUSH = 4;

	private int _lastClickTime;

	public IntPtr Hwnd => _hwnd;

	public new event EventHandler? MouseLeftButtonDown;

	public event EventHandler? MouseDoubleClick;

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	internal static extern IntPtr CreateWindowEx(int dwExStyle, string lpszClassName, string lpszWindowName, int style, int x, int y, int width, int height, IntPtr hwndParent, IntPtr hMenu, IntPtr hInst, IntPtr pvParam);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	internal static extern bool DestroyWindow(IntPtr hwnd);

	protected override HandleRef BuildWindowCore(HandleRef hwndParent)
	{
		_hwnd = CreateWindowEx(0, "Static", "", 1442840576, 0, 0, 0, 0, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
		return new HandleRef(this, _hwnd);
	}

	protected override void DestroyWindowCore(HandleRef hwnd)
	{
		DestroyWindow(hwnd.Handle);
	}

	[DllImport("user32.dll")]
	private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT lpPaint);

	[DllImport("user32.dll")]
	private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT lpPaint);

	[DllImport("user32.dll")]
	private static extern bool FillRect(IntPtr hDC, [In] ref RECT lprc, IntPtr hbr);

	[DllImport("gdi32.dll")]
	private static extern IntPtr GetStockObject(int fnObject);

	[DllImport("user32.dll")]
	private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

	[DllImport("user32.dll")]
	private static extern int GetDoubleClickTime();

	[DllImport("user32.dll")]
	private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

	public struct POINT
	{
		public int X;
		public int Y;
	}

	protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		switch (msg)
		{
		case 20:
			handled = true;
			return new IntPtr(1);
		case 15:
		{
			PAINTSTRUCT lpPaint;
			IntPtr hDC = BeginPaint(hwnd, out lpPaint);
			GetClientRect(hwnd, out var lpRect);
			FillRect(hDC, ref lpRect, GetStockObject(4));
			EndPaint(hwnd, ref lpPaint);
			handled = true;
			return IntPtr.Zero;
		}
		case 513:
		{
			int tickCount = Environment.TickCount;
			if (tickCount - _lastClickTime <= GetDoubleClickTime())
			{
				this.MouseDoubleClick?.Invoke(this, EventArgs.Empty);
				_lastClickTime = 0;
			}
			else
			{
				this.MouseLeftButtonDown?.Invoke(this, EventArgs.Empty);
				_lastClickTime = tickCount;
			}
			break;
		}
		}
		return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
	}
}
