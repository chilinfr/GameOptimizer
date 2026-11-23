using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace GameOptimizer;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly List<TweakItem> _allTweaks = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly Queue<double> _cpuHistory = new();
    private bool _initialized;
    private int _tickCounter;

    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
    private static readonly Brush BadBrush = new SolidColorBrush(Color.FromRgb(0xFB, 0x71, 0x85));

    private string _searchText = "";
    private bool _isSearching;
    private string _searchSummary = "";
    private bool _isBusy;
    private string _busyText = "";
    private bool _toastVisible;
    private string _toastText = "";
    private Brush _toastAccent = OkBrush;
    private string _adminLabel = "ADMIN";
    private Brush _adminBrush = OkBrush;

    private string _cpuUsageText = "0%";
    private double _cpuUsage;
    private PointCollection _cpuGraphPoints = new();
    private string _memUsedText = "0.0 GB";
    private string _memTotalText = "из 0 GB";
    private string _memFreeText = "Свободно 0.0 GB";
    private double _memUsage;
    private string _uptimeText = "UPTIME 00:00:00";
    private string _hagsStatus = "—";
    private Brush _hagsBrush = BadBrush;
    private string _gameModeStatus = "—";
    private Brush _gameModeBrush = BadBrush;
    private string _activePlanName = "…";
    private string _appliedCountText = "0";
    private string _tweakCountText = "/ 0";
    private string _tweakSummaryText = "Сканирование системы…";
    private double _optimizationPercent;
    private string _cpuName = "…";
    private string _gpuName = "…";
    private string _osName = "…";
    private string _coreCountText = "…";
    private string _ramInstalledText = "…";
    private string _networkAdaptersText = "…";
    private string _priorityCurrentText = "…";
    private PriorityOption? _selectedPriority;
    private string _pciSummaryText = "Сканирование PCI-устройств…";
    private string _affinitySummaryText = "…";
    private string _standbyResultText = "Очистка кэша Standby List не выполнялась.";
    private string _restorePointStatus = "Точка создаётся автоматически.";
    private string _journalStatusText = "Журнал пуст.";
    private string _backupsSizeText = "…";
    private string _backupsPath = BackupManager.BaseDirectory;
    private string _profileStatusText =
        "Профиль хранит состояния всех твиков в JSON. При импорте применяются только отличия.";
    private string _appVersion = "v1.0.0 · PRO";

    public MainWindow()
    {
        InitializeComponent();
        BuildNavigation();
        BuildPriorityOptions();
        InitializeCommands();
        DataContext = this;
        NavList.SelectedIndex = 0;

        for (var i = 0; i < 39; i++)
        {
            _cpuHistory.Enqueue(0);
        }

        _toastTimer.Tick += (_, _) =>
        {
            ToastVisible = false;
            _toastTimer.Stop();
        };
        _uiTimer.Tick += (_, _) => UpdateMetrics();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ------------------------------------------------------------------
    //  Collections
    // ------------------------------------------------------------------
    public ObservableCollection<NavigationItem> NavItems { get; } = new();
    public ObservableCollection<TweakItem> KernelTweaks { get; } = new();
    public ObservableCollection<TweakItem> GpuTweaks { get; } = new();
    public ObservableCollection<TweakItem> CpuTweaks { get; } = new();
    public ObservableCollection<TweakItem> MemoryTweaks { get; } = new();
    public ObservableCollection<TweakItem> NetworkTweaks { get; } = new();
    public ObservableCollection<TweakItem> SearchResults { get; } = new();
    public ObservableCollection<PciDeviceItem> PciDevices { get; } = new();
    public ObservableCollection<AffinityDeviceItem> AffinityDevices { get; } = new();
    public ObservableCollection<BackupEntry> BackupEntries { get; } = new();
    public ObservableCollection<PriorityOption> PriorityOptions { get; } = new();

    // ------------------------------------------------------------------
    //  Commands
    // ------------------------------------------------------------------
    public ICommand CreateRestorePointCommand { get; private set; } = null!;
    public ICommand ApplyPresetCommand { get; private set; } = null!;
    public ICommand PurgeStandbyCommand { get; private set; } = null!;
    public ICommand ApplyPriorityCommand { get; private set; } = null!;
    public ICommand ExportProfileCommand { get; private set; } = null!;
    public ICommand ImportProfileCommand { get; private set; } = null!;
    public ICommand ImportRegCommand { get; private set; } = null!;
    public ICommand RollbackCommand { get; private set; } = null!;
    public ICommand OpenBackupsCommand { get; private set; } = null!;
    public ICommand RefreshDevicesCommand { get; private set; } = null!;

    // ------------------------------------------------------------------
    //  Bindable properties
    // ------------------------------------------------------------------
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                UpdateSearch();
            }
        }
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set => SetProperty(ref _isSearching, value);
    }

    public string SearchSummary
    {
        get => _searchSummary;
        private set => SetProperty(ref _searchSummary, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    public bool ToastVisible
    {
        get => _toastVisible;
        private set => SetProperty(ref _toastVisible, value);
    }

    public string ToastText
    {
        get => _toastText;
        private set => SetProperty(ref _toastText, value);
    }

    public Brush ToastAccent
    {
        get => _toastAccent;
        private set => SetProperty(ref _toastAccent, value);
    }

    public string AdminLabel
    {
        get => _adminLabel;
        private set => SetProperty(ref _adminLabel, value);
    }

    public Brush AdminBrush
    {
        get => _adminBrush;
        private set => SetProperty(ref _adminBrush, value);
    }

    public string CpuUsageText
    {
        get => _cpuUsageText;
        private set => SetProperty(ref _cpuUsageText, value);
    }

    public double CpuUsage
    {
        get => _cpuUsage;
        private set => SetProperty(ref _cpuUsage, value);
    }

    public PointCollection CpuGraphPoints
    {
        get => _cpuGraphPoints;
        private set => SetProperty(ref _cpuGraphPoints, value);
    }

    public string MemUsedText
    {
        get => _memUsedText;
        private set => SetProperty(ref _memUsedText, value);
    }

    public string MemTotalText
    {
        get => _memTotalText;
        private set => SetProperty(ref _memTotalText, value);
    }

    public string MemFreeText
    {
        get => _memFreeText;
        private set => SetProperty(ref _memFreeText, value);
    }

    public double MemUsage
    {
        get => _memUsage;
        private set => SetProperty(ref _memUsage, value);
    }

    public string UptimeText
    {
        get => _uptimeText;
        private set => SetProperty(ref _uptimeText, value);
    }

    public string HagsStatus
    {
        get => _hagsStatus;
        private set => SetProperty(ref _hagsStatus, value);
    }

    public Brush HagsBrush
    {
        get => _hagsBrush;
        private set => SetProperty(ref _hagsBrush, value);
    }

    public string GameModeStatus
    {
        get => _gameModeStatus;
        private set => SetProperty(ref _gameModeStatus, value);
    }

    public Brush GameModeBrush
    {
        get => _gameModeBrush;
        private set => SetProperty(ref _gameModeBrush, value);
    }

    public string ActivePlanName
    {
        get => _activePlanName;
        private set => SetProperty(ref _activePlanName, value);
    }

    public string AppliedCountText
    {
        get => _appliedCountText;
        private set => SetProperty(ref _appliedCountText, value);
    }

    public string TweakCountText
    {
        get => _tweakCountText;
        private set => SetProperty(ref _tweakCountText, value);
    }

    public string TweakSummaryText
    {
        get => _tweakSummaryText;
        private set => SetProperty(ref _tweakSummaryText, value);
    }

    public double OptimizationPercent
    {
        get => _optimizationPercent;
        private set => SetProperty(ref _optimizationPercent, value);
    }

    public string CpuName
    {
        get => _cpuName;
        private set => SetProperty(ref _cpuName, value);
    }

    public string GpuName
    {
        get => _gpuName;
        private set => SetProperty(ref _gpuName, value);
    }

    public string OsName
    {
        get => _osName;
        private set => SetProperty(ref _osName, value);
    }

    public string CoreCountText
    {
        get => _coreCountText;
        private set => SetProperty(ref _coreCountText, value);
    }

    public string RamInstalledText
    {
        get => _ramInstalledText;
        private set => SetProperty(ref _ramInstalledText, value);
    }

    public string NetworkAdaptersText
    {
        get => _networkAdaptersText;
        private set => SetProperty(ref _networkAdaptersText, value);
    }

    public string PriorityCurrentText
    {
        get => _priorityCurrentText;
        private set => SetProperty(ref _priorityCurrentText, value);
    }

    public PriorityOption? SelectedPriority
    {
        get => _selectedPriority;
        set => SetProperty(ref _selectedPriority, value);
    }

    public string PciSummaryText
    {
        get => _pciSummaryText;
        private set => SetProperty(ref _pciSummaryText, value);
    }

    public string AffinitySummaryText
    {
        get => _affinitySummaryText;
        private set => SetProperty(ref _affinitySummaryText, value);
    }

    public string StandbyResultText
    {
        get => _standbyResultText;
        private set => SetProperty(ref _standbyResultText, value);
    }

    public string RestorePointStatus
    {
        get => _restorePointStatus;
        private set => SetProperty(ref _restorePointStatus, value);
    }

    public string JournalStatusText
    {
        get => _journalStatusText;
        private set => SetProperty(ref _journalStatusText, value);
    }

    public string BackupsSizeText
    {
        get => _backupsSizeText;
        private set => SetProperty(ref _backupsSizeText, value);
    }

    public string BackupsPath
    {
        get => _backupsPath;
        private set => SetProperty(ref _backupsPath, value);
    }

    public string ProfileStatusText
    {
        get => _profileStatusText;
        private set => SetProperty(ref _profileStatusText, value);
    }

    public string AppVersion
    {
        get => _appVersion;
        private set => SetProperty(ref _appVersion, value);
    }

    // ------------------------------------------------------------------
    //  Setup
    // ------------------------------------------------------------------
    private void BuildNavigation()
    {
        NavItems.Add(new NavigationItem("Dashboard",
            "M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H20 V20 H14 Z", 0));
        NavItems.Add(new NavigationItem("Deep Kernel & BCD",
            "M7,7 H17 V17 H7 Z M9.5,9.5 H14.5 V14.5 H9.5 Z M9,3 V7 M15,3 V7 M9,17 V21 M15,17 V21 M3,9 H7 M3,15 H7 M17,9 H21 M17,15 H21", 1));
        NavItems.Add(new NavigationItem("GPU & Interrupts",
            "M3,7 H21 V17 H3 Z M6,10 H13 V14 H6 Z M16,11 A2,2 0 1 1 15.99,11 M6,17 V20 M18,17 V20", 2));
        NavItems.Add(new NavigationItem("CPU & Power",
            "M13,2 L5,13 H11 L10,22 L19,9 H12 Z", 3));
        NavItems.Add(new NavigationItem("Memory & Storage",
            "M3,7 H21 V17 H3 Z M6,10 V14 M10,10 V14 M14,10 V14 M18,10 V14 M6,17 V20 M18,17 V20", 4));
        NavItems.Add(new NavigationItem("Network & Hitreg",
            "M4,9 A12,12 0 0 1 20,9 M7.5,12.5 A7.5,7.5 0 0 1 16.5,12.5 M10.5,16 A3.5,3.5 0 0 1 13.5,16 M11,20 A1,1 0 1 1 11.01,20", 5));
        NavItems.Add(new NavigationItem("Backups & Config",
            "M4,12 A8,8 0 1 0 7,6 M4,3 V8 H9 M12,8 V12 L15,14", 6));
    }

    private void BuildPriorityOptions()
    {
        PriorityOptions.Add(new PriorityOption(2, "0x02 · 2 — Windows default (balanced)", "Стандартное поведение планировщика."));
        PriorityOptions.Add(new PriorityOption(0x26, "0x26 · 38 — short quanta, high priority", "Короткие кванты, активное окно получает больше CPU."));
        PriorityOptions.Add(new PriorityOption(0x2A, "0x2A · 42 — maximum responsiveness", "Максимальный отклик активного приложения в играх."));
        SelectedPriority = PriorityOptions[0];
    }

    private void InitializeCommands()
    {
        CreateRestorePointCommand = new AsyncRelayCommand(_ => CreateRestorePointAsync());
        ApplyPresetCommand = new AsyncRelayCommand(parameter => ApplyPresetAsync(parameter as string ?? "balanced"));
        PurgeStandbyCommand = new AsyncRelayCommand(_ => PurgeStandbyAsync());
        ApplyPriorityCommand = new AsyncRelayCommand(_ => ApplyPriorityAsync());
        ExportProfileCommand = new AsyncRelayCommand(_ => ExportProfileAsync());
        ImportProfileCommand = new AsyncRelayCommand(_ => ImportProfileAsync());
        ImportRegCommand = new AsyncRelayCommand(_ => ImportRegAsync());
        RollbackCommand = new AsyncRelayCommand(_ => RollbackAsync());
        OpenBackupsCommand = new RelayCommand(_ => OpenBackupsFolder());
        RefreshDevicesCommand = new AsyncRelayCommand(_ => RefreshDevicesAsync());
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            ShowToast($"Ошибка инициализации: {ex.Message}", false);
        }
    }

    private async Task InitializeAsync()
    {
        AdminLabel = App.IsAdministrator ? "ADMIN" : "LIMITED";
        AdminBrush = App.IsAdministrator ? OkBrush : BadBrush;

        IsBusy = true;
        BusyText = "Чтение реального состояния системы…";
        try
        {
            var catalog = await Task.Run(() => TweaksEngine.CreateCatalog(Notify, OnTweakChanged));
            _allTweaks.Clear();
            KernelTweaks.Clear();
            GpuTweaks.Clear();
            CpuTweaks.Clear();
            MemoryTweaks.Clear();
            NetworkTweaks.Clear();

            foreach (var tweak in catalog)
            {
                _allTweaks.Add(tweak);
                CollectionFor(tweak.Category).Add(tweak);
            }

            RefreshCounters();
            await LoadHardwareAsync();
            await RefreshDevicesAsync();

            ActivePlanName = await Task.Run(TweaksEngine.GetActivePowerPlanName);
            var currentPriority = await Task.Run(TweaksEngine.GetPrioritySeparation);
            SelectedPriority = PriorityOptions.FirstOrDefault(p => p.Value == currentPriority) ?? PriorityOptions[0];
            UpdatePriorityText();
            UpdateMetrics();
            RefreshBackupInfo();

            _uiTimer.Start();
            ShowToast("Сканирование завершено. Переключатели показывают реальное состояние Windows.", true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadHardwareAsync()
    {
        var info = await Task.Run(() =>
        {
            var memory = TweaksEngine.GetMemoryStatus();
            return (
                Cpu: TweaksEngine.GetCpuName(),
                Gpu: TweaksEngine.GetGpuNames(),
                Os: TweaksEngine.GetOsName(),
                Cores: TweaksEngine.GetPhysicalCoreCount(),
                RamKb: TweaksEngine.GetInstalledRamKb(),
                RamBytes: memory.TotalBytes,
                Adapters: TweaksEngine.GetActiveNetworkAdapters());
        });

        CpuName = info.Cpu;
        GpuName = info.Gpu;
        OsName = info.Os;
        CoreCountText = $"{info.Cores} ядер / {Environment.ProcessorCount} потоков";
        var installedGb = info.RamKb > 0
            ? info.RamKb / 1024.0 / 1024.0
            : info.RamBytes / 1024.0 / 1024.0 / 1024.0;
        RamInstalledText = $"{installedGb:0.#} GB RAM";
        NetworkAdaptersText = info.Adapters.Count == 0
            ? "Активные сетевые адаптеры не найдены."
            : string.Join("  ·  ", info.Adapters.Take(3))
              + (info.Adapters.Count > 3 ? $"  (+{info.Adapters.Count - 3})" : "");
    }

    private async Task RefreshDevicesAsync()
    {
        var infos = await Task.Run(TweaksEngine.ScanPciDevices);
        var cores = BuildCoreOptions();

        PciDevices.Clear();
        AffinityDevices.Clear();
        foreach (var info in infos)
        {
            PciDevices.Add(new PciDeviceItem(info, Notify));
            if (info.Kind is "GPU" or "USB")
            {
                AffinityDevices.Add(new AffinityDeviceItem(info, cores, Notify));
            }
        }

        PciSummaryText = PciDevices.Count == 0
            ? "PCI-устройства не найдены (или нет доступа к ветке Enum\\PCI)."
            : $"{PciDevices.Count} устройств найдено. MSI и affinity применяются после перезагрузки. " +
              "Запись в Enum\\PCI требует прав администратора.";
        AffinitySummaryText = $"{AffinityDevices.Count} устройств (GPU/USB) доступны для привязки прерываний. " +
                              "Core 0 лучше не выбирать для USB — он обслуживает системные прерывания.";
    }

    private static List<CoreOption> BuildCoreOptions()
    {
        var options = new List<CoreOption> { new(-1) };
        for (var i = 0; i < Environment.ProcessorCount; i++)
        {
            options.Add(new CoreOption(i));
        }

        return options;
    }

    private ObservableCollection<TweakItem> CollectionFor(TweakCategory category) => category switch
    {
        TweakCategory.Kernel => KernelTweaks,
        TweakCategory.Gpu => GpuTweaks,
        TweakCategory.Cpu => CpuTweaks,
        TweakCategory.Memory => MemoryTweaks,
        TweakCategory.Network => NetworkTweaks,
        _ => KernelTweaks
    };

    // ------------------------------------------------------------------
    //  Live metrics
    // ------------------------------------------------------------------
    private void UpdateMetrics()
    {
        try
        {
            var cpu = TweaksEngine.GetCpuUsage();
            CpuUsage = cpu;
            CpuUsageText = $"{cpu:0}%";

            _cpuHistory.Enqueue(cpu);
            while (_cpuHistory.Count > 40)
            {
                _cpuHistory.Dequeue();
            }

            var points = new PointCollection();
            var index = 0;
            foreach (var value in _cpuHistory)
            {
                points.Add(new Point(index * 6, 52 - Math.Clamp(value, 0, 100) / 100.0 * 48));
                index++;
            }

            CpuGraphPoints = points;

            var memory = TweaksEngine.GetMemoryStatus();
            var totalGb = memory.TotalBytes / 1024.0 / 1024.0 / 1024.0;
            var availableGb = memory.AvailableBytes / 1024.0 / 1024.0 / 1024.0;
            var usedGb = Math.Max(0, totalGb - availableGb);
            MemUsedText = $"{usedGb:0.0} GB";
            MemTotalText = $"из {totalGb:0.0} GB";
            MemFreeText = $"Свободно {availableGb:0.0} GB";
            MemUsage = memory.LoadPercent;
            UptimeText = FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64));

            var hags = TweaksEngine.IsHagsEnabled();
            HagsStatus = hags ? "ВКЛЮЧЕНО" : "ВЫКЛЮЧЕНО";
            HagsBrush = hags ? OkBrush : BadBrush;

            var gameMode = TweaksEngine.IsGameModeEnabled();
            GameModeStatus = gameMode ? "ВКЛЮЧЕНО" : "ВЫКЛЮЧЕНО";
            GameModeBrush = gameMode ? OkBrush : BadBrush;

            _tickCounter++;
            if (_tickCounter % 10 == 1)
            {
                _ = RefreshPowerPlanAsync();
            }
        }
        catch
        {
            // Monitoring must never break the UI.
        }
    }

    private async Task RefreshPowerPlanAsync()
    {
        try
        {
            var name = await Task.Run(TweaksEngine.GetActivePowerPlanName);
            if (!string.IsNullOrWhiteSpace(name))
            {
                ActivePlanName = name;
            }
        }
        catch
        {
            // ignored
        }
    }

    private static string FormatUptime(TimeSpan uptime)
        => uptime.TotalDays >= 1
            ? $"UPTIME {(int)uptime.TotalDays}d {uptime:hh\\:mm}"
            : $"UPTIME {uptime:hh\\:mm\\:ss}";

    private void RefreshCounters()
    {
        var active = _allTweaks.Count(t => t.IsOn);
        var total = _allTweaks.Count;
        AppliedCountText = active.ToString();
        TweakCountText = $"/ {total}";
        OptimizationPercent = total == 0 ? 0 : active * 100.0 / total;
        TweakSummaryText = total == 0
            ? "Сканирование системы…"
            : active == 0
                ? "Все твики в состоянии Windows. Выберите пресет или отдельные карточки."
                : $"Активно {active} из {total} оптимизаций. Перезагрузка нужна только для помеченных REBOOT.";
    }

    private void OnTweakChanged()
    {
        RefreshCounters();
        RefreshBackupCounters();
    }

    private void Notify(string message, bool success) => ShowToast(message, success);

    private void ShowToast(string message, bool success)
    {
        ToastText = message;
        ToastAccent = success ? OkBrush : BadBrush;
        ToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ------------------------------------------------------------------
    //  Commands
    // ------------------------------------------------------------------
    private async Task RunBusyAsync(string text, Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        BusyText = text;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowToast($"Ошибка: {ex.Message}", false);
        }
        finally
        {
            IsBusy = false;
            RefreshCounters();
        }
    }

    private async Task CreateRestorePointAsync()
    {
        await RunBusyAsync("Создание точки восстановления Windows…", async () =>
        {
            BackupManager.ResetRestorePointAttempt();
            var result = await BackupManager.EnsureRestorePointAsync();
            ShowToast(result.Message, result.Success);
            RefreshBackupInfo();
        });
    }

    private async Task ApplyPriorityAsync()
    {
        if (SelectedPriority is null)
        {
            return;
        }

        var selected = SelectedPriority;
        await RunBusyAsync($"Win32PrioritySeparation = 0x{selected.Value:X2}…", async () =>
        {
            var result = await Task.Run(() => TweaksEngine.ApplyPrioritySeparation(selected.Value));
            ShowToast(result.Message, result.Success);
            UpdatePriorityText();
        });
    }

    private async Task PurgeStandbyAsync()
    {
        await RunBusyAsync("Очистка Standby List через NtSetSystemInformation…", async () =>
        {
            var message = "";
            await Task.Run(() =>
            {
                TweaksEngine.PurgeStandbyList(out var result);
                message = result;
            });
            StandbyResultText = message;
            UpdateMetrics();
            ShowToast(message, message.StartsWith("Standby", StringComparison.OrdinalIgnoreCase));
        });
    }

    private async Task ApplyPresetAsync(string preset)
    {
        if (_allTweaks.Count == 0)
        {
            ShowToast("Каталог твиков ещё не загружен.", false);
            return;
        }

        var isStock = preset == "stock";
        var isEsports = preset == "esports";
        var title = isStock ? "RESTORE STOCK" : isEsports ? "ESPORTS PRO" : "BALANCED GAMING";
        var description = isStock
            ? "Будет выполнен полный откат всех изменений GameOptimizer, сброс сети, BCD-оверрайдов и схемы питания.\n\n" +
              "Текущие изменения будут отменены. Продолжить?"
            : isEsports
                ? "Будут применены таймеры ядра, MMCSS, HAGS, MPO, Game DVR, сеть, питание и отключены фоновые службы.\n\n" +
                  "Потребуется перезагрузка. Продолжить?"
                : "Будут применены только безопасные твики без отключения служб и BCD.\n\nПродолжить?";

        if (MessageBox.Show(this, description, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
        {
            return;
        }

        await RunBusyAsync($"Применение пресета {title}…", async () =>
        {
            var restorePoint = await BackupManager.EnsureRestorePointAsync();
            if (!restorePoint.Success && !BackupManager.RestorePointWarningAcknowledged)
            {
                var answer = MessageBox.Show(
                    this,
                    $"Не удалось создать точку восстановления:\n\n{restorePoint.Message}\n\n" +
                    "Все изменения журналируются, их можно откатить.\n\nПродолжить?",
                    "Безопасность системы",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    ShowToast("Пресет отменён: нет точки восстановления.", false);
                    return;
                }

                BackupManager.RestorePointWarningAcknowledged = true;
            }

            if (isStock)
            {
                BusyText = "Полный откат журнала изменений…";
                var rollback = await BackupManager.RollbackAsync();
                BusyText = "Сброс сети, BCD и схемы питания…";
                var extras = await Task.Run(TweaksEngine.RestoreStockExtras);
                await RefreshAllStatesAsync();
                RefreshBackupInfo();
                ShowToast(
                    $"{(rollback.Success ? "Откат выполнен." : rollback.Message)} {extras.Message}",
                    rollback.Success);
                return;
            }

            var ids = isEsports ? TweaksEngine.EsportsPreset : TweaksEngine.BalancedPreset;
            var applied = 0;
            var skipped = 0;
            var failed = 0;

            foreach (var id in ids)
            {
                var tweak = _allTweaks.FirstOrDefault(t => t.Id == id);
                if (tweak is null)
                {
                    skipped++;
                    continue;
                }

                if (tweak.IsOn)
                {
                    skipped++;
                    continue;
                }

                BusyText = $"Применение: {tweak.Title}";
                try
                {
                    var result = await Task.Run(tweak.ApplyForPreset);
                    if (result.Success)
                    {
                        applied++;
                    }
                    else
                    {
                        failed++;
                        Debug.WriteLine($"Preset {id}: {result.Message}");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    Debug.WriteLine($"Preset {id}: {ex.Message}");
                }
            }

            if (isEsports)
            {
                BusyText = "Win32PrioritySeparation = 0x2A…";
                await Task.Run(() => TweaksEngine.ApplyPrioritySeparation(0x2A));
                SelectedPriority = PriorityOptions.FirstOrDefault(p => p.Value == 0x2A) ?? SelectedPriority;
                UpdatePriorityText();
            }

            await RefreshAllStatesAsync();
            RefreshBackupInfo();
            var suffix = failed > 0 ? $", ошибок: {failed} (см. Debug Output)" : "";
            ShowToast(
                $"Пресет {title} применён: {applied} изменений, {skipped} уже было включено{suffix}.",
                failed == 0);
        });
    }

    private async Task RefreshAllStatesAsync()
    {
        var states = await Task.Run(() =>
            _allTweaks.Select(t => (Tweak: t, State: t.QueryState())).ToList());
        foreach (var (tweak, state) in states)
        {
            tweak.SetState(state);
        }

        RefreshCounters();
    }

    private async Task RollbackAsync()
    {
        var answer = MessageBox.Show(
            this,
            "Полный откат восстановит исходные значения реестра, BCD и служб из журнала изменений " +
            "и сбросит сетевые параметры.\n\nПродолжить?",
            "Полный откат",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await RunBusyAsync("Полный откат изменений…", async () =>
        {
            var result = await BackupManager.RollbackAsync();
            BusyText = "Сброс сетевых параметров…";
            await Task.Run(TweaksEngine.ResetNetworkToDefaults);
            await RefreshAllStatesAsync();
            RefreshBackupInfo();
            ShowToast(result.Message, result.Success);
        });
    }

    private async Task ExportProfileAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Экспорт профиля GameOptimizer",
            Filter = "GameOptimizer profile (*.json)|*.json",
            FileName = $"GameOptimizer_{DateTime.Now:yyyyMMdd_HHmm}.json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            BackupManager.ExportProfile(dialog.FileName, _allTweaks);
            ShowToast($"Профиль сохранён: {Path.GetFileName(dialog.FileName)}", true);
        }
        catch (Exception ex)
        {
            ShowToast($"Ошибка экспорта: {ex.Message}", false);
        }

        await Task.CompletedTask;
    }

    private async Task ImportProfileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Импорт профиля GameOptimizer",
            Filter = "GameOptimizer profile (*.json)|*.json|JSON (*.json)|*.json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        OptimizerProfile profile;
        try
        {
            profile = BackupManager.LoadProfile(dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowToast($"Некорректный профиль: {ex.Message}", false);
            return;
        }

        if (MessageBox.Show(
                this,
                $"Профиль содержит {profile.Items.Count} записей.\n\n" +
                "Некоторые твики могут быть Advanced/Risky. Применить отличия?",
                "Импорт профиля",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunBusyAsync("Импорт профиля…", async () =>
        {
            var restorePoint = await BackupManager.EnsureRestorePointAsync();
            if (!restorePoint.Success && !BackupManager.RestorePointWarningAcknowledged)
            {
                BackupManager.RestorePointWarningAcknowledged = true;
            }

            var changed = 0;
            var failed = 0;
            foreach (var item in profile.Items)
            {
                var tweak = _allTweaks.FirstOrDefault(t => t.Id == item.Id);
                if (tweak is null || tweak.IsOn == item.Enabled)
                {
                    continue;
                }

                BusyText = $"Импорт: {tweak.Title}";
                try
                {
                    var result = await Task.Run(() => item.Enabled ? tweak.ApplyForPreset() : tweak.RevertForPreset());
                    if (result.Success)
                    {
                        changed++;
                    }
                    else
                    {
                        failed++;
                    }
                }
                catch
                {
                    failed++;
                }
            }

            await RefreshAllStatesAsync();
            RefreshBackupInfo();
            ShowToast(
                $"Профиль импортирован: изменено {changed}" + (failed > 0 ? $", ошибок: {failed}" : "."),
                failed == 0);
        });
    }

    private async Task ImportRegAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Импорт ветки реестра из .reg",
            Filter = "Registry backup (*.reg)|*.reg|Все файлы (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (MessageBox.Show(
                this,
                "Импорт .reg перезапишет указанные в файле ключи реестра.\n\n" +
                "Продолжить? Перед импортом рекомендуется создать точку восстановления.",
                "Импорт .reg",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunBusyAsync("Импорт ветки реестра…", async () =>
        {
            var result = await Task.Run(() => BackupManager.ImportRegFile(dialog.FileName));
            ShowToast(result.Message, result.Success);
            await RefreshAllStatesAsync();
            RefreshBackupInfo();
        });
    }

    private void OpenBackupsFolder()    {
        try
        {
            Directory.CreateDirectory(BackupManager.BaseDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = BackupManager.BaseDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowToast($"Не удалось открыть папку: {ex.Message}", false);
        }
    }

    private void RefreshBackupInfo()
    {
        try
        {
            var entries = BackupManager.GetBackupEntries();
            RestorePointStatus = BackupManager.RestorePointCreated
                ? "Точка восстановления создана в этой сессии."
                : "Точка создаётся автоматически перед первым изменением.";
            BackupsPath = BackupManager.BaseDirectory;
            BackupsSizeText = $"{BackupManager.GetBackupsSizeText()} · файлов: {entries.Count}";
            BackupEntries.Clear();
            foreach (var entry in entries)
            {
                BackupEntries.Add(entry);
            }

            RefreshBackupCounters();
        }
        catch (Exception ex)
        {
            BackupsSizeText = $"Ошибка чтения бэкапов: {ex.Message}";
        }
    }

    private void RefreshBackupCounters()
    {
        var count = BackupManager.JournalCount;
        JournalStatusText = count == 0
            ? "Журнал пуст — приложение ещё не меняло систему."
            : $"{count} изменений в журнале. Полный откат вернёт реестр, BCD и службы к исходным значениям.";
    }

    private void UpdatePriorityText()
    {
        var current = TweaksEngine.GetPrioritySeparation();
        PriorityCurrentText = $"Сейчас: 0x{current:X2} ({current})";
    }

    // ------------------------------------------------------------------
    //  Search
    // ------------------------------------------------------------------
    private void UpdateSearch()
    {
        IsSearching = !string.IsNullOrWhiteSpace(SearchText);
        SearchResults.Clear();
        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            SearchSummary = "";
            return;
        }

        var matches = _allTweaks
            .Where(t =>
                t.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || t.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                || t.CategoryName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || t.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(80)
            .ToList();

        foreach (var match in matches)
        {
            SearchResults.Add(match);
        }

        SearchSummary = matches.Count == 0
            ? $"По запросу «{query}» ничего не найдено. Попробуйте «timer», «GPU», «nagle» или «telemetry»."
            : $"Найдено {matches.Count} твиков по запросу «{query}». Настройки можно менять прямо здесь.";
    }

    // ------------------------------------------------------------------
    //  Window chrome
    // ------------------------------------------------------------------
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // ignored
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SearchInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchText = "";
            Keyboard.ClearFocus();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _uiTimer.Stop();
        _toastTimer.Stop();
        base.OnClosed(e);
    }

    // ------------------------------------------------------------------
    //  INotifyPropertyChanged
    // ------------------------------------------------------------------
    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
