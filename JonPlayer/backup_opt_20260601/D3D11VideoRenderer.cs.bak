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
        private readonly object _renderLock = new object();

        public D3DImage D3DImage { get; } = new D3DImage();

        public int Width { get; private set; }
        public int Height { get; private set; }

        private ID3D11VertexShader? _vertexShader;
        private ID3D11PixelShader? _pixelShader;
        private ID3D11PixelShader? _pixelShaderArray;
        private ID3D11SamplerState? _samplerState;
        private bool _shadersInitialized;

        private ID3D11Texture2D? _d3d11DecodeTexture;
        private ID3D11Texture2D? _d3d11OffscreenTexture;

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
                    D3DImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _d3d9Surface.NativePointer);
                    D3DImage.Unlock();
                }
                catch { }
            }
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (_isDirty && D3DImage.IsFrontBufferAvailable)
            {
                _isDirty = false;
                try
                {
                    D3DImage.Lock();
                    D3DImage.AddDirtyRect(new Int32Rect(0, 0, Width, Height));
                    D3DImage.Unlock();
                }
                catch { }
            }
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

                Vortice.D3DCompiler.Compiler.Compile(vsCode, "VSMain", "vsCode", "vs_4_0", out Vortice.Direct3D.Blob vsBlob, out Vortice.Direct3D.Blob vsError);
                if (vsError != null) throw new Exception("VS Error: " + vsError.AsString());
                if (vsBlob != null) _vertexShader = _d3d11Device.CreateVertexShader(vsBlob.AsBytes());

                Vortice.D3DCompiler.Compiler.Compile(psCode, "PSMain", "psCode", "ps_4_0", out Vortice.Direct3D.Blob psBlob, out Vortice.Direct3D.Blob psError);
                if (psError != null) throw new Exception("PS Error: " + psError.AsString());
                if (psBlob != null) _pixelShader = _d3d11Device.CreatePixelShader(psBlob.AsBytes());

                Vortice.D3DCompiler.Compiler.Compile(psCodeArray, "PSMain", "psCodeArray", "ps_4_0", out Vortice.Direct3D.Blob psBlobArray, out Vortice.Direct3D.Blob psErrorArray);
                if (psErrorArray != null) throw new Exception("PSArray Error: " + psErrorArray.AsString());
                if (psBlobArray != null) _pixelShaderArray = _d3d11Device.CreatePixelShader(psBlobArray.AsBytes());

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
                CleanupResources();
            }
        }

        public void RenderFrame(IntPtr data, int width, int height, int stride, bool isHardwareTexture = false)
        {
            if (_isDisposed || data == IntPtr.Zero || width <= 0 || height <= 0 || _d3d11Device == null) return;

            lock (_renderLock)
            {
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
                    _d3d11DecodeTexture = _d3d11Device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)trueWidth,
                        Height = (uint)trueHeight,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = DXGIFormat.NV12,
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ShaderResource,
                        SampleDescription = new SampleDescription(1, 0)
                    });
                }

                if (_renderTargetView == null || _d3d11Texture == null || _d3d11OffscreenTexture == null) return;

                var box = new Vortice.Mathematics.Box { Left = 0, Top = 0, Front = 0, Right = trueWidth, Bottom = trueHeight, Back = 1 };
                _d3d11Context.CopySubresourceRegion(_d3d11DecodeTexture, 0, 0, 0, 0, hwTexture, (uint)sliceIndex, box);

                var srvDescY = new ShaderResourceViewDescription
                {
                    Format = DXGIFormat.R8_UNorm,
                    ViewDimension = ShaderResourceViewDimension.Texture2D,
                    Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 }
                };
                var srvDescUV = new ShaderResourceViewDescription
                {
                    Format = DXGIFormat.R8G8_UNorm,
                    ViewDimension = ShaderResourceViewDimension.Texture2D,
                    Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 }
                };

                using var srvY = _d3d11Device.CreateShaderResourceView(_d3d11DecodeTexture, srvDescY);
                using var srvUV = _d3d11Device.CreateShaderResourceView(_d3d11DecodeTexture, srvDescUV);

                _d3d11Context.OMSetRenderTargets(_renderTargetView);
                _d3d11Context.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, trueWidth, trueHeight));
                _d3d11Context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
                _d3d11Context.VSSetShader(_vertexShader);
                _d3d11Context.PSSetShader(_pixelShader);
                _d3d11Context.PSSetSampler(0, _samplerState);
                _d3d11Context.PSSetShaderResources(0, new[] { srvY, srvUV });
                _d3d11Context.Draw(3, 0);

                // Atomic copy to the shared texture to prevent WPF tearing
                _d3d11Context.CopyResource(_d3d11Texture, _d3d11OffscreenTexture);
                _d3d11Context.Flush();
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText("render_error.txt", $"Zero-copy render failed: {ex}\n");
            }
        }

        private void CleanupResources()
        {
            var oldSurface = _d3d9Surface;
            var oldTexture9 = _d3d9Texture;
            var oldTexture11 = _d3d11Texture;
            var oldDecodeTex = _d3d11DecodeTexture;
            var oldOffscreenTex = _d3d11OffscreenTexture;
            var oldRt = _renderTargetView;

            _d3d9Surface = null;
            _d3d9Texture = null;
            _d3d11Texture = null;
            _d3d11DecodeTexture = null;
            _d3d11OffscreenTexture = null;
            _renderTargetView = null;
            _sharedHandle = IntPtr.Zero;

            Action detachAndDispose = () =>
            {
                try
                {
                    D3DImage.Lock();
                    D3DImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
                    D3DImage.Unlock();
                }
                catch { }

                D3DImage.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
                {
                    oldRt?.Dispose();
                    oldSurface?.Dispose();
                    oldTexture9?.Dispose();
                    oldTexture11?.Dispose();
                    oldDecodeTex?.Dispose();
                    oldOffscreenTex?.Dispose();
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
            _d3d9Device?.Dispose(); _d3d9Device = null;
            _d3d9Ex?.Dispose(); _d3d9Ex = null;
            _d3d11Context?.Dispose(); _d3d11Context = null;
            _d3d11Device?.Dispose(); _d3d11Device = null;
        }
    }
}
