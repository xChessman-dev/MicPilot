using System.Runtime.InteropServices;
using MicPilot.Core;

namespace MicPilot.App.Services;

public sealed class RnNoise : IFrameDenoiser
{
    private nint _state;
    private readonly float[] _input = new float[480], _output = new float[480];
    public static bool IsAvailable => File.Exists(Path.Combine(AppContext.BaseDirectory, "rnnoise.dll"));
    public RnNoise()
    {
        if (!IsAvailable) throw new InvalidOperationException("Не найден rnnoise.dll. Запустите tools/setup-noise.ps1 и пересоберите приложение.");
        if (!System.Runtime.Intrinsics.X86.Avx2.IsSupported) throw new InvalidOperationException("Эта сборка RNNoise требует процессор с AVX2.");
        if (rnnoise_get_frame_size() != 480) throw new InvalidOperationException("Несовместимый модуль RNNoise.");
        _state = rnnoise_create(0);
        if (_state == 0) throw new InvalidOperationException("Не удалось создать локальный шумоподавитель.");
    }
    public float Process(float[] input, float[] output)
    {
        ObjectDisposedException.ThrowIf(_state == 0, this);
        for (var i = 0; i < 480; i++) _input[i] = Math.Clamp(input[i], -1, 1) * 32768;
        var probability = rnnoise_process_frame(_state, _output, _input);
        for (var i = 0; i < 480; i++) output[i] = _output[i] / 32768;
        return probability;
    }
    public void Dispose() { if (_state != 0) { rnnoise_destroy(_state); _state = 0; } }
    [DllImport("rnnoise.dll", CallingConvention = CallingConvention.Cdecl)] private static extern nint rnnoise_create(nint model);
    [DllImport("rnnoise.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int rnnoise_get_frame_size();
    [DllImport("rnnoise.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void rnnoise_destroy(nint state);
    [DllImport("rnnoise.dll", CallingConvention = CallingConvention.Cdecl)] private static extern float rnnoise_process_frame(nint state, [Out] float[] output, float[] input);
}
