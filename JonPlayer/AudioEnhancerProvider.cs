using System;
using NAudio.Wave;
using NAudio.Dsp;

namespace JonPlayer
{
    public class AudioEnhancerProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly BiQuadFilter[] _bassFilters;    // Low Shelf  : 저음 펀치감
        private readonly BiQuadFilter[] _trebleFilters;  // High Shelf : 고음 선명도

        public bool IsEnhancerEnabled { get; set; } = true;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public AudioEnhancerProvider(ISampleProvider source)
        {
            _source = source;
            int sampleRate = _source.WaveFormat.SampleRate;

            if (_source.WaveFormat.Channels == 2)
            {
                _bassFilters   = new BiQuadFilter[2];
                _trebleFilters = new BiQuadFilter[2];

                for (int i = 0; i < 2; i++)
                {
                    _bassFilters[i]   = BiQuadFilter.LowShelf(sampleRate, 100, 0.7f, 5.0f);  // +5dB 저음 부스트
                    _trebleFilters[i] = BiQuadFilter.HighShelf(sampleRate, 8000, 0.7f, 2f);
                }
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);

            if (!IsEnhancerEnabled || _source.WaveFormat.Channels != 2)
                return samplesRead;

            for (int i = 0; i < samplesRead; i += 2)
            {
                float left  = buffer[offset + i];
                float right = buffer[offset + i + 1];

                // 입력 보호 (NaN/Inf 방지)
                if (float.IsNaN(left) || float.IsInfinity(left)) left = 0;
                if (float.IsNaN(right) || float.IsInfinity(right)) right = 0;

                // 과도한 값은 clamp (0으로 세팅하면 클릭 발생)
                left = Math.Max(-8.0f, Math.Min(8.0f, left));
                right = Math.Max(-8.0f, Math.Min(8.0f, right));

                // EQ
                left  = _trebleFilters[0].Transform(_bassFilters[0].Transform(left));
                right = _trebleFilters[1].Transform(_bassFilters[1].Transform(right));

                // ✅ 4. Stereo widening (약하게)
                float mid  = (left + right) / 2.0f;
                float side = (left - right) / 2.0f;
                side *= 1.1f;

                left  = mid + side;
                right = mid - side;

                // 5. 돌발 노이즈(Glitch) 억제 (너무 aggressive하지 않게)
                float energyTotal = Math.Abs(left) + Math.Abs(right);
                if (energyTotal > 8.0f)
                {
                    float atten = 8.0f / energyTotal;
                    left *= atten;
                    right *= atten;
                }

                // ✅ 6. 안전 Limiter
                buffer[offset + i]     = SoftLimiter(left);
                buffer[offset + i + 1] = SoftLimiter(right);
            }

            return samplesRead;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static float SoftLimiter(float x)
        {
            // 개선된 소프트 리미터: |x|<=1 구간은 기존 cubic, 그 이상은 부드럽게 접근
            const float hard = 1.0f;
            const float maxOut = 0.98f;

            if (x <= hard && x >= -hard)
            {
                return 1.5f * x - 0.5f * x * x * x;
            }

            // |x| > 1 영역: tanh로 소프트하게 포화
            float sign = x < 0 ? -1f : 1f;
            float excess = Math.Abs(x) - hard;
            float soft = hard + (float)Math.Tanh(excess * 2.5f) * (maxOut - hard);
            return sign * soft;
        }
    }
}
