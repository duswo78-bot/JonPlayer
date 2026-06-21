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
                    _bassFilters[i]   = BiQuadFilter.LowShelf(sampleRate, 100, 0.7f, 8f);
                    _trebleFilters[i] = BiQuadFilter.HighShelf(sampleRate, 8000, 0.7f, 3f);
                }
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);

            if (!IsEnhancerEnabled || _source.WaveFormat.Channels != 2)
            {
                return samplesRead;
            }

            for (int i = 0; i < samplesRead; i += 2)
            {
                float left  = buffer[offset + i];
                float right = buffer[offset + i + 1];

                // [1단계] EQ : Low Shelf → High Shelf 순서로 직렬 적용
                left  = _trebleFilters[0].Transform(_bassFilters[0].Transform(left));
                right = _trebleFilters[1].Transform(_bassFilters[1].Transform(right));

                // [2단계] 스테레오 와이드닝 : Mid/Side 분리 후 Side 30% 증폭
                float mid  = (left + right) / 2.0f;
                float side = (left - right) / 2.0f;
                side *= 1.3f;
                left  = mid + side;
                right = mid - side;

                // [3단계] Soft Clipping : Tanh로 -1~+1 범위 내 자연스럽게 포화
                buffer[offset + i]     = (float)Math.Tanh(left);
                buffer[offset + i + 1] = (float)Math.Tanh(right);
            }

            return samplesRead;
        }
    }
}
