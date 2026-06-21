using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using Vortice.D3DCompiler;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace JonPlayer;

public class D3D11VideoRenderer : IDisposable
{
	public struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	private IntPtr _hwnd;

	private ID3D11Device? _d3d11Device;

	private ID3D11DeviceContext? _d3d11Context;

	private IDXGISwapChain? _swapChain;

	private ID3D11RenderTargetView? _renderTargetView;

	private ID3D11VertexShader? _vertexShader;

	private ID3D11PixelShader? _pixelShader;

	private ID3D11PixelShader? _enhancedPixelShader;

	private ID3D11PixelShader? _4kEnhancedPixelShader;

	private ID3D11PixelShader? _rgbPixelShader;

	private ID3D11PixelShader? _enhancedRgbPixelShader;

	private ID3D11PixelShader? _4kEnhancedRgbPixelShader;

	private ID3D11SamplerState? _samplerState;

	private ID3D11ShaderResourceView? _srvY;

	private ID3D11ShaderResourceView? _srvUV;

	private ID3D11Texture2D? _d3d11DecodeTexture;

	private ID3D11Texture2D? _bgraTexture;

	private ID3D11ShaderResourceView? _bgraSrv;

	private int _width;

	private int _height;

	private bool _isDisposed;

	private Thread? _renderThread;

	private int _presentedFramesThisSecond;

	private int _skippedFramesThisSecond;

	private DateTime _lastFpsUpdateTime = DateTime.UtcNow;

	private FFmpegMediaDecoder? _decoder;

	private readonly object _renderLock = new object();

	private bool _shadersInitialized;

	private bool _useEnhancedShader;

	public IntPtr D3D11DevicePtr => _d3d11Device?.NativePointer ?? IntPtr.Zero;

	public IntPtr D3D11ContextPtr => _d3d11Context?.NativePointer ?? IntPtr.Zero;

	public int Width => _width;

	public int Height => _height;

	public double PresentedFps { get; private set; }

	public double SkippedFramesPerSecond { get; private set; }

	public double VideoBrightness { get; set; } = 1.0;


	public double VideoContrast { get; set; } = 1.0;


	public double VideoSaturation { get; set; } = 1.0;


	public Stretch StretchMode { get; set; } = Stretch.Uniform;


	public D3D11VideoRenderer(IntPtr hwnd, FFmpegMediaDecoder decoder)
	{
		_hwnd = hwnd;
		_decoder = decoder;
		InitializeD3D();
	}

	public void EnableEnhancedShader(bool enable)
	{
		_useEnhancedShader = enable;
	}

	private void InitializeD3D()
	{
		try
		{
			IDXGIFactory1 iDXGIFactory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
			D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, (FeatureLevel[])null, out _d3d11Device, out _d3d11Context).CheckError();
			using (ID3D11Multithread iD3D11Multithread = _d3d11Context.QueryInterfaceOrNull<ID3D11Multithread>())
			{
				if (iD3D11Multithread != null)
				{
					iD3D11Multithread.SetMultithreadProtected(true);
				}
				else
				{
					using ID3D11Multithread iD3D11Multithread2 = _d3d11Device.QueryInterfaceOrNull<ID3D11Multithread>();
					if (iD3D11Multithread2 != null)
					{
						iD3D11Multithread2.SetMultithreadProtected(true);
					}
				}
			}
			SwapChainDescription swapChainDescription = default(SwapChainDescription);
			swapChainDescription.BufferCount = 2u;
			swapChainDescription.BufferDescription = new ModeDescription
			{
				Format = Format.B8G8R8A8_UNorm,
				Width = 1u,
				Height = 1u
			};
			swapChainDescription.Windowed = true;
			swapChainDescription.OutputWindow = _hwnd;
			swapChainDescription.SampleDescription = new SampleDescription(1u, 0u);
			swapChainDescription.SwapEffect = SwapEffect.Discard;
			swapChainDescription.BufferUsage = Usage.RenderTargetOutput;
			SwapChainDescription desc = swapChainDescription;
			_swapChain = iDXGIFactory.CreateSwapChain(_d3d11Device, desc);
			iDXGIFactory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);
			iDXGIFactory.Dispose();
			InitializeShaders();
			_renderThread = new Thread(RenderLoop)
			{
				IsBackground = true,
				Name = "D3D11RenderLoop"
			};
			_renderThread.Start();
		}
		catch (Exception)
		{
		}
	}

	private void InitializeShaders()
	{
		if (_shadersInitialized || _d3d11Device == null)
		{
			return;
		}
		try
		{
			string shaderSource = "\n                    Texture2D texY : register(t0);\n                    Texture2D texUV : register(t1);\n                    SamplerState samLinear : register(s0);\n                    struct PS_IN { float4 Pos : SV_POSITION; float2 Tex : TEXCOORD; };\n                    static const float3x3 YUVtoRGB = float3x3(1.16438, 0.00000, 1.79274, 1.16438, -0.21325, -0.53291, 1.16438, 2.11240, 0.00000);\n                    float4 PSMain(PS_IN input) : SV_Target {\n                        float y = texY.Sample(samLinear, input.Tex).r - 0.0625;\n                        float2 uv = texUV.Sample(samLinear, input.Tex).rg - 0.5;\n                        float3 rgb = mul(YUVtoRGB, float3(y, uv.x, uv.y));\n                        return float4(saturate(rgb), 1.0);\n                    }";
			string shaderSource2 = "\n                    Texture2D texY : register(t0);\n                    Texture2D texUV : register(t1);\n                    SamplerState samLinear : register(s0);\n                    struct PS_IN { float4 Pos : SV_POSITION; float2 Tex : TEXCOORD; };\n                    static const float3x3 YUVtoRGB = float3x3(1.16438, 0.00000, 1.79274, 1.16438, -0.21325, -0.53291, 1.16438, 2.11240, 0.00000);\n                    \n                    float3 GetRGB(float2 uvCoords) {\n                        float y = texY.Sample(samLinear, uvCoords).r - 0.0625;\n                        float2 uv = texUV.Sample(samLinear, uvCoords).rg - 0.5;\n                        return saturate(mul(YUVtoRGB, float3(y, uv.x, uv.y)));\n                    }\n\n                    float4 PSMain(PS_IN input) : SV_Target {\n                        uint w, h;\n                        texY.GetDimensions(w, h);\n                        float radiusX = max(1.0, w / 1920.0);\n                        float radiusY = max(1.0, h / 1080.0);\n                        float dx = radiusX / w;\n                        float dy = radiusY / h;\n                        \n                        float3 center = GetRGB(input.Tex);\n                        float3 left = GetRGB(input.Tex + float2(-dx, 0));\n                        float3 right = GetRGB(input.Tex + float2(dx, 0));\n                        float3 top = GetRGB(input.Tex + float2(0, -dy));\n                        float3 bottom = GetRGB(input.Tex + float2(0, dy));\n                        \n                        // Half the previous 4x strength\n                        float3 edge = center - (left + right + top + bottom) * 0.25;\n                        float3 sharpened = center + edge * 1.5;\n                        float luma = dot(sharpened, float3(0.299, 0.587, 0.114));\n                        float3 boosted = lerp(float3(luma, luma, luma), sharpened, 1.25);\n                        return float4(saturate(boosted), 1.0);\n                    }";
			string shaderSource3 = "\n                    Texture2D texY : register(t0);\n                    Texture2D texUV : register(t1);\n                    SamplerState samLinear : register(s0);\n                    struct PS_IN { float4 Pos : SV_POSITION; float2 Tex : TEXCOORD; };\n                    static const float3x3 YUVtoRGB = float3x3(1.16438, 0.00000, 1.79274, 1.16438, -0.21325, -0.53291, 1.16438, 2.11240, 0.00000);\n                    \n                    float3 GetRGB(float2 uvCoords) {\n                        float y = texY.Sample(samLinear, uvCoords).r - 0.0625;\n                        float2 uv = texUV.Sample(samLinear, uvCoords).rg - 0.5;\n                        return saturate(mul(YUVtoRGB, float3(y, uv.x, uv.y)));\n                    }\n\n                    float4 PSMain(PS_IN input) : SV_Target {\n                        uint w, h;\n                        texY.GetDimensions(w, h);\n                        \n                        float3 center = GetRGB(input.Tex);\n                        \n                        float dx = 1.0 / w;\n                        float dy = 1.0 / h;\n                        float3 left = GetRGB(input.Tex + float2(-dx, 0));\n                        float3 right = GetRGB(input.Tex + float2(dx, 0));\n                        float3 top = GetRGB(input.Tex + float2(0, -dy));\n                        float3 bottom = GetRGB(input.Tex + float2(0, dy));\n                        float3 edge = center - (left + right + top + bottom) * 0.25;\n                        \n                        // Sharpness +10\n                        float3 sharpened = center + edge * 2.0;\n                        \n                        // Brightness -2\n                        float3 color = sharpened - 0.02;\n                        \n                        // Contrast +8\n                        color = (color - 0.5) * 1.08 + 0.5;\n                        \n                        // Saturation +10\n                        float luma = dot(color, float3(0.299, 0.587, 0.114));\n                        color = lerp(float3(luma, luma, luma), color, 1.10);\n                        \n                        return float4(saturate(color), 1.0);\n                    }";
			string shaderSource4 = "\n                    Texture2D texRGB : register(t0);\n                    SamplerState samLinear : register(s0);\n                    struct PS_IN { float4 Pos : SV_POSITION; float2 Tex : TEXCOORD; };\n                    float4 PSMain(PS_IN input) : SV_Target {\n                        return texRGB.Sample(samLinear, input.Tex);\n                    }";
			string shaderSource5 = "\n                    Texture2D texRGB : register(t0);\n                    SamplerState samLinear : register(s0);\n                    struct PS_IN { float4 Pos : SV_POSITION; float2 Tex : TEXCOORD; };\n                    \n                    float3 GetRGB(float2 uvCoords) {\n                        return texRGB.Sample(samLinear, uvCoords).rgb;\n                    }\n\n                    float4 PSMain(PS_IN input) : SV_Target {\n                        uint w, h;\n                        texRGB.GetDimensions(w, h);\n                        float radiusX = max(1.0, w / 1920.0);\n                        float radiusY = max(1.0, h / 1080.0);\n                        float dx = radiusX / w;\n                        float dy = radiusY / h;\n                        \n                        float3 center = GetRGB(input.Tex);\n                        float3 left = GetRGB(input.Tex + float2(-dx, 0));\n                        float3 right = GetRGB(input.Tex + float2(dx, 0));\n                        float3 top = GetRGB(input.Tex + float2(0, -dy));\n                        float3 bottom = GetRGB(input.Tex + float2(0, dy));\n                        \n                        float3 edge = center - (left + right + top + bottom) * 0.25;\n                        float3 sharpened = center + edge * 1.5;\n                        float luma = dot(sharpened, float3(0.299, 0.587, 0.114));\n                        float3 boosted = lerp(float3(luma, luma, luma), sharpened, 1.25);\n                        return float4(saturate(boosted), 1.0);\n                    }";
			string shaderSource6 = "\n                    Texture2D texRGB : register(t0);\n                    SamplerState samLinear : register(s0);\n                    struct PS_IN { float4 Pos : SV_POSITION; float2 Tex : TEXCOORD; };\n                    \n                    float3 GetRGB(float2 uvCoords) {\n                        return texRGB.Sample(samLinear, uvCoords).rgb;\n                    }\n\n                    float4 PSMain(PS_IN input) : SV_Target {\n                        uint w, h;\n                        texRGB.GetDimensions(w, h);\n                        float3 center = GetRGB(input.Tex);\n                        \n                        float dx = 1.0 / w;\n                        float dy = 1.0 / h;\n                        float3 left = GetRGB(input.Tex + float2(-dx, 0));\n                        float3 right = GetRGB(input.Tex + float2(dx, 0));\n                        float3 top = GetRGB(input.Tex + float2(0, -dy));\n                        float3 bottom = GetRGB(input.Tex + float2(0, dy));\n                        float3 edge = center - (left + right + top + bottom) * 0.25;\n                        \n                        float3 sharpened = center + edge * 2.0;\n                        float3 color = sharpened - 0.02;\n                        color = (color - 0.5) * 1.08 + 0.5;\n                        float luma = dot(color, float3(0.299, 0.587, 0.114));\n                        color = lerp(float3(luma, luma, luma), color, 1.10);\n                        \n                        return float4(saturate(color), 1.0);\n                    }";
			Compiler.Compile("\n                    struct VS_OUT { float4 Pos : SV_POSITION; float2 Tex : TEXCOORD; };\n                    VS_OUT VSMain(uint id : SV_VertexID) {\n                        VS_OUT output;\n                        output.Tex = float2((id << 1) & 2, id & 2);\n                        output.Pos = float4(output.Tex * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);\n                        return output;\n                    }", "VSMain", "vsCode", "vs_4_0", out Blob blob, out Blob errorBlob);
			using (blob)
			{
				using (errorBlob)
				{
					_vertexShader = _d3d11Device.CreateVertexShader(blob.AsBytes());
				}
			}
			Compiler.Compile(shaderSource, "PSMain", "psCode", "ps_4_0", out Blob blob4, out Blob errorBlob2);
			using (blob4)
			{
				using (errorBlob2)
				{
					_pixelShader = _d3d11Device.CreatePixelShader(blob4.AsBytes());
				}
			}
			Compiler.Compile(shaderSource2, "PSMain", "psEnhancedCode", "ps_4_0", out Blob blob5, out Blob errorBlob3);
			using (blob5)
			{
				using (errorBlob3)
				{
					_enhancedPixelShader = _d3d11Device.CreatePixelShader(blob5.AsBytes());
				}
			}
			Compiler.Compile(shaderSource3, "PSMain", "ps4KEnhancedCode", "ps_4_0", out Blob blob6, out Blob errorBlob4);
			using (blob6)
			{
				using (errorBlob4)
				{
					_4kEnhancedPixelShader = _d3d11Device.CreatePixelShader(blob6.AsBytes());
				}
			}
			Compiler.Compile(shaderSource4, "PSMain", "psRgbCode", "ps_4_0", out Blob blob7, out Blob errorBlob5);
			using (blob7)
			{
				using (errorBlob5)
				{
					_rgbPixelShader = _d3d11Device.CreatePixelShader(blob7.AsBytes());
				}
			}
			Compiler.Compile(shaderSource5, "PSMain", "psEnhancedRgbCode", "ps_4_0", out Blob blob8, out Blob errorBlob6);
			using (blob8)
			{
				using (errorBlob6)
				{
					_enhancedRgbPixelShader = _d3d11Device.CreatePixelShader(blob8.AsBytes());
				}
			}
			Compiler.Compile(shaderSource6, "PSMain", "ps4KEnhancedRgbCode", "ps_4_0", out Blob blob9, out Blob errorBlob7);
			using (blob9)
			{
				using (errorBlob7)
				{
					_4kEnhancedRgbPixelShader = _d3d11Device.CreatePixelShader(blob9.AsBytes());
				}
			}
			SamplerDescription samplerDescription = default(SamplerDescription);
			samplerDescription.Filter = Filter.MinMagMipLinear;
			samplerDescription.AddressU = TextureAddressMode.Clamp;
			samplerDescription.AddressV = TextureAddressMode.Clamp;
			samplerDescription.AddressW = TextureAddressMode.Clamp;
			SamplerDescription samplerDesc = samplerDescription;
			_samplerState = _d3d11Device.CreateSamplerState(samplerDesc);
			_shadersInitialized = true;
		}
		catch (Exception value)
		{
			File.WriteAllText("shader_error.txt", $"Shader Init failed: {value}");
		}
	}

	[DllImport("user32.dll")]
	private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

	private void ResetSize()
	{
		if (_swapChain == null || _d3d11Device == null)
		{
			return;
		}
		GetClientRect(_hwnd, out var lpRect);
		int num = lpRect.Right - lpRect.Left;
		int num2 = lpRect.Bottom - lpRect.Top;
		if (num <= 0 || num2 <= 0 || (Width == num && Height == num2 && _renderTargetView != null))
		{
			return;
		}
		lock (_renderLock)
		{
			_renderTargetView?.Dispose();
			_renderTargetView = null;
			_swapChain.ResizeBuffers(2u, (uint)num, (uint)num2, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
			using (ID3D11Texture2D resource = _swapChain.GetBuffer<ID3D11Texture2D>(0u))
			{
				_renderTargetView = _d3d11Device.CreateRenderTargetView(resource);
			}
			_width = num;
			_height = num2;
		}
	}

	private void RenderLoop()
	{
		try
		{
			while (!_isDisposed)
			{
				if (_decoder == null || !_decoder.IsRunning || _decoder.IsPaused)
				{
					if (_decoder == null)
					{
						lock (_renderLock)
						{
							ResetSize();
							if (_renderTargetView != null && _d3d11Context != null)
							{
								try
								{
									_d3d11Context.ClearRenderTargetView(_renderTargetView, new Color4(0f, 0f, 0f));
									_swapChain?.Present(1u, PresentFlags.None);
								}
								catch (Exception)
								{
								}
							}
						}
					}
					Thread.Sleep(16);
					continue;
				}
				double masterClockPts = _decoder.GetMasterClockPts();
				FFmpegMediaDecoder.DecodedVideoFrame decodedVideoFrame = _decoder.PullVideoFrame(masterClockPts);
				if (decodedVideoFrame != null)
				{
					try
					{
						RenderFrameInternal(decodedVideoFrame);
						_swapChain?.Present(1u, PresentFlags.None);
						_presentedFramesThisSecond++;
					}
					catch (Exception)
					{
					}
					finally
					{
						decodedVideoFrame.Dispose();
					}
				}
				else
				{
					Thread.Sleep(1);
				}
				UpdatePresentationStats();
			}
		}
		catch (Exception)
		{
		}
	}

	private void UpdatePresentationStats()
	{
		DateTime utcNow = DateTime.UtcNow;
		if ((utcNow - _lastFpsUpdateTime).TotalMilliseconds >= 1000.0)
		{
			PresentedFps = _presentedFramesThisSecond;
			SkippedFramesPerSecond = _skippedFramesThisSecond;
			_presentedFramesThisSecond = 0;
			_skippedFramesThisSecond = 0;
			_lastFpsUpdateTime = utcNow;
		}
	}

	private void RenderFrameInternal(FFmpegMediaDecoder.DecodedVideoFrame frame)
	{
		lock (_renderLock)
		{
			ResetSize();
			if (!(_renderTargetView == null) && !(_d3d11Context == null) && !(_d3d11Device == null))
			{
				if (frame.IsD3D11 && frame.TexturePtr != IntPtr.Zero)
				{
					RenderHardwareTexture(frame.TexturePtr, frame.SliceIndexOrStride, frame.Width, frame.Height);
				}
				else if (frame.BgraBuffer != null && frame.BgraPointer != IntPtr.Zero)
				{
					RenderSoftwareBuffer(frame.BgraPointer, frame.SliceIndexOrStride, frame.Width, frame.Height);
				}
			}
		}
	}

	private unsafe void RenderSoftwareBuffer(IntPtr data, int stride, int width, int height)
	{
		if (_bgraTexture == null || _bgraTexture.Description.Width != width || _bgraTexture.Description.Height != height)
		{
			_bgraTexture?.Dispose();
			_bgraSrv?.Dispose();
			ID3D11Device? d3d11Device = _d3d11Device;
			Texture2DDescription description = new Texture2DDescription
			{
				Width = (uint)width,
				Height = (uint)height,
				MipLevels = 1u,
				ArraySize = 1u,
				Format = Format.B8G8R8A8_UNorm,
				Usage = ResourceUsage.Dynamic,
				BindFlags = BindFlags.ShaderResource,
				CPUAccessFlags = CpuAccessFlags.Write,
				SampleDescription = new SampleDescription(1u, 0u)
			};
			_bgraTexture = d3d11Device.CreateTexture2D(in description);
			_bgraSrv = _d3d11Device.CreateShaderResourceView(_bgraTexture);
		}
		MappedSubresource mappedSubresource = _d3d11Context.Map(_bgraTexture, 0u, MapMode.WriteDiscard);
		if (mappedSubresource.DataPointer != IntPtr.Zero)
		{
			for (int i = 0; i < height; i++)
			{
				Buffer.MemoryCopy((void*)IntPtr.Add(data, i * stride), (void*)IntPtr.Add(mappedSubresource.DataPointer, (int)(i * mappedSubresource.RowPitch)), (int)Math.Min(mappedSubresource.RowPitch, stride), (int)Math.Min(mappedSubresource.RowPitch, stride));
			}
			_d3d11Context.Unmap(_bgraTexture, 0u);
		}
		_d3d11Context.ClearRenderTargetView(_renderTargetView, new Color4(0f, 0f, 0f));
		float num = (float)width / (float)height;
		float num2 = (float)_width / (float)_height;
		int num3 = 0;
		int num4 = 0;
		int num5 = _width;
		int num6 = _height;
		if (StretchMode == Stretch.Uniform)
		{
			if (num2 > num)
			{
				num5 = (int)((float)_height * num);
				num3 = (_width - num5) / 2;
			}
			else
			{
				num6 = (int)((float)_width / num);
				num4 = (_height - num6) / 2;
			}
		}
		else if (StretchMode == Stretch.UniformToFill)
		{
			if (num2 > num)
			{
				num6 = (int)((float)_width / num);
				num4 = (_height - num6) / 2;
			}
			else
			{
				num5 = (int)((float)_height * num);
				num3 = (_width - num5) / 2;
			}
		}
		_d3d11Context.OMSetRenderTargets(_renderTargetView);
		_d3d11Context.RSSetViewport(new Viewport(num3, num4, num5, num6));
		_d3d11Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
		_d3d11Context.VSSetShader(_vertexShader);
		ID3D11PixelShader pixelShader = _rgbPixelShader;
		if (_useEnhancedShader)
		{
			pixelShader = ((width < 3800) ? ((_enhancedRgbPixelShader != null) ? _enhancedRgbPixelShader : _rgbPixelShader) : ((_4kEnhancedRgbPixelShader != null) ? _4kEnhancedRgbPixelShader : _enhancedRgbPixelShader));
		}
		_d3d11Context.PSSetShader(pixelShader);
		_d3d11Context.PSSetSampler(0u, _samplerState);
		_d3d11Context.PSSetShaderResource(0u, _bgraSrv);
		_d3d11Context.Draw(3u, 0u);
	}

	private void RenderHardwareTexture(IntPtr texturePtr, int sliceIndex, int trueWidth, int trueHeight)
	{
		try
		{
			Marshal.AddRef(texturePtr);
			using ID3D11Texture2D iD3D11Texture2D = new ID3D11Texture2D(texturePtr);
			Texture2DDescription description = iD3D11Texture2D.Description;
			if (_d3d11DecodeTexture == null || _d3d11DecodeTexture.Description.Width != trueWidth || _d3d11DecodeTexture.Description.Height != trueHeight)
			{
				_d3d11DecodeTexture?.Dispose();
				_srvY?.Dispose();
				_srvUV?.Dispose();
				ID3D11Device? d3d11Device = _d3d11Device;
				Texture2DDescription description2 = new Texture2DDescription
				{
					Width = (uint)trueWidth,
					Height = (uint)trueHeight,
					MipLevels = 1u,
					ArraySize = 1u,
					Format = description.Format,
					Usage = ResourceUsage.Default,
					BindFlags = BindFlags.ShaderResource,
					SampleDescription = new SampleDescription(1u, 0u)
				};
				_d3d11DecodeTexture = d3d11Device.CreateTexture2D(in description2);
				ShaderResourceViewDescription shaderResourceViewDescription = default(ShaderResourceViewDescription);
				shaderResourceViewDescription.Format = ((description.Format == Format.P010) ? Format.R16_UNorm : Format.R8_UNorm);
				shaderResourceViewDescription.ViewDimension = ShaderResourceViewDimension.Texture2D;
				shaderResourceViewDescription.Texture2D = new Texture2DShaderResourceView
				{
					MostDetailedMip = 0u,
					MipLevels = 1u
				};
				ShaderResourceViewDescription value = shaderResourceViewDescription;
				shaderResourceViewDescription = default(ShaderResourceViewDescription);
				shaderResourceViewDescription.Format = ((description.Format == Format.P010) ? Format.R16G16_UNorm : Format.R8G8_UNorm);
				shaderResourceViewDescription.ViewDimension = ShaderResourceViewDimension.Texture2D;
				shaderResourceViewDescription.Texture2D = new Texture2DShaderResourceView
				{
					MostDetailedMip = 0u,
					MipLevels = 1u
				};
				ShaderResourceViewDescription value2 = shaderResourceViewDescription;
				_srvY = _d3d11Device.CreateShaderResourceView(_d3d11DecodeTexture, value);
				_srvUV = _d3d11Device.CreateShaderResourceView(_d3d11DecodeTexture, value2);
			}
			Box box = default(Box);
			box.Left = 0;
			box.Top = 0;
			box.Front = 0;
			box.Right = trueWidth;
			box.Bottom = trueHeight;
			box.Back = 1;
			Box value3 = box;
			_d3d11Context.CopySubresourceRegion(_d3d11DecodeTexture, 0u, 0u, 0u, 0u, iD3D11Texture2D, (uint)sliceIndex, value3);
			_d3d11Context.ClearRenderTargetView(_renderTargetView, new Color4(0f, 0f, 0f));
			float num = (float)trueWidth / (float)trueHeight;
			float num2 = (float)_width / (float)_height;
			int num3 = 0;
			int num4 = 0;
			int num5 = _width;
			int num6 = _height;
			if (StretchMode == Stretch.Uniform)
			{
				if (num2 > num)
				{
					num5 = (int)((float)_height * num);
					num3 = (_width - num5) / 2;
				}
				else
				{
					num6 = (int)((float)_width / num);
					num4 = (_height - num6) / 2;
				}
			}
			else if (StretchMode == Stretch.UniformToFill)
			{
				if (num2 > num)
				{
					num6 = (int)((float)_width / num);
					num4 = (_height - num6) / 2;
				}
				else
				{
					num5 = (int)((float)_height * num);
					num3 = (_width - num5) / 2;
				}
			}
			_d3d11Context.OMSetRenderTargets(_renderTargetView);
			_d3d11Context.RSSetViewport(new Viewport(num3, num4, num5, num6));
			_d3d11Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
			_d3d11Context.VSSetShader(_vertexShader);
			ID3D11PixelShader pixelShader = _pixelShader;
			if (_useEnhancedShader)
			{
				pixelShader = ((trueWidth < 3800) ? ((_enhancedPixelShader != null) ? _enhancedPixelShader : _pixelShader) : ((_4kEnhancedPixelShader != null) ? _4kEnhancedPixelShader : _enhancedPixelShader));
			}
			_d3d11Context.PSSetShader(pixelShader);
			_d3d11Context.PSSetSampler(0u, _samplerState);
			_d3d11Context.PSSetShaderResources(0u, new ID3D11ShaderResourceView[2] { _srvY, _srvUV });
			_d3d11Context.Draw(3u, 0u);
		}
		catch (Exception)
		{
		}
	}

	private void CleanupResources()
	{
		lock (_renderLock)
		{
			_renderTargetView?.Dispose();
			_d3d11DecodeTexture?.Dispose();
			_bgraTexture?.Dispose();
			_srvY?.Dispose();
			_srvUV?.Dispose();
			_bgraSrv?.Dispose();
			_swapChain?.Dispose();
			_renderTargetView = null;
			_d3d11DecodeTexture = null;
			_bgraTexture = null;
			_srvY = null;
			_srvUV = null;
			_bgraSrv = null;
			_swapChain = null;
		}
	}

	public void Dispose()
	{
		if (_isDisposed)
		{
			return;
		}
		_isDisposed = true;
		_renderThread?.Join();
		CleanupResources();
		lock (_renderLock)
		{
			_vertexShader?.Dispose();
			_pixelShader?.Dispose();
			_enhancedPixelShader?.Dispose();
			_4kEnhancedPixelShader?.Dispose();
			_rgbPixelShader?.Dispose();
			_enhancedRgbPixelShader?.Dispose();
			_4kEnhancedRgbPixelShader?.Dispose();
			_samplerState?.Dispose();
			_d3d11Context?.Dispose();
			_d3d11Device?.Dispose();
		}
	}
}
