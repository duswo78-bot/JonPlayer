using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Vortice.Direct3D;
using Vortice.Direct3D9;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

using D3D9Format = Vortice.Direct3D9.Format;
using DXGIFormat = Vortice.DXGI.Format;
using D3D9Usage = Vortice.Direct3D9.Usage;
using D3D9SwapEffect = Vortice.Direct3D9.SwapEffect;
using D3D9PresentParameters = Vortice.Direct3D9.PresentParameters;

namespace JonPlayer
{
    public class D3D11VideoRenderer : IDisposable
    {
        private ID3D11Device? _d3d11Device;
        private ID3D11DeviceContext? _d3d11Context;
        private IDirect3D9Ex? _d3d9Ex;
        private IDirect3DDevice9Ex? _d3d9Device;

        private ID3D11Texture2D? _d3d11Texture;
        private IDirect3DTexture9? _d3d9Texture;
        private IDirect3DSurface9? _d3d9Surface;
        private IntPtr _sharedHandle;

        public D3DImage D3DImage { get; } = new D3DImage();

        public int Width { get; private set; }
        public int Height { get; private set; }

        public D3D11VideoRenderer()
        {
            InitializeD3D();
        }

        private void InitializeD3D()
        {
            try
            {
                // 1. Create D3D11 Device
                D3D11.D3D11CreateDevice(
                    null,
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    (FeatureLevel[]?)null,
                    out _d3d11Device,
                    out _d3d11Context
                ).CheckError();

                // 2. Create D3D9Ex Context
                D3D9.Direct3DCreate9Ex(out _d3d9Ex).CheckError();

                var presentParams = new D3D9PresentParameters
                {
                    Windowed = true,
                    SwapEffect = D3D9SwapEffect.Discard,
                    BackBufferFormat = D3D9Format.A8R8G8B8,
                    BackBufferWidth = 1,
                    BackBufferHeight = 1,
                    DeviceWindowHandle = IntPtr.Zero
                };

                _d3d9Device = _d3d9Ex.CreateDeviceEx(
                    0,
                    DeviceType.Hardware,
                    IntPtr.Zero,
                    CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
                    presentParams
                );
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize Direct3D: {ex.Message}");
            }
        }

        public void ResetSize(int width, int height)
        {
            if (Width == width && Height == height && _d3d11Texture != null)
                return;

            CleanupResources();

            Width = width;
            Height = height;

            if (width <= 0 || height <= 0 || _d3d11Device == null || _d3d9Device == null) return;

            try
            {
                // 3. Create Shared D3D11 Texture
                var textureDesc = new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = DXGIFormat.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.Shared
                };

                _d3d11Texture = _d3d11Device.CreateTexture2D(textureDesc);

                // 4. Get Shared Handle
                using (var dxgiResource = _d3d11Texture.QueryInterface<IDXGIResource>())
                {
                    _sharedHandle = dxgiResource.SharedHandle;
                }

                // 5. Open in D3D9Ex
                IntPtr tempSharedHandle = _sharedHandle;
                _d3d9Texture = _d3d9Device.CreateTexture(
                    (uint)width,
                    (uint)height,
                    1,
                    D3D9Usage.RenderTarget,
                    D3D9Format.A8R8G8B8,
                    Pool.Default,
                    ref tempSharedHandle
                );

                _d3d9Surface = _d3d9Texture.GetSurfaceLevel(0);

                // Bind to D3DImage (BeginInvoke to prevent deadlock with Stop)
                if (D3DImage.Dispatcher.CheckAccess())
                {
                    D3DImage.Lock();
                    D3DImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _d3d9Surface.NativePointer);
                    D3DImage.Unlock();
                }
                else
                {
                    D3DImage.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            D3DImage.Lock();
                            D3DImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _d3d9Surface.NativePointer);
                            D3DImage.Unlock();
                        }
                        catch { }
                    }));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to create shared texture: {ex.Message}");
            }
        }

        public void RenderFrame(IntPtr bgraData, int stride)
        {
            if (_d3d11Texture == null || _d3d11Context == null) return;

            try
            {
                // 6. Update Subresource on D3D11 (BGRA raw data copy)
                // Use the non-generic overload: (resource, subresource, box?, srcData, rowPitch, depthPitch)
                _d3d11Context.UpdateSubresource(_d3d11Texture, 0, null, bgraData, (uint)stride, 0);
                _d3d11Context.Flush();

                // 7. Update WPF D3DImage (BeginInvoke to avoid deadlock with Stop/Join)
                int w = Width, h = Height;
                D3DImage.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (D3DImage.IsFrontBufferAvailable)
                        {
                            D3DImage.Lock();
                            D3DImage.AddDirtyRect(new Int32Rect(0, 0, w, h));
                            D3DImage.Unlock();
                        }
                    }
                    catch { /* D3DImage may be disposed during shutdown */ }
                }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to render frame: {ex.Message}");
            }
        }

        private void CleanupResources()
        {
            if (D3DImage.Dispatcher.CheckAccess())
            {
                D3DImage.Lock();
                D3DImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
                D3DImage.Unlock();
            }
            else
            {
                D3DImage.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        D3DImage.Lock();
                        D3DImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
                        D3DImage.Unlock();
                    }
                    catch { }
                }));
            }

            _d3d9Surface?.Dispose(); _d3d9Surface = null;
            _d3d9Texture?.Dispose(); _d3d9Texture = null;
            _d3d11Texture?.Dispose(); _d3d11Texture = null;
            _sharedHandle = IntPtr.Zero;
        }

        public void Dispose()
        {
            CleanupResources();
            _d3d9Device?.Dispose(); _d3d9Device = null;
            _d3d9Ex?.Dispose(); _d3d9Ex = null;
            _d3d11Context?.Dispose(); _d3d11Context = null;
            _d3d11Device?.Dispose(); _d3d11Device = null;
        }
    }
}
