namespace JonPlayer;

public struct DecoderStats
{
	public string VideoInfo;
	public string AudioInfo;
	public double TargetFps;
	public double ActualFps;
	public double AvgDecodeTimeMs;
	public double VideoDecodeTimeMs;
	public double AudioDecodeTimeMs;
	public double MasterClock;
	public double AvDiffMs;
	public double SyncDelayMs;
	
	// render
	public double PresentedFps;
	public double DroppedFrames;
	public double QueuedFrames;
	public int LateFrames;

	// queue
	public int VideoQueueSize;
	public int AudioQueueSize;

	// hw
	public bool IsHwAccel;
	public bool IsRealHwAccel;
	public double SwsConvertTimeMs;
	public double GpuUploadTimeMs;

	// threads
	public int ThreadCount;

	// analyze
	public string Analyze()
	{
		return "";
	}

	public double VideoPts;
	public double VideoDecodePts;
	public double DecodeLeadMs;
	public double AudioPts;
	public int PacketQueueSize;
	public long Bitrate;

	// New tracking properties
	public double ReaderFps;
	public double VideoDecodeFps;
	public double AudioDecodeFps;
	public int VideoFrameQueueSize;
	public int AudioFrameQueueSize;
	public int VideoPacketQueueSize;
	public int AudioPacketQueueSize;
	public string DecoderMode;
	public string RendererMode;
	public int AudioUnderrunCount;
	public double SurfacePoolWaitTimeMs;
}
