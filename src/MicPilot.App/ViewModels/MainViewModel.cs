using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using MicPilot.App.Services;
using MicPilot.Core;
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.Wave.SampleProviders;

namespace MicPilot.App.ViewModels;

public sealed class RelayCommand(Action action, Func<bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => action();
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}

public sealed class SettingParameter : INotifyPropertyChanged
{
    private float _value;
    private readonly Action<float> _change;
    public string Key { get; }
    public string Name { get; }
    public string Hint { get; }
    public float Min { get; }
    public float Max { get; }
    public float Step { get; }
    public string Unit { get; }
    public float Value { get => _value; set { var v = Math.Clamp(value, Min, Max); if (Math.Abs(v - _value) < .0001) return; _value = v; PropertyChanged?.Invoke(this, new(nameof(Value))); PropertyChanged?.Invoke(this, new(nameof(DisplayValue))); _change(v); } }
    public string DisplayValue => Unit == "%" ? $"{Value * 100:0}%" : $"{Value:0.#} {Unit}";
    public SettingParameter(string key, string name, string hint, float min, float max, float step, string unit, float value, Action<float> change)
    { Key = key; Name = name; Hint = hint; Min = min; Max = max; Step = step; Unit = unit; _value = value; _change = change; }
    public void Refresh(float value) { _value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); PropertyChanged?.Invoke(this, new(nameof(DisplayValue))); }
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed record ParameterGroup(string Name, string Description, SettingParameter[] Parameters);

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AudioEngine _engine = new();
    private readonly ProfileStore _store;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly bool _diagnostic;
    private AudioDevice? _input, _output;
    private MicProfile? _profile;
    private MicSettings _settings;
    private string _profileName;
    private bool _running, _busy, _bypass, _mute;
    private int _pageIndex, _ticks;
    private string _notice = "", _calibrationStatus = "Запись ещё не загружена.", _analysisText = "Здесь появятся измерения и объяснение предложенных настроек.";
    private float[]? _speech, _room, _keyboard;
    private CalibrationResult? _suggestion;
    private CancellationTokenSource? _operation;
    private WasapiPlayer? _previewPlayer;
    private TimeSpan _lastCpu; private long _lastTick = Stopwatch.GetTimestamp();
    private float _inputDb = -120, _outputDb = -120; private double _cpu, _ram; private bool _cpuMeasured;
    private string _dsp = "—", _latency = "—", _diagnostics = "Нет активного аудиопотока", _reduction = "—";
    private double _progress;
    public ObservableCollection<AudioDevice> Inputs { get; } = [];
    public ObservableCollection<AudioDevice> Outputs { get; } = [];
    public ObservableCollection<MicProfile> Profiles { get; } = [];
    public ParameterGroup[] Groups { get; }
    public event Action<float, float>? LevelsChanged;
    public event Action<double>? CpuChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel(bool diagnostic = false)
    {
        _diagnostic = diagnostic;
        _process.Refresh(); _ram = _process.WorkingSet64 / 1048576d;
        _lastCpu = _process.TotalProcessorTime; _lastTick = Stopwatch.GetTimestamp();
        _store = new ProfileStore(Path.Combine(AppContext.BaseDirectory, "data"));
        var state = diagnostic ? new AppState(null, null, "Естественный", new(), []) : _store.Load();
        _settings = state.Settings; _profileName = state.ProfileName;
        foreach (var p in MicProfile.BuiltIns.Concat(state.Profiles)) Profiles.Add(p);
        _profile = Profiles.FirstOrDefault(p => p.Name == _profileName) ?? Profiles[0];
        Groups = BuildGroups();
        RefreshDevices(state.InputId, state.OutputId); _notice = _store.Warning;
        _engine.Apply(_settings);
        _engine.Faulted += (_, text) => System.Windows.Application.Current.Dispatcher.BeginInvoke(() => { _engine.Stop(); IsRunning = false; Notice = text; });
        StartCommand = new(async () => await StartStop(), () => !Busy && (IsRunning || SelectedInput is not null && SelectedOutput is not null && RnNoise.IsAvailable));
        RefreshCommand = new(() => RefreshDevices(SelectedInput?.Id, SelectedOutput?.Id), () => !IsRunning && !Busy);
        LoadCommand = new(async () => await LoadSpeech(), () => !Busy && !IsRunning);
        RoomCommand = new(async () => await Record("room", 7), CanRecord);
        KeyboardCommand = new(async () => await Record("keyboard", 8), CanRecord);
        SpeechCommand = new(async () => await Record("speech", 30), CanRecord);
        AnalyzeCommand = new(async () => await Analyze(), () => !Busy && _speech is { Length: >= 384000 });
        ApplyCommand = new(ApplySuggestion, () => !Busy && _suggestion is not null);
        CancelCommand = new(() => _operation?.Cancel(), () => Busy);
        DryCommand = new(async () => await Preview(false), CanPreview);
        WetCommand = new(async () => await Preview(true), CanPreview);
        StopPreviewCommand = new(StopPreview, () => _previewPlayer is not null);
        SaveCommand = new(SaveProfile, () => !Busy);
        ResetCommand = new(() => { if (SelectedProfile is not null) { _profileName = SelectedProfile.Name; UpdateSettings(SelectedProfile.Settings); Notice = "Параметры выбранного профиля восстановлены."; } }, () => !Busy);
        ImportCommand = new(ImportProfile, () => !Busy);
        ExportCommand = new(ExportProfile, () => !Busy);
        ExportAudioCommand = new(async () => await ExportAudio(), () => !Busy && _speech is not null);
        _timer.Tick += (_, _) => Tick(); _timer.Start();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveState(); };
        if (!RnNoise.IsAvailable) Notice = "Шумоподавитель не собран: нужен rnnoise.dll рядом с приложением. Остальные настройки доступны, запуск аудио пока заблокирован.";
    }
    public AudioDevice? SelectedInput
    {
        get => _input;
        set
        {
            if (_input?.Id != value?.Id && (_speech is not null || _room is not null || _keyboard is not null))
            {
                StopPreview(); _speech = _room = _keyboard = null; _suggestion = null;
                CalibrationStatus = "Микрофон изменён. Запишите или загрузите новые образцы для этого устройства.";
                AnalysisText = "Образцы предыдущего микрофона удалены из памяти. Исходные файлы не изменены.";
                Changed(nameof(RoomStatus)); Changed(nameof(KeyboardStatus)); CommandManager.InvalidateRequerySuggested();
            }
            _input = value; Changed(); ScheduleSave();
        }
    }
    public AudioDevice? SelectedOutput { get => _output; set { _output = value; Changed(); ScheduleSave(); } }
    public MicProfile? SelectedProfile { get => _profile; set { if (value is null || value == _profile) return; _profile = value; Changed(); _profileName = value.Name; UpdateSettings(value.Settings); } }
    public string ProfileName { get => _profileName; set { _profileName = value; Changed(); ScheduleSave(); } }
    public int PageIndex { get => _pageIndex; set { _pageIndex = value; Changed(); } }
    public bool IsRunning { get => _running; private set { _running = value; Changed(); Changed(nameof(StatusTitle)); Changed(nameof(StatusDetail)); Changed(nameof(StartLabel)); Changed(nameof(CanChangeDevices)); Changed(nameof(CanRecordNow)); CommandManager.InvalidateRequerySuggested(); } }
    public bool Busy { get => _busy; private set { _busy = value; Changed(); Changed(nameof(CanChangeDevices)); Changed(nameof(CanRecordNow)); CommandManager.InvalidateRequerySuggested(); } }
    public bool CanChangeDevices => !Busy && !IsRunning;
    public bool CanRecordNow => CanRecord();
    public bool Bypass { get => _bypass; set { _bypass = value; _engine.Bypass(value); Changed(); Changed(nameof(StatusDetail)); } }
    public bool Muted { get => _mute; set { _mute = value; _engine.Mute(value); Changed(); Changed(nameof(StatusTitle)); Changed(nameof(StatusDetail)); } }
    public bool NoiseEnabled { get => _settings.NoiseEnabled; set => UpdateSettings(_settings with { NoiseEnabled = value }); }
    public bool GateEnabled { get => _settings.GateEnabled; set => UpdateSettings(_settings with { GateEnabled = value }); }
    public bool CompressorEnabled { get => _settings.CompressorEnabled; set => UpdateSettings(_settings with { CompressorEnabled = value }); }
    public string StatusTitle => IsRunning ? Muted ? "Микрофон заглушён" : "Голос проходит через MicPilot" : "Микрофон готов к настройке";
    public string StatusDetail => IsRunning ? Bypass ? "Фильтры обойдены · защитный лимитер остаётся активен" : "Локальная обработка · звук направляется в CABLE Input" : "Выберите устройства и включите обработку, когда будете готовы.";
    public string StartLabel => IsRunning ? "Остановить" : "Включить микрофон";
    public string Notice { get => _notice; set { _notice = value; Changed(); } }
    public string CalibrationStatus { get => _calibrationStatus; set { _calibrationStatus = value; Changed(); } }
    public string AnalysisText { get => _analysisText; set { _analysisText = value; Changed(); } }
    public string RoomStatus => _room is null ? "Нет образца комнаты" : $"Комната: {_room.Length / 48000d:0.#} с";
    public string KeyboardStatus => _keyboard is null ? "Нет образца клавиатуры" : $"Клавиатура: {_keyboard.Length / 48000d:0.#} с";
    public double Progress { get => _progress; set { _progress = value; Changed(); } }
    public string InputLevel => IsRunning ? $"{Math.Max(-80, _inputDb):0.#} dBFS" : "— dBFS";
    public string OutputLevel => IsRunning ? $"{Math.Max(-80, _outputDb):0.#} dBFS" : "— dBFS";
    public string CpuText => _cpuMeasured ? $"{_cpu:0.0}% CPU" : "— CPU";
    public string RamText => $"{_ram:0} МБ RAM";
    public string DspText => _dsp;
    public string LatencyText => _latency;
    public string DiagnosticsText => _diagnostics;
    public string ReductionText => _reduction;
    public string EngineText => RnNoise.IsAvailable ? "RNNoise · CPU · 48 кГц" : "RNNoise не установлен";
    public RelayCommand StartCommand { get; } public RelayCommand RefreshCommand { get; } public RelayCommand LoadCommand { get; }
    public RelayCommand RoomCommand { get; } public RelayCommand KeyboardCommand { get; } public RelayCommand SpeechCommand { get; }
    public RelayCommand AnalyzeCommand { get; } public RelayCommand ApplyCommand { get; } public RelayCommand CancelCommand { get; }
    public RelayCommand DryCommand { get; } public RelayCommand WetCommand { get; } public RelayCommand StopPreviewCommand { get; }
    public RelayCommand SaveCommand { get; } public RelayCommand ResetCommand { get; } public RelayCommand ImportCommand { get; } public RelayCommand ExportCommand { get; } public RelayCommand ExportAudioCommand { get; }
    public MicSettings Settings => _settings;

    private ParameterGroup[] BuildGroups()
    {
        SettingParameter P(string key, string name, string hint, float min, float max, float step, string unit)
        {
            var property = typeof(MicSettings).GetProperty(key)!;
            return new(key, name, hint, min, max, step, unit, (float)property.GetValue(_settings)!, value =>
            { var clone = _settings with { }; property.SetValue(clone, value); _profileName = "Своя настройка"; UpdateSettings(clone); });
        }
        return
        [
            new("Очистка и вход", "Подавление помех, гул и воздушные удары. Начните с умеренных значений.",
            [ P(nameof(MicSettings.InputGainDb), "Усиление входа", "Цифровой уровень, не аппаратное усиление Maono", -18, 18, .5f, "dB"),
              P(nameof(MicSettings.NoiseStrength), "Шумоподавление", "Больше — тише фон, но выше риск изменения согласных", 0, 1, .05f, "%"),
              P(nameof(MicSettings.HighPassHz), "Срез низких частот", "Гул и вибрации; слишком высокий срез истончает голос", 20, 180, 5, "Гц"),
              P(nameof(MicSettings.PopStrength), "Защита от «П/Б»", "Динамическое ослабление сильных низкочастотных ударов", 0, 1, .05f, "%") ]),
            new("Тишина в паузах", "Мягкое приглушение вместо резкого отрезания слов.",
            [ P(nameof(MicSettings.GateThresholdDb), "Порог экспандера", "Ниже — больше тихой речи проходит", -70, -20, 1, "dBFS"),
              P(nameof(MicSettings.GateFloorDb), "Приглушение фона", "Глубина ослабления в паузах", -40, 0, 1, "dB"),
              P(nameof(MicSettings.GateReleaseMs), "Плавное закрытие", "Больше — мягче окончания слов", 60, 500, 10, "мс") ]),
            new("Тембр и разборчивость", "Четыре широкие полосы EQ и обработка резких свистящих.",
            [ P(nameof(MicSettings.BassDb), "Тело · 130 Гц", "Низ и ощущение полноты", -8, 8, .5f, "dB"),
              P(nameof(MicSettings.MudDb), "Гулкость · 350 Гц", "Отрицательные значения уменьшают мутность", -8, 6, .5f, "dB"),
              P(nameof(MicSettings.PresenceDb), "Ясность · 2,8 кГц", "Разборчивость; избыток делает голос резким", -6, 6, .5f, "dB"),
              P(nameof(MicSettings.AirDb), "Воздух · 8,5 кГц", "Верхние частоты и детали", -6, 6, .5f, "dB"),
              P(nameof(MicSettings.DeEssStrength), "Смягчение «С/Ш»", "Сила высокочастотной динамической обработки", 0, 1, .05f, "%"),
              P(nameof(MicSettings.DeEssThresholdDb), "Порог де-эссера", "Ниже — чаще срабатывает", -45, -10, 1, "dBFS") ]),
            new("Динамика и выход", "Выравнивание громкости и защита от перегруза на выходе.",
            [ P(nameof(MicSettings.CompressorThresholdDb), "Порог компрессора", "Уровень, выше которого речь сжимается", -36, -6, 1, "dBFS"),
              P(nameof(MicSettings.CompressorRatio), "Степень сжатия", "1 — без сжатия; начните с 2–3", 1, 6, .1f, ":1"),
              P(nameof(MicSettings.CompressorAttackMs), "Атака", "Скорость реакции на громкую речь", 1, 40, 1, "мс"),
              P(nameof(MicSettings.CompressorReleaseMs), "Восстановление", "Плавность возврата громкости", 50, 500, 10, "мс"),
              P(nameof(MicSettings.OutputGainDb), "Громкость выхода", "После компрессора, перед защитным лимитером", -18, 12, .5f, "dB"),
              P(nameof(MicSettings.LimiterCeilingDb), "Потолок лимитера", "Максимальный уровень выхода", -6, -.3f, .1f, "dBFS") ]),
        ];
    }
    private void UpdateSettings(MicSettings settings)
    {
        _settings = settings.Sanitize(); _engine.Apply(_settings);
        foreach (var parameter in Groups.SelectMany(g => g.Parameters)) parameter.Refresh((float)typeof(MicSettings).GetProperty(parameter.Key)!.GetValue(_settings)!);
        Changed(nameof(NoiseEnabled)); Changed(nameof(GateEnabled)); Changed(nameof(CompressorEnabled)); Changed(nameof(ProfileName)); ScheduleSave();
    }
    private void RefreshDevices(string? inputId, string? outputId)
    {
        try
        {
            Inputs.Clear(); Outputs.Clear(); foreach (var d in AudioEngine.Inputs()) Inputs.Add(d); foreach (var d in AudioEngine.Outputs()) Outputs.Add(d);
            SelectedInput = Inputs.FirstOrDefault(d => d.Id == inputId) ?? Inputs.FirstOrDefault(d => d.Name.Contains("PD200X", StringComparison.OrdinalIgnoreCase)) ?? Inputs.FirstOrDefault(d => !d.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase));
            SelectedOutput = Outputs.FirstOrDefault(d => d.Id == outputId) ?? Outputs.FirstOrDefault();
            if (Outputs.Count == 0) Notice = "Не найден CABLE Input. Нужен установленный VB-CABLE; вывод в колонки автоматически не включается.";
            CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception e) { Notice = "Не удалось получить аудиоустройства: " + e.Message; }
    }
    private async Task StartStop()
    {
        if (IsRunning) { _engine.Stop(); IsRunning = false; LevelsChanged?.Invoke(-120, -120); return; }
        Busy = true; Notice = ""; StopPreview();
        try { await _engine.StartAsync(SelectedInput!.Id, SelectedOutput!.Id); IsRunning = true; }
        catch (Exception e) { Notice = e.Message; }
        finally { Busy = false; }
    }
    private bool CanRecord() => !Busy && !IsRunning && SelectedInput is not null;
    private bool CanPreview() => !Busy && !IsRunning && _speech is not null && RnNoise.IsAvailable;
    private async Task Record(string kind, int seconds)
    {
        Busy = true; StopPreview(); _operation = new(); Progress = 0;
        CalibrationStatus = kind switch { "room" => "7 секунд: не говорите, оставьте обычный фон комнаты.", "keyboard" => "8 секунд: печатайте и кликайте, не говорите.", _ => "30 секунд: обычная и тихая речь, «П/Б/С/Ш», затем несколько громких фраз." };
        try
        {
            var capture = await AudioFiles.Capture(SelectedInput!.Id, seconds, new Progress<double>(v => Progress = v), _operation.Token);
            if (kind == "room") _room = capture; else if (kind == "keyboard") _keyboard = capture; else _speech = capture;
            _suggestion = null; CalibrationStatus = $"Готово: записано {capture.Length / 48000d:0.#} с. Аудио хранится в памяти, пока приложение открыто.";
            Changed(nameof(RoomStatus)); Changed(nameof(KeyboardStatus));
        }
        catch (OperationCanceledException) { CalibrationStatus = "Запись отменена. Предыдущие образцы сохранены."; }
        catch (Exception e) { CalibrationStatus = "Ошибка записи: " + e.Message; }
        finally { Busy = false; _operation.Dispose(); _operation = null; }
    }
    private async Task LoadSpeech()
    {
        var dialog = new OpenFileDialog { Filter = "Аудио|*.wav;*.mp3;*.aiff;*.wma;*.m4a|Все файлы|*.*", Title = "Чистая запись своего голоса — 8–120 секунд" };
        if (dialog.ShowDialog() != true) return;
        Busy = true; _operation = new(); StopPreview();
        try { var samples = await Task.Run(() => AudioFiles.ReadMono(dialog.FileName, 120, _operation.Token)); if (samples.Length < 384000) throw new InvalidDataException("Нужно не менее 8 секунд речи."); _speech = samples; _suggestion = null; CalibrationStatus = $"{Path.GetFileName(dialog.FileName)} · {samples.Length / 48000d:0.#} с. Исходный файл не изменён."; }
        catch (OperationCanceledException) { CalibrationStatus = "Загрузка отменена."; }
        catch (Exception e) { CalibrationStatus = e.Message; }
        finally { Busy = false; _operation.Dispose(); _operation = null; }
    }
    private async Task Analyze()
    {
        Busy = true; _operation = new(); AnalysisText = "Анализируем уровень, фон, спектр и динамику…";
        try
        {
            var result = await Task.Run(() => CalibrationAnalyzer.Analyze(_speech!, _room, _keyboard, _operation.Token)); _suggestion = result;
            var noiseLabel = result.HasNoiseSample ? "фон комнаты" : "тихие участки файла";
            AnalysisText = $"Речь: {result.SpeechRmsDb:0.#} dBFS · {noiseLabel}: {result.NoiseFloorDb:0.#} dBFS · пик: {result.PeakDb:0.#} dBFS\nПерегруз: {result.ClippedPercent:0.##}% отсчётов\n\n" + string.Join("\n\n", result.Findings) + $"\n\nПредложение: шумоподавление {result.Settings.NoiseStrength:P0}, срез {result.Settings.HighPassHz:0} Гц, порог пауз {result.Settings.GateThresholdDb:0} dBFS. Настройки ещё не применены.";
        }
        catch (OperationCanceledException) { AnalysisText = "Анализ отменён."; }
        catch (Exception e) { AnalysisText = e.Message; }
        finally { Busy = false; _operation.Dispose(); _operation = null; }
    }
    private void ApplySuggestion()
    {
        if (_suggestion is null) return; _profileName = "Моя калибровка"; UpdateSettings(_suggestion.Settings); Notice = "Предложенные настройки применены. Сравните звук до/после, затем сохраните профиль.";
    }
    private async Task Preview(bool wet)
    {
        Busy = true; _operation = new(); StopPreview();
        try
        {
            var settings = _settings; var pair = await Task.Run(() => { var dry = (float[])_speech!.Clone(); var processed = AudioFiles.Process(dry, settings, _operation.Token); AudioFiles.MatchLoudness(dry, processed); return (dry, processed); });
            using var enumerator = new MMDeviceEnumerator();
            using var device = FindPreviewDevice(enumerator);
            _previewPlayer = new WasapiPlayerBuilder().WithDevice(device).WithSharedMode().WithEventSync().Build();
            ISampleProvider route = new ArrayProvider(wet ? pair.processed : pair.dry);
            _previewPlayer.Init(AudioOutputAdapter.ForDevice(route, _previewPlayer.DeviceMixFormat)); _previewPlayer.Play();
            CalibrationStatus = wet ? "Слушаете обработанную запись. Громкость уравнена с оригиналом для сравнения." : "Слушаете оригинал. Громкость уравнена с обработанной записью для сравнения.";
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { CalibrationStatus = "Ошибка прослушивания: " + e.Message; StopPreview(); }
        finally { Busy = false; _operation.Dispose(); _operation = null; }
    }
    private void StopPreview() { if (_previewPlayer is null) return; try { _previewPlayer.Stop(); } catch { } _previewPlayer.Dispose(); _previewPlayer = null; CommandManager.InvalidateRequerySuggested(); }
    private static MMDevice FindPreviewDevice(MMDeviceEnumerator enumerator)
    {
        var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        if (!defaultDevice.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase)) return defaultDevice;
        defaultDevice.Dispose();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        { if (!device.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase)) return device; device.Dispose(); }
        throw new InvalidOperationException("Не найдены наушники/динамики для прослушивания. Выводить тест в CABLE небезопасно.");
    }
    private async Task ExportAudio()
    {
        var dialog = new SaveFileDialog { Filter = "WAV|*.wav", FileName = "MicPilot-processed.wav", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), Title = "Сохранить обработанную запись" };
        if (dialog.ShowDialog() != true) return;
        Busy = true; _operation = new();
        try { var settings = _settings; await Task.Run(() => AudioFiles.Save(dialog.FileName, AudioFiles.Process(_speech!, settings, _operation.Token))); CalibrationStatus = "Обработанная запись сохранена: " + dialog.FileName; }
        catch (Exception e) { CalibrationStatus = e is OperationCanceledException ? "Экспорт отменён." : e.Message; }
        finally { Busy = false; _operation.Dispose(); _operation = null; }
    }
    private void SaveProfile()
    {
        var name = ProfileName.Trim(); if (name.Length is < 1 or > 64) { Notice = "Название профиля: от 1 до 64 символов."; return; }
        if (MicProfile.BuiltIns.Any(p => p.Name == name)) { Notice = "Чтобы сохранить изменения, задайте своё название — встроенный профиль останется нетронутым."; return; }
        var previous = Profiles.FirstOrDefault(p => p.Name == name); if (previous is not null) Profiles.Remove(previous);
        var profile = new MicProfile(name, "Собственный профиль микрофона", _settings); Profiles.Add(profile); _profile = profile; _profileName = name; Changed(nameof(SelectedProfile)); Changed(nameof(ProfileName)); if (SaveState(true)) Notice = "Профиль сохранён локально.";
    }
    private void ImportProfile()
    {
        var dialog = new OpenFileDialog { Filter = "Профиль MicPilot|*.json" }; if (dialog.ShowDialog() != true) return;
        try
        {
            var p = ProfileStore.Import(dialog.FileName); var baseName = p.Name; var number = 1;
            while (Profiles.Any(x => x.Name == p.Name))
            { var suffix = $" (импорт {number++})"; p = p with { Name = baseName[..Math.Min(64 - suffix.Length, baseName.Length)] + suffix }; }
            Profiles.Add(p); SelectedProfile = p; Notice = "Профиль импортирован. Настройки применены.";
        }
        catch (Exception e) { Notice = e.Message; }
    }
    private void ExportProfile()
    {
        var dialog = new SaveFileDialog { Filter = "Профиль MicPilot|*.json", FileName = "MicPilot-profile.json" }; if (dialog.ShowDialog() != true) return;
        try { ProfileStore.Export(dialog.FileName, new(ProfileName, "Настройки MicPilot", _settings)); Notice = "Профиль экспортирован."; } catch (Exception e) { Notice = e.Message; }
    }
    private void Tick()
    {
        var snapshot = _engine.Snapshot();
        if (snapshot is not null)
        {
            _inputDb = AudioMath.Db(snapshot.Meters.InputPeak); _outputDb = AudioMath.Db(snapshot.Meters.OutputPeak);
            _dsp = $"{snapshot.ProcessingMs:0.00} мс / блок 10 мс"; _latency = $"≈ {snapshot.EstimatedLatencyMs} мс";
            _reduction = $"Сжатие {snapshot.Meters.CompressionDb:0.#} dB · паузы {snapshot.Meters.GateReductionDb:0.#} dB";
            _diagnostics = $"Очередь {snapshot.QueueMs} мс · сбросы буфера {snapshot.BufferResets} · входных пиков {snapshot.Meters.ClippedInputSamples}";
            LevelsChanged?.Invoke(_inputDb, _outputDb);
        }
        else { _dsp = "—"; _latency = "—"; _reduction = "—"; _diagnostics = "Нет активного аудиопотока"; }
        Changed(nameof(InputLevel)); Changed(nameof(OutputLevel)); Changed(nameof(DspText)); Changed(nameof(LatencyText)); Changed(nameof(ReductionText)); Changed(nameof(DiagnosticsText));
        if (++_ticks % 10 != 0) return;
        _process.Refresh(); var elapsed = Stopwatch.GetElapsedTime(_lastTick).TotalSeconds; var cpuTime = _process.TotalProcessorTime;
        _cpu = Math.Clamp((cpuTime - _lastCpu).TotalSeconds / Math.Max(.1, elapsed) / Environment.ProcessorCount * 100, 0, 100);
        _ram = _process.WorkingSet64 / 1048576d; _lastCpu = cpuTime; _lastTick = Stopwatch.GetTimestamp(); _cpuMeasured = true;
        Changed(nameof(CpuText)); Changed(nameof(RamText)); CpuChanged?.Invoke(_cpu);
    }
    private void ScheduleSave() { if (_diagnostic) return; _saveTimer.Stop(); _saveTimer.Start(); }
    private bool SaveState(bool explicitSave = false)
    {
        if (_diagnostic) return false;
        try { _store.Save(new(SelectedInput?.Id, SelectedOutput?.Id, ProfileName, _settings, Profiles.Where(p => !MicProfile.BuiltIns.Any(b => b.Name == p.Name)).ToArray()), explicitSave); return true; }
        catch (Exception e) { Notice = "Не удалось сохранить настройки: " + e.Message; return false; }
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public void Dispose() { _timer.Stop(); _saveTimer.Stop(); _operation?.Cancel(); StopPreview(); _engine.Dispose(); SaveState(); _process.Dispose(); }
    private sealed class ArrayProvider(float[] samples) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        public int Read(Span<float> buffer) { var n = Math.Min(buffer.Length, samples.Length - _position); samples.AsSpan(_position, n).CopyTo(buffer); _position += n; return n; }
    }
}
