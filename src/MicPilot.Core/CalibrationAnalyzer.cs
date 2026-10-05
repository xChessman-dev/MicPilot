namespace MicPilot.Core;

public sealed record CalibrationResult(MicSettings Settings, double DurationSeconds, float NoiseFloorDb,
    float SpeechRmsDb, float PeakDb, float ClippedPercent, bool HasNoiseSample, string[] Findings);

/// <summary>Conservative acoustic measurements, not voice cloning or an invented quality score.</summary>
public static class CalibrationAnalyzer
{
    public static CalibrationResult Analyze(float[] speech, float[]? room = null, float[]? keyboard = null, CancellationToken cancellationToken = default)
    {
        if (speech.Length < 48000 * 8 || speech.Length > 48000 * 120) throw new ArgumentException("Нужно от 8 до 120 секунд речи.");
        if (speech.Any(sample => !float.IsFinite(sample))) throw new ArgumentException("Аудио содержит некорректные отсчёты.");
        var levels = FrameLevels(speech); var peak = speech.Max(Math.Abs);
        if (peak < .002f) throw new ArgumentException("Речь слишком тихая или файл содержит тишину. Проверьте микрофон.");
        var clipped = speech.Count(s => Math.Abs(s) >= .999f) * 100f / speech.Length;
        var explicitNoise = room is { Length: >= 48000 };
        var noiseLevels = explicitNoise ? FrameLevels(room!) : levels;
        var noiseDb = Percentile(noiseLevels, explicitNoise ? .5f : .1f);
        var loud = levels.Where(db => db > Math.Max(noiseDb + 10, -55)).ToArray();
        var missingPauses = !explicitNoise && loud.Length < 80;
        if (missingPauses) loud = levels.Where(db => db > -55).ToArray();
        if (loud.Length < 80) throw new ArgumentException("Не удалось отделить речь от фона. Нужна запись обычной речи и отдельный образец тишины.");
        var speechDb = Percentile(loud, .5f);
        float lowEnergy = 0, highEnergy = 0, totalEnergy = 0;
        var low = new Biquad(); low.SetLowPass(180); var high = new Biquad(); high.SetHighPass(4500);
        var popFrames = 0;
        for (var frame = 0; frame < speech.Length / 480; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float lowFrame = 0, fullFrame = 0;
            for (var i = 0; i < 480; i++)
            {
                var s = speech[frame * 480 + i]; var l = low.Process(s); var h = high.Process(s);
                lowFrame += l * l; fullFrame += s * s; highEnergy += h * h;
            }
            lowEnergy += lowFrame; totalEnergy += fullFrame;
            if (fullFrame > .8f && lowFrame / Math.Max(fullFrame, 1e-9f) > .72f) popFrames++;
        }
        var notes = new List<string>();
        if (missingPauses) notes.Add("В файле нет надёжных пауз: тихие участки нельзя считать фоном комнаты. Уровень и динамика оценены, а для подавления фона предложены осторожные базовые настройки. Запишите комнату отдельно.");
        else if (!explicitNoise) notes.Add("Фон оценён по тихим участкам файла. Отдельная запись комнаты даст более надёжный порог.");
        if (clipped > .05f) notes.Add($"Перегруз входа: {clipped:F2}% отсчётов. Уменьшите усиление в Maono; цифровой фильтр не восстанавливает потерянный звук.");
        if (AudioMath.Db(peak) > -3) notes.Add("Громким фразам не хватает запаса. Проверьте усиление микрофона и расстояние до него.");
        if (speechDb < -30) notes.Add("Речь тихая. Сначала проверьте положение микрофона; усиление программы ограничено, чтобы не поднять шум.");
        if (popFrames > 3) notes.Add("Есть сильные низкочастотные фрагменты: возможны удары «П/Б», близкое положение или гул. Включена более сильная защита; проверьте поп-фильтр.");
        if (!missingPauses && noiseDb > -45) notes.Add("Заметный фон: увеличено шумоподавление. Сравните согласные и окончания слов до и после.");
        if (keyboard is { Length: >= 48000 }) notes.Add("Учтён отдельный образец клавиатуры. Нельзя гарантировать удаление кликов одновременно с речью.");
        var keyboardDb = keyboard is { Length: >= 48000 } ? Percentile(FrameLevels(keyboard), .85f) : noiseDb;
        var strength = missingPauses ? .6f : Math.Clamp(.55f + (noiseDb + 60) / 70 + Math.Max(0, keyboardDb - noiseDb - 8) / 100, .45f, .9f);
        var lowFraction = lowEnergy / Math.Max(totalEnergy, 1e-9f); var highFraction = highEnergy / Math.Max(totalEnergy, 1e-9f);
        var settings = new MicSettings
        {
            InputGainDb = Math.Clamp(-24 - speechDb, -6, 6), NoiseStrength = strength,
            HighPassHz = popFrames > 3 || lowFraction > .65f ? 100 : 75, PopStrength = popFrames > 3 ? .8f : .4f,
            GateThresholdDb = missingPauses ? -55 : Math.Clamp(Math.Min(noiseDb + 7, speechDb - 14), -65, -32),
            GateFloorDb = explicitNoise ? -18 : -10, MudDb = lowFraction > .65f ? -2 : -.5f,
            PresenceDb = .5f, AirDb = 0, DeEssStrength = highFraction > .18f ? .45f : .2f,
            CompressorThresholdDb = -18, CompressorRatio = 2.5f, OutputGainDb = 2,
        }.Sanitize();
        notes.Add("Эквалайзер настроен бережно: спектр зависит от фразы, комнаты и микрофона, а предпочтения тембра — от слушателя.");
        notes.Add("Шумоподавление Maono/Discord/OBS нужно проверить отдельно, чтобы не накладывать несколько агрессивных обработок.");
        return new(settings, speech.Length / 48000d, noiseDb, speechDb, AudioMath.Db(peak), clipped, explicitNoise, notes.ToArray());
    }
    private static float[] FrameLevels(float[] samples)
    {
        var frames = new float[samples.Length / 480];
        for (var f = 0; f < frames.Length; f++)
        {
            double energy = 0; for (var i = 0; i < 480; i++) { var v = samples[f * 480 + i]; if (!float.IsFinite(v)) throw new ArgumentException("Некорректное аудио."); energy += v * v; }
            frames[f] = AudioMath.Db((float)Math.Sqrt(energy / 480));
        }
        return frames;
    }
    private static float Percentile(float[] values, float p) { var sorted = (float[])values.Clone(); Array.Sort(sorted); return sorted[(int)Math.Clamp((sorted.Length - 1) * p, 0, sorted.Length - 1)]; }
}
