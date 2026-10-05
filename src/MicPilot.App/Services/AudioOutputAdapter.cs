using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicPilot.App.Services;

/// <summary>Use the exact WASAPI device format, including multichannel USB headsets.</summary>
public static class AudioOutputAdapter
{
    public static IWaveProvider ForDevice(ISampleProvider mono, WaveFormat deviceFormat)
    {
        if (mono.WaveFormat.Channels != 1) throw new ArgumentException("Mono source required.");
        ISampleProvider route = mono;
        if (deviceFormat.Channels == 2) route = new MonoToStereoSampleProvider(route);
        else if (deviceFormat.Channels is > 2 and <= 8) route = new FrontChannels(route, deviceFormat.Channels);
        else if (deviceFormat.Channels != 1) throw new InvalidOperationException("Поддерживается выход от 1 до 8 каналов.");
        if (route.WaveFormat.SampleRate != deviceFormat.SampleRate) route = new WdlResamplingSampleProvider(route, deviceFormat.SampleRate);
        return new NativeOutput(route, deviceFormat);
    }

    private sealed class FrontChannels(ISampleProvider source, int channels) : ISampleProvider
    {
        private float[] _mono = new float[4096];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, channels);
        public int Read(Span<float> buffer)
        {
            var frames = buffer.Length / channels;
            if (_mono.Length < frames) _mono = new float[frames];
            var read = source.Read(_mono.AsSpan(0, frames));
            buffer[..(read * channels)].Clear();
            for (var frame = 0; frame < read; frame++)
            {
                // Windows interleaved front-left/front-right first; no voice in surround or LFE.
                buffer[frame * channels] = _mono[frame]; buffer[frame * channels + 1] = _mono[frame];
            }
            return read * channels;
        }
    }

    private sealed class NativeOutput : IWaveProvider
    {
        private readonly ISampleProvider _source;
        private readonly WaveFormat _standard;
        private float[] _samples = new float[4096];
        public WaveFormat WaveFormat { get; }
        public NativeOutput(ISampleProvider source, WaveFormat format)
        {
            _source = source; WaveFormat = format; _standard = format.AsStandardWaveFormat();
            if (!(_standard.Encoding == WaveFormatEncoding.IeeeFloat && _standard.BitsPerSample == 32) &&
                !(_standard.Encoding == WaveFormatEncoding.Pcm && _standard.BitsPerSample is 16 or 24 or 32))
                throw new InvalidOperationException("Формат аудиовыхода не поддерживается.");
        }
        public int Read(Span<byte> buffer)
        {
            var samples = buffer.Length / WaveFormat.BlockAlign * WaveFormat.Channels;
            if (_samples.Length < samples) _samples = new float[samples];
            var read = _source.Read(_samples.AsSpan(0, samples));
            var bytesPerSample = _standard.BitsPerSample / 8;
            if (_standard.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                _samples.AsSpan(0, read).CopyTo(MemoryMarshal.Cast<byte, float>(buffer[..(read * 4)])); return read * 4;
            }
            for (var i = 0; i < read; i++)
            {
                var value = float.IsFinite(_samples[i]) ? Math.Clamp(_samples[i], -.999f, .999f) : 0;
                var destination = buffer.Slice(i * bytesPerSample, bytesPerSample);
                if (bytesPerSample == 2) BinaryPrimitives.WriteInt16LittleEndian(destination, (short)(value * 32767));
                else if (bytesPerSample == 3)
                {
                    var integer = (int)(value * 8388607);
                    destination[0] = (byte)integer; destination[1] = (byte)(integer >> 8); destination[2] = (byte)(integer >> 16);
                }
                else BinaryPrimitives.WriteInt32LittleEndian(destination, (int)(value * 2147483647d));
            }
            return read * bytesPerSample;
        }
    }
}
