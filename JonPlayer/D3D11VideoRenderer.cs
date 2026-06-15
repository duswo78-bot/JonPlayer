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
        private ID3D11RenderTargetView? _renderTargetView;
        private IDirect3DTexture9? _d3d9Texture;
        private IDirect3DSurface9? _d3d9Surface;
        private IntPtr _sharedHandle;
        private bool _isDisposed;
        private volatile bool _isDirty;
        private int _presentQueued;
        private readonly object _renderLock = new object();
        private int _sourceFramesThisSecond;
        private int _sourceFramesPerSecond;
        private DateTime _lastSourceFpsUpdateTime = DateTime.UtcNow;
        private int _presentedFramesThisSecond;
        private int _skippedFramesThisSecond;
        private DateTime _lastFpsUpdateTime = DateTime.UtcNow;

        public D3DImage D3DImage { get; } = new D3DImage();

        public int Width { get; private set; }
        public int Height { get; private set; }
        public double PresentedFps { get; private set; }
        public int RenderEventsPerSecond { get; private set; }
        public int SkippedFramesPerSecond { get; private set; }

        private ID3D11VertexShader? _vertexShader;
        private ID3D11PixelShader? _pixelShader;
        private ID3D11PixelShader? _pixelShaderArray;
        private ID3D11PixelShader? _enhancedPixelShader;
        private ID3D11SamplerState? _samplerState;
        private bool _shadersInitialized;
        private bool _useEnhancedShader;

        public void EnableEnhancedShader(bool enable)
        {
            _useEnhancedShader = enable;
        }

        private ID3D11Texture2D? _d3d11DecodeTexture;
        private ID3D11Texture2D? _d3d11OffscreenTexture;
        private ID3D11ShaderResourceView? _srvY;
        private ID3D11ShaderResourceView? _srvUV;

        public IntPtr D3D11DevicePtr => _d3d11Device?.NativePointer ?? IntPtr.Zero;
        public IntPtr D3D11ContextPtr => _d3d11Context?.NativePointer ?? IntPtr.Zero;

        public D3D11VideoRenderer()
        {
            InitializeD3D();
            if (System.Windows.Application.Current?.Dispatcher != null)
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    CompositionTarget.Rendering += OnRendering;
                    D3DImage.IsFrontBufferAvailableChanged += OnIsFrontBufferAvailableChanged;
                }));
            }
        }

        private void OnIsFrontBufferAvailableChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (D3DImage.IsFrontBufferAvailable && _d3d9Surface != null)
            {
                try
                {
                    D3DImage.Lock();
                    try { D3DImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _d3d9Surface.NativePointer); }
                    finally { D3DImage.Unlock(); }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"D3D11VideoRenderer Initial Render Error: {ex.Message}"); }
            }
        }
        private int _renderEvents;
        private DateTime _lastStat = DateTime.UtcNow;

        private void OnRendering(object? sender, EventArgs e)
        {
            _renderEvents++;

            if ((DateTime.UtcNow - _lastStat).TotalSeconds >= 1)
            {
                RenderEventsPerSecond = _renderEvents;
                _renderEvents = 0;
                _lastStat = DateTime.UtcNow;
            }

            PresentPendingFrame();
        }

        private void QueuePresent()
        {
            if (_isDisposed || D3DImage.Dispatcher.HasShutdownStarted) return;
            if (System.Threading.Interlocked.Exchange(ref _presentQueued, 1) == 1) return;

            D3DImage.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Render,
                new Action(PresentPendingFrame));
        }

        private void PresentPendingFrame()
        {
            System.Threading.Interlocked.Exchange(ref _presentQueued, 0);
            if (!_isDirty || !D3DImage.IsFrontBufferAvailable) return;

            lock (_renderLock)
            {
                if (!_isDirty || _isDisposed || !D3DImage.IsFrontBufferAvailable) return;

                try
                {
                    D3DImage.Lock();

                    try
                    {
                        D3DImage.AddDirtyRect(
                            new Int32Rect(0, 0, Width, Height));
                    }
                    finally
                    {
                        D3DImage.Unlock();
                    }

                    _isDirty = false;
                    _presentedFramesThisSecond++;
                    UpdatePresentationStats();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"D3D11VideoRenderer Present Error: {ex.Message}");
                }
            }
        }

        private void UpdateSourceFrameRate()
        {
            _sourceFramesThisSecond++;

            var now = DateTime.UtcNow;
            if ((now - _lastSourceFpsUpdateTime).TotalMilliseconds < 1000) return;

            _sourceFramesPerSecond = _sourceFramesThisSecond;
            _sourceFramesThisSecond = 0;
            _lastSourceFpsUpdateTime = now;
        }

        private bool ShouldQueueImmediatePresent()
        {
            return _sourceFramesPerSecond >= 50 || _sourceFramesThisSecond >= 45;
        }

        private void UpdatePresentationStats()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastFpsUpdateTime).TotalMilliseconds < 1000) return;

            PresentedFps = _presentedFramesThisSecond;
            SkippedFramesPerSecond = _skippedFramesThisSecond;
            _presentedFramesThisSecond = 0;
            _skippedFramesThisSecond = 0;
            _lastFpsUpdateTime = now;
        }

        private void InitializeD3D()
        {
            try
            {
                // 1. Create D3D11 Device
                D3D11.D3D11CreateDevice(
                    null,
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                    null!,
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

                // 3. Create Shared Texture (for WPF)
                // Defer to ResetSize

                InitializeShaders();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize D3D: {ex.Message}");
                _d3d9Device?.Dispose(); _d3d9Device = null;
                _d3d9Ex?.Dispose(); _d3d9Ex = null;
                _d3d11Context?.Dispose(); _d3d11Context = null;
                _d3d11Device?.Dispose(); _d3d11Device = null;
            }
        }

        private void InitializeShaders()
        {
            if (_shadersInitialized || _d3d11Device == null) return;
            try
            {
                string vsCode = @"
                    struct VS_OUT { float4 Pos : SV_POSITION; float2 Tex : TEXCOORD; };
                    VS_OUT VSMain(uint id : SV_VertexID) {
                        VS_OUT output;
                        output.Tex = float2((id << 1) & 2, id & 2);
                        output.Pos = float4(output.Tex * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
                        return output;
                    }";
                
                string psCode = @"
                    Texture2D texY : register(t0);
                    Texture2D texUV : register(t1);
                    SamplerState samLinear : register(s0);
                    struct PS_IN { float4 Pos : SV_POSITION; float2 Tex : TEXCOORD; };
                    static const float3x3 YUVtoRGB = float3x3(1.16438, 0.00000, 1.79274, 1.16438, -0.21325, -0.53291, 1.16438, 2.11240, 0.00000);
                    float4 PSMain(PS_IN input) : SV_Target {
                        float y = texY.Sample(samLinear, input.Tex).r - 0.0625;
                        float2 uv = texUV.Sample(samLinear, input.Tex).rg - 0.5;
                        float3 rgb = mul(YUVtoRGB, float3(y, uv.x, uv.y));
                        return float4(saturate(rgb), 1.0);
                    }";

                string psCodeArray = @"
                    Texture2DArray texY : register(t0);
                    Texture2DArray texUV : register(t1);
                    SamplerState samLinear : register(s0);
                    struct PS_IN { float4 Pos : SV_POSITION; float2 Tex : TEXCOORD; };
                    static const float3x3 YUVtoRGB = float3x3(1.16438, 0.00000, 1.79274, 1.16438, -0.21325, -0.53291, 1.16438, 2.11240, 0.00000);
                    float4 PSMain(PS_IN input) : SV_Target {
                        float y = texY.Sample(samLinear, float3(input.Tex, 0)).r - 0.0625;
                        float2 uv = texUV.Sample(samLinear, float3(input.Tex, 0)).rg - 0.5;
                        float3 rgb = mul(YUVtoRGB, float3(y, uv.x, uv.y));
                        return float4(saturate(rgb), 1.0);
                    }";

                string psEnhancedCode = @"
                    Texture2D texY : register(t0);
                    Texture2D texUV : register(t1);
                    SamplerState samLinear : register(s0);
                    struct PS_IN { float4 Pos : SV_POSITION; float2 Tex : TEXCOORD; };
                    static const float3x3 YUVtoRGB = float3x3(1.16438, 0.00000, 1.79274, 1.16438, -0.21325, -0.53291, 1.16438, 2.11240, 0.00000);
                    
                    float3 GetRGB(float2 uvCoords) {
                        float y = texY.Sample(samLinear, uvCoords).r - 0.0625;
                        float2 uv = texUV.Sample(samLinear, uvCoords).rg - 0.5;
                        return saturate(mul(YUVtoRGB, float3(y, uv.x, uv.y)));
                    }

                    float4 PSMain(PS_IN input) : SV_Target {
                        float3 center = GetRGB(input.Tex);
                        float dx = 1.0 / 1920.0;
                        float dy = 1.0 / 1080.0;
                        float3 left = GetRGB(input.Tex + float2(-dx, 0));
                        float3 right = GetRGB(input.Tex + float2(dx, 0));
                        float3 top = GetRGB(input.Tex + float2(0, -dy));
                        float3 bottom = GetRGB(input.Tex + float2(0, dy));
                        
                        float3 sharpened = center * 1.5 - (left + right + top + bottom) * 0.125;
                        float luma = dot(sharpened, float3(0.299, 0.587, 0.114));
                        float3 boosted = lerp(float3(luma, luma, luma), sharpened, 1.2);
                        return float4(saturate(boosted), 1.0);
                    }";

                Vortice.D3DCompiler.Compiler.Compile(vsCode, "VSMain", "vsCode", "vs_4_0", out Vortice.Direct3D.Blob vsBlob, out Vortice.Direct3D.Blob vsError);
                using (vsBlob) using (vsError)
                {
                    if (vsError != null) throw new Exception("VS Error: " + vsError.AsString());
                    if (vsBlob != null) _vertexShader = _d3d11Device.CreateVertexShader(vsBlob.AsBytes());
                }

                Vortice.D3DCompiler.Compiler.Compile(psCode, "PSMain", "psCode", "ps_4_0", out Vortice.Direct3D.Blob psBlob, out Vortice.Direct3D.Blob psError);
                using (psBlob) using (psError)
                {
                    if (psError != null) throw new Exception("PS Error: " + psError.AsString());
                    if (psBlob != null) _pixelShader = _d3d11Device.CreatePixelShader(psBlob.AsBytes());
                }

                Vortice.D3DCompiler.Compiler.Compile(psCodeArray, "PSMain", "psCodeArray", "ps_4_0", out Vortice.Direct3D.Blob psBlobArray, out Vortice.Direct3D.Blob psErrorArray);
                using (psBlobArray) using (psErrorArray)
                {
                    if (psErrorArray != null) throw new Exception("PSArray Error: " + psErrorArray.AsString());
                    if (psBlobArray != null) _pixelShaderArray = _d3d11Device.CreatePixelShader(psBlobArray.AsBytes());
                }

                Vortice.D3DCompiler.Compiler.Compile(psEnhancedCode, "PSMain", "psEnhancedCode", "ps_4_0", out Vortice.Direct3D.Blob psBlobEnhanced, out Vortice.Direct3D.Blob psErrorEnhanced);
                using (psBlobEnhanced) using (psErrorEnhanced)
                {
                    if (psErrorEnhanced != null) throw new Exception("PSEnhanced Error: " + psErrorEnhanced.AsString());
                    if (psBlobEnhanced != null) _enhancedPixelShader = _d3d11Device.CreatePixelShader(psBlobEnhanced.AsBytes());
                }

                var samplerDesc = new SamplerDescription
                {
                    Filter = Filter.MinMagMipLinear,
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Clamp
                };
                _samplerState = _d3d11Device.CreateSamplerState(samplerDesc);
                _shadersInitialized = true;
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText("shader_error.txt", $"Shader Init failed: {ex}");
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

                // Create offscreen texture for tear-free rendering
                textureDesc.MiscFlags = ResourceOptionFlags.None;
                _d3d11OffscreenTexture = _d3d11Device.CreateTexture2D(textureDesc);
                _renderTargetView = _d3d11Device.CreateRenderTargetView(_d3d11OffscreenTexture);

                // 4. Extract Shared Handle
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
                    try { D3DImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _d3d9Surface.NativePointer); }
                    finally { D3DImage.Unlock(); }
                }
                else
                {
                    D3DImage.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            D3DImage.Lock();
                            try { D3DImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _d3d9Surface.NativePointer); }
                            finally { D3DImage.Unlock(); }
                        }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"D3D11VideoRenderer Resize Async Error: {ex.Message}"); }
                    }));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to create shared texture: {ex.Message}");
                CleanupResources();
            }
        }

        public void RenderFrame(IntPtr data, int width, int height, int stride, bool isHardwareTexture)
        {
            if (data == IntPtr.Zero) return;
            
            lock (_renderLock)
            {
                if (_isDisposed || _d3d11Context == null || _renderTargetView == null) return;
                UpdateSourceFrameRate();

                try
                {
                    if (isHardwareTexture)
                    {
                        RenderHardwareTexture(data, stride, width, height);
                    }
                    else
                    {
                        if (_d3d11Texture == null || Width != width || Height != height)
                        {
                            ResetSize(width, height);
                        }
                        if (_d3d11Texture == null || _d3d11Context == null) return;
                        
                        _d3d11Context.UpdateSubresource(_d3d11Texture, 0, null, data, (uint)stride, 0);
                        _d3d11Context.Flush();
                    }
                    _isDirty = true;
                    if (ShouldQueueImmediatePresent()) QueuePresent();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to render frame: {ex.Message}");
                }
            }
        }

        private void RenderHardwareTexture(IntPtr texturePtr, int sliceIndex, int trueWidth, int trueHeight)
        {
            if (_d3d11Device == null || _d3d11Context == null) return;
            if (_vertexShader == null || _pixelShader == null || _samplerState == null) return;

            try
            {
                System.Runtime.InteropServices.Marshal.AddRef(texturePtr);
                using var hwTexture = new ID3D11Texture2D(texturePtr);
                var desc = hwTexture.Description;
                
                if (Width != trueWidth || Height != trueHeight || _d3d11DecodeTexture == null)
                {
                    ResetSize(trueWidth, trueHeight);
                    _d3d11DecodeTexture?.Dispose();
                    _srvY?.Dispose();
                    _srvUV?.Dispose();

                    _d3d11DecodeTexture = _d3d11Device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)trueWidth,
                        Height = (uint)trueHeight,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = desc.Format,
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ShaderResource,
                        SampleDescription = new SampleDescription(1, 0)
                    });

                    var srvDescY = new ShaderResourceViewDescription
                    {
                        Format = desc.Format == DXGIFormat.P010 ? DXGIFormat.R16_UNorm : DXGIFormat.R8_UNorm,
                        ViewDimension = ShaderResourceViewDimension.Texture2D,
                        Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 }
                    };
                    var srvDescUV = new ShaderResourceViewDescription
                    {
                        Format = desc.Format == DXGIFormat.P010 ? DXGIFormat.R16G16_UNorm : DXGIFormat.R8G8_UNorm,
                        ViewDimension = ShaderResourceViewDimension.Texture2D,
                        Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 }
                    };

                    _srvY = _d3d11Device.CreateShaderResourceView(_d3d11DecodeTexture, srvDescY);
                    _srvUV = _d3d11Device.CreateShaderResourceView(_d3d11DecodeTexture, srvDescUV);

                    if (_shadersInitialized && _d3d11Context != null)
                    {
                        _d3d11Context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
                        _d3d11Context.VSSetShader(_vertexShader);
                        _d3d11Context.PSSetSampler(0, _samplerState);
                    }
                }

                if (_d3d11Context == null || _d3d11DecodeTexture == null || _renderTargetView == null || _d3d11Texture == null || _d3d11OffscreenTexture == null || _srvY == null || _srvUV == null) return;

                var box = new Vortice.Mathematics.Box { Left = 0, Top = 0, Front = 0, Right = trueWidth, Bottom = trueHeight, Back = 1 };
                _d3d11Context.CopySubresourceRegion(_d3d11DecodeTexture, 0, 0, 0, 0, hwTexture, (uint)sliceIndex, box);

                _d3d11Context.OMSetRenderTargets(_renderTargetView);
                _d3d11Context.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, trueWidth, trueHeight));
                _d3d11Context.PSSetShader(_useEnhancedShader && _enhancedPixelShader != null ? _enhancedPixelShader : _pixelShader);
                _d3d11Context.PSSetShaderResources(0, new[] { _srvY, _srvUV });
                _d3d11Context.Draw(3, 0);

                // Atomic copy to the shared texture to prevent WPF tearing
                _d3d11Context.CopyResource(_d3d11Texture, _d3d11OffscreenTexture);
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText("render_error.txt", $"Zero-copy render failed: {ex}\n");
            }
        }

        private void CleanupResources()
        {
            IDirect3DSurface9? oldSurface;
            IDirect3DTexture9? oldTexture9;
            ID3D11Texture2D? oldTexture11;
            ID3D11Texture2D? oldDecodeTex;
            ID3D11Texture2D? oldOffscreenTex;
            ID3D11RenderTargetView? oldRt;
            ID3D11ShaderResourceView? oldSrvY;
            ID3D11ShaderResourceView? oldSrvUV;

            lock (_renderLock)
            {
                oldSurface = _d3d9Surface;
                oldTexture9 = _d3d9Texture;
                oldTexture11 = _d3d11Texture;
                oldDecodeTex = _d3d11DecodeTexture;
                oldOffscreenTex = _d3d11OffscreenTexture;
                oldRt = _renderTargetView;
                oldSrvY = _srvY;
                oldSrvUV = _srvUV;

                _d3d9Surface = null;
                _d3d9Texture = null;
                _d3d11Texture = null;
                _d3d11DecodeTexture = null;
                _d3d11OffscreenTexture = null;
                _renderTargetView = null;
                _srvY = null;
                _srvUV = null;
                _sharedHandle = IntPtr.Zero;
            }

            Action detachAndDispose = () =>
            {
                try
                {
                    D3DImage.Lock();
                    try { D3DImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero); }
                    finally { D3DImage.Unlock(); }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"D3D11VideoRenderer Detach Error: {ex.Message}"); }

                D3DImage.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
                {
                    oldRt?.Dispose();
                    oldSurface?.Dispose();
                    oldTexture9?.Dispose();
                    oldTexture11?.Dispose();
                    oldDecodeTex?.Dispose();
                    oldOffscreenTex?.Dispose();
                    oldSrvY?.Dispose();
                    oldSrvUV?.Dispose();
                }));
            };

            if (D3DImage.Dispatcher.CheckAccess())
                detachAndDispose();
            else
                D3DImage.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, detachAndDispose);
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (System.Windows.Application.Current?.Dispatcher != null)
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    CompositionTarget.Rendering -= OnRendering;
                    D3DImage.IsFrontBufferAvailableChanged -= OnIsFrontBufferAvailableChanged;
                }));
            }
            CleanupResources();
            // Capture device objects to dispose them AFTER the textures are disposed
            Vortice.Direct3D11.ID3D11VertexShader? oldVs;
            Vortice.Direct3D11.ID3D11PixelShader? oldPs;
            Vortice.Direct3D11.ID3D11PixelShader? oldPsArr;
            Vortice.Direct3D11.ID3D11PixelShader? oldEnhancedPs;
            Vortice.Direct3D11.ID3D11SamplerState? oldSampler;
            Vortice.Direct3D9.IDirect3DDevice9Ex? oldD3d9Device;
            Vortice.Direct3D9.IDirect3D9Ex? oldD3d9Ex;
            Vortice.Direct3D11.ID3D11DeviceContext? oldD3d11Context;
            Vortice.Direct3D11.ID3D11Device? oldD3d11Device;

            lock (_renderLock)
            {
                oldVs = _vertexShader; _vertexShader = null;
                oldPs = _pixelShader; _pixelShader = null;
                oldPsArr = _pixelShaderArray; _pixelShaderArray = null;
                oldEnhancedPs = _enhancedPixelShader; _enhancedPixelShader = null;
                oldSampler = _samplerState; _samplerState = null;
                oldD3d9Device = _d3d9Device; _d3d9Device = null;
                oldD3d9Ex = _d3d9Ex; _d3d9Ex = null;
                oldD3d11Context = _d3d11Context; _d3d11Context = null;
                oldD3d11Device = _d3d11Device; _d3d11Device = null;
            }

            D3DImage.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
            {
                oldVs?.Dispose();
                oldPs?.Dispose();
                oldPsArr?.Dispose();
                oldEnhancedPs?.Dispose();
                oldSampler?.Dispose();
                oldD3d9Device?.Dispose();
                oldD3d9Ex?.Dispose();
                oldD3d11Context?.Dispose();
                oldD3d11Device?.Dispose();
            }));
        }
    }
}
