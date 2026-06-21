namespace JonPlayer;

public struct DecoderStats
{
	public string VideoInfo;

	public string AudioInfo;

	public double TargetFps;

	public double ActualFps;

	public double AvgDecodeTimeMs;

	public double SyncDelayMs;

	public double VideoPts;

	public double AudioPts;

	public int LateFrames;

	public int ThreadCount;

	public int DroppedFrames;

	public int PacketQueueSize;

	public int AudioQueueSize;

	public bool IsHwAccel;

	public long Bitrate;
}
