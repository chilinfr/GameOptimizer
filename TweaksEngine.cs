using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameOptimizer;

/// <summary>
/// All system access lives here: registry (x64 view), BCD, powercfg, netsh, services,
/// PowerShell, WMI, performance counters and native ntdll/kernel32 calls.
/// </summary>
public static class TweaksEngine
{
    private const RegistryHive Lm = RegistryHive.LocalMachine;
    private const RegistryHive Cu = RegistryHive.CurrentUser;
    private const RegistryValueKind Dw = RegistryValueKind.DWord;

    private const string GraphicsDriversKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string MemoryManagementKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string FileSystemKey = @"SYSTEM\CurrentControlSet\Control\FileSystem";
    private const string MultimediaKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string MmcssGamesKey = MultimediaKey + @"\Tasks\Games";
    private const string DwmKey = @"SOFTWARE\Microsoft\Windows\Dwm";
    private const string GameDvrPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\GameDVR";
    private const string GameConfigStoreKey = @"System\GameConfigStore";
    private const string GameDvrKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string GameBarKey = @"Software\Microsoft\GameBar";
    private const string TelemetryPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";
    private const string BackgroundAppsKey = @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications";
    private const string AppPrivacyPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy";
    private const string TcpipInterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private const string PriorityControlKey = @"SYSTEM\CurrentControlSet\Control\PriorityControl";

    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string NetClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    private const string UsbClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{36fc9e60-c465-11cf-8056-444553540000}";
    private const string PciEnumKey = @"SYSTEM\CurrentControlSet\Enum\PCI";

    private const string SubProcessor = "SUB_PROCESSOR";
    private const string CoreParkingGuid = "0cc5b647-c1df-4637-891a-dec35c318583";
    private const string IdleDisableGuid = "5d76a2ca-e8c0-402f-a133-2158492d58ad";
    private const string UltimatePerformanceGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";

    private static readonly string BcdEdit = Path.Combine(Environment.SystemDirectory, "bcdedit.exe");
    private static readonly string PowerCfg = Path.Combine(Environment.SystemDirectory, "powercfg.exe");
    private static readonly string NetSh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
    private static readonly string ScExe = Path.Combine(Environment.SystemDirectory, "sc.exe");
    private static readonly string PowerShellExe = Path.Combine(
        Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    private static readonly object CpuGate = new();
    private static PerformanceCounter? _cpuCounter;
    private static bool _cpuCounterUnavailable;
    private static long _prevIdle;
    private static long _prevKernel;
    private static long _prevUser;
    private static Encoding? _consoleEncoding;

    private static Encoding ConsoleEncoding
    {
        get
        {
            if (_consoleEncoding is not null)
            {
                return _consoleEncoding;
            }

            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _consoleEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch
            {
                _consoleEncoding = Encoding.UTF8;
            }

            return _consoleEncoding;
        }
    }

    // ---------------------------------------------------------------------
    //  Tweaks catalog
    // ---------------------------------------------------------------------

    public static List<TweakItem> CreateCatalog(Action<string, bool> notify, Action changed)
    {
        var list = new List<TweakItem>();
        TweakItem Make(
            string id,
            TweakCategory category,
            string title,
            string description,
            RiskLevel risk,
            bool reboot,
            Func<bool> read,
            Func<TweakResult> apply,
            Func<TweakResult> revert,
            string? warning = null)
        {
            var item = new TweakItem(
                id, category, title, description, risk, reboot,
                read, apply, revert, notify, changed, warning);
            list.Add(item);
            return item;
        }

        var nvidia = HasNvidiaGpu();
        var amd = HasAmdGpu();

        // ---------------- Deep Kernel & BCD ----------------
        Make("bcd-dynamictick", TweakCategory.Kernel, "Dynamic Tick: выключен",
            "Убирает динамическое тиканье ядра и фиксирует системный таймер. Стабилизирует frame time и снижает микро-статтеры. Требуется перезагрузка.",
            RiskLevel.Advanced, true,
            () => string.Equals(BcdGet("disabledynamictick"), "Yes", StringComparison.OrdinalIgnoreCase),
            () => SetBcd("disabledynamictick", "yes", "Dynamic Tick отключён. Перезагрузите ПК."),
            () => ClearBcd("disabledynamictick", "Dynamic Tick снова управляется Windows. Перезагрузите ПК."));

        Make("bcd-tscsync", TweakCategory.Kernel, "TSC Sync Policy: Enhanced",
            "Включает расширенную синхронизацию тактового генератора ядер (invariant TSC). Помогает ровнее распределять кадры на многоядерных CPU. Требуется перезагрузка.",
            RiskLevel.Advanced, true,
            () => string.Equals(BcdGet("tscsyncpolicy"), "Enhanced", StringComparison.OrdinalIgnoreCase),
            () => SetBcd("tscsyncpolicy", "Enhanced", "TSC Sync Policy = Enhanced. Перезагрузите ПК."),
            () => ClearBcd("tscsyncpolicy", "Политика TSC возвращена к значению Windows. Перезагрузите ПК."));

        Make("bcd-hpet", TweakCategory.Kernel, "HPET / Platform Clock: без принудиловки",
            "Удаляет принудительный platform clock (HPET) из загрузчика, оставляя Windows выбирать TSC. Выключение твика принудительно включает HPET — только для диагностики. Требуется перезагрузка.",
            RiskLevel.Advanced, true,
            () =>
            {
                var value = BcdGet("useplatformclock");
                return value is null || !IsTruthy(value);
            },
            () => ClearBcd("useplatformclock", "Принудительный platform clock удалён. Перезагрузите ПК."),
            () => SetBcd("useplatformclock", "true", "HPET принудительно включён. Перезагрузите ПК."));

        Make("timer-resolution", TweakCategory.Kernel, "Timer Resolution: 0.5 ms",
            "Запрашивает у ntdll минимальный системный таймер 0.5 мс вместо стандартных 15.6 мс. Работает, пока запущен GameOptimizer; после закрытия Windows возвращает обычный интервал.",
            RiskLevel.Advanced, false,
            IsHighTimerResolution,
            () => SetTimerResolution(5000, true, "Timer Resolution = 0.5 мс (пока приложение запущено)."),
            () => SetTimerResolution(5000, false, "Запрос 0.5 мс снят, Windows управляет таймером сама."));

        Make("mmcss-responsiveness", TweakCategory.Kernel, "MMCSS SystemResponsiveness = 0",
            "Отдаёт мультимедийному планировщику максимум ресурсов CPU (0% зарезервировано под фоновые задачи). Снижает задержку аудио и ввода в играх.",
            RiskLevel.Advanced, false,
            () => ReadInt(Lm, MultimediaKey, "SystemResponsiveness") == 0,
            () =>
            {
                SetDword(Lm, MultimediaKey, "SystemResponsiveness", 0);
                return TweakResult.Ok("SystemResponsiveness = 0. Рекомендуется перезагрузка.");
            },
            () =>
            {
                SetDword(Lm, MultimediaKey, "SystemResponsiveness", 20);
                return TweakResult.Ok("SystemResponsiveness возвращён к 20 (значение Windows).");
            });

        Make("mmcss-games", TweakCategory.Kernel, "MMCSS Games Profile: High",
            "Выставляет профилю Tasks\\Games приоритет GPU 8, CPU 6, Scheduling Category High и SFIO High. Игры получают более агрессивное планирование ввода-вывода.",
            RiskLevel.Safe, false,
            () => ReadInt(Lm, MmcssGamesKey, "GPU Priority") == 8
                  && ReadInt(Lm, MmcssGamesKey, "Priority") == 6
                  && ReadString(Lm, MmcssGamesKey, "Scheduling Category") == "High"
                  && ReadString(Lm, MmcssGamesKey, "SFIO Priority") == "High",
            () =>
            {
                SetDword(Lm, MmcssGamesKey, "GPU Priority", 8);
                SetDword(Lm, MmcssGamesKey, "Priority", 6);
                SetString(Lm, MmcssGamesKey, "Scheduling Category", "High");
                SetString(Lm, MmcssGamesKey, "SFIO Priority", "High");
                return TweakResult.Ok("MMCSS Games Profile = High. Перезагрузка не обязательна.");
            },
            () =>
            {
                SetDword(Lm, MmcssGamesKey, "GPU Priority", 8);
                SetDword(Lm, MmcssGamesKey, "Priority", 2);
                SetString(Lm, MmcssGamesKey, "Scheduling Category", "Medium");
                SetString(Lm, MmcssGamesKey, "SFIO Priority", "Normal");
                return TweakResult.Ok("MMCSS Games Profile возвращён к значениям Windows.");
            });

        Make("cpu-mitigations", TweakCategory.Kernel, "Spectre / Meltdown mitigations: OFF",
            "Отключает программные заплатки CPU для Spectre/Meltdown (FeatureSettingsOverride = 3). Даёт заметный прирост в CPU-bound сценах, но снижает защиту от side-channel атак.",
            RiskLevel.Risky, true,
            () => ReadInt(Lm, MemoryManagementKey, "FeatureSettingsOverride") == 3
                  && ReadInt(Lm, MemoryManagementKey, "FeatureSettingsOverrideMask") == 3,
            () =>
            {
                SetDword(Lm, MemoryManagementKey, "FeatureSettingsOverride", 3);
                SetDword(Lm, MemoryManagementKey, "FeatureSettingsOverrideMask", 3);
                return TweakResult.Ok("CPU-митигации отключены. Перезагрузите ПК. Безопасность снижена осознанно.");
            },
            () =>
            {
                DeleteValue(Lm, MemoryManagementKey, "FeatureSettingsOverride");
                DeleteValue(Lm, MemoryManagementKey, "FeatureSettingsOverrideMask");
                return TweakResult.Ok("CPU-митигации снова управляются Windows. Перезагрузите ПК.");
            },
            "Этот твик снижает защиту системы отSpectre/Meltdown. Включайте только если понимаете риск.");

        // ---------------- GPU, latency & interrupts ----------------
        Make("hags", TweakCategory.Gpu, "Hardware-Accelerated GPU Scheduling",
            "Включает HAGS: планирование GPU-очередей аппаратно, меньше участия CPU и ниже задержка кадра. Требуется перезагрузка.",
            RiskLevel.Safe, true,
            () => ReadInt(Lm, GraphicsDriversKey, "HwSchMode") == 2,
            () =>
            {
                SetDword(Lm, GraphicsDriversKey, "HwSchMode", 2);
                return TweakResult.Ok("HAGS включён. Перезагрузите ПК.");
            },
            () =>
            {
                SetDword(Lm, GraphicsDriversKey, "HwSchMode", 1);
                return TweakResult.Ok("HAGS выключен. Перезагрузите ПК.");
            });

        Make("game-mode", TweakCategory.Gpu, "Windows Game Mode",
            "Разрешает Windows отдавать приоритет активной игре и подавлять фоновые задачи. Безопасный системный твик.",
            RiskLevel.Safe, false,
            () => ReadInt(Cu, GameBarKey, "AutoGameModeEnabled") == 1
                  && ReadInt(Cu, GameBarKey, "AllowAutoGameMode") == 1,
            () =>
            {
                SetDword(Cu, GameBarKey, "AutoGameModeEnabled", 1);
                SetDword(Cu, GameBarKey, "AllowAutoGameMode", 1);
                return TweakResult.Ok("Game Mode включён.");
            },
            () =>
            {
                SetDword(Cu, GameBarKey, "AutoGameModeEnabled", 0);
                SetDword(Cu, GameBarKey, "AllowAutoGameMode", 0);
                return TweakResult.Ok("Game Mode выключен.");
            });

        Make("gamedvr", TweakCategory.Gpu, "Game DVR / Background Recording: OFF",
            "Отключает фоновую запись, игровую панель и захват экрана Game DVR. Убирает часть фоновой нагрузки на GPU и диск.",
            RiskLevel.Safe, false,
            () => ReadInt(Cu, GameConfigStoreKey, "GameDVR_Enabled") == 0
                  && ReadInt(Cu, GameDvrKey, "AppCaptureEnabled") == 0,
            () =>
            {
                SetDword(Cu, GameConfigStoreKey, "GameDVR_Enabled", 0);
                SetDword(Cu, GameDvrKey, "AppCaptureEnabled", 0);
                SetDword(Lm, GameDvrPolicyKey, "AllowGameDVR", 0);
                return TweakResult.Ok("Game DVR отключён.");
            },
            () =>
            {
                SetDword(Cu, GameConfigStoreKey, "GameDVR_Enabled", 1);
                SetDword(Cu, GameDvrKey, "AppCaptureEnabled", 1);
                DeleteValue(Lm, GameDvrPolicyKey, "AllowGameDVR");
                return TweakResult.Ok("Game DVR снова включён.");
            });

        Make("mpo", TweakCategory.Gpu, "Multi-Plane Overlay (MPO): OFF",
            "Отключает MPO через OverlayTestMode = 5. Убирает микрофризы и статтеры в оконном и безрамочном режиме на части GPU. Требуется перезагрузка.",
            RiskLevel.Advanced, true,
            () => ReadInt(Lm, DwmKey, "OverlayTestMode") == 5,
            () =>
            {
                SetDword(Lm, DwmKey, "OverlayTestMode", 5);
                return TweakResult.Ok("MPO отключён. Перезагрузите ПК.");
            },
            () =>
            {
                DeleteValue(Lm, DwmKey, "OverlayTestMode");
                return TweakResult.Ok("MPO возвращён к значению Windows. Перезагрузите ПК.");
            });

        Make("tdr", TweakCategory.Gpu, "TDR Delay: 10 секунд",
            "Увеличивает таймаут сброса драйвера GPU (TdrDelay / TdrDdiDelay) до 10 секунд. Помогает при тяжёлых сценах и оверклоке, но зависший драйвер дольше не перезапускается.",
            RiskLevel.Advanced, true,
            () => ReadInt(Lm, GraphicsDriversKey, "TdrDelay") == 10
                  && ReadInt(Lm, GraphicsDriversKey, "TdrDdiDelay") == 10,
            () =>
            {
                SetDword(Lm, GraphicsDriversKey, "TdrDelay", 10);
                SetDword(Lm, GraphicsDriversKey, "TdrDdiDelay", 10);
                return TweakResult.Ok("TDR timeout = 10 сек. Перезагрузите ПК.");
            },
            () =>
            {
                DeleteValue(Lm, GraphicsDriversKey, "TdrDelay");
                DeleteValue(Lm, GraphicsDriversKey, "TdrDdiDelay");
                return TweakResult.Ok("TDR timeout возвращён к значению Windows. Перезагрузите ПК.");
            });

        if (nvidia)
        {
            Make("nvidia-telemetry", TweakCategory.Gpu, "NVIDIA Telemetry: OFF",
                "Останавливает и отключает службу NvTelemetryContainer и телеметрические задачи NVIDIA. Не затрагивает драйвер и панель управления.",
                RiskLevel.Safe, false,
                () => IsServiceMissingOrDisabled("NvTelemetryContainer"),
                () => SetNvidiaTelemetry(false),
                () => SetNvidiaTelemetry(true));

            Make("nvidia-hdcp", TweakCategory.Gpu, "NVIDIA HDCP: OFF",
                "Отключает HDCP-проверки драйвера NVIDIA (RMHdcpKeyglobZero = 1) на всех найденных GPU. Может помочь с захватом и некоторыми мониторами; DRM-контент может не проигрываться. Требуется перезагрузка.",
                RiskLevel.Advanced, true,
                () => AllGpuValuesEqual(true, "RMHdcpKeyglobZero", 1),
                () => SetAllGpuValues(true, "RMHdcpKeyglobZero", 1, "HDCP отключён. Перезагрузите ПК."),
                () => DeleteAllGpuValues(true, "RMHdcpKeyglobZero", "HDCP снова управляется NVIDIA. Перезагрузите ПК."));
        }

        if (amd)
        {
            Make("amd-ulps", TweakCategory.Gpu, "AMD ULPS: OFF",
                "Отключает Ultra Low Power State для видеокарт AMD (EnableUlps = 0). Убирает провалы частот и задержки выхода из энергосбережения. Требуется перезагрузка.",
                RiskLevel.Advanced, true,
                () => AllGpuValuesEqual(false, "EnableUlps", 0),
                () => SetAllGpuValues(false, "EnableUlps", 0, "ULPS отключён. Перезагрузите ПК."),
                () => SetAllGpuValues(false, "EnableUlps", 1, "ULPS снова включён. Перезагрузите ПК."));
        }

        // ---------------- CPU & Power ----------------
        Make("ultimate-performance", TweakCategory.Cpu, "Ultimate Performance план",
            "Создаёт и активирует скрытую схему «Ultimate Performance» (e9a42b02…), убирает парковку ядер и агрессивные энергосберегающие паузы. Для ноутбука — только от сети.",
            RiskLevel.Advanced, false,
            IsUltimatePerformanceActive,
            EnableUltimatePerformance,
            () =>
            {
                SetActivePowerPlan(Guid.Parse(BalancedGuid));
                return TweakResult.Ok("Активирована схема «Сбалансированная».");
            });

        Make("core-parking", TweakCategory.Cpu, "Core Parking: OFF (CPMINCORES = 100)",
            "Запрещает парковку ядер: все потоки остаются доступными. Снижает задержку пробуждения и разброс frame time, повышает энергопотребление.",
            RiskLevel.Advanced, false,
            () => QueryPowerSetting(CoreParkingGuid).Ac == 100,
            () => SetPowerSetting(CoreParkingGuid, 100, "Парковка ядер отключена (CPMINCORES = 100)."),
            () => SetPowerSetting(CoreParkingGuid, 0, "Парковка ядер снова разрешена (CPMINCORES = 0)."));

        Make("c-states", TweakCategory.Cpu, "CPU Idle / C-States: OFF",
            "Запрещает процессору уходить в idle-состояния (IDLEDISABLE = 1). Нулевая задержка пробуждения ядер, но выше температура и потребление. Требуется схема питания с поддержкой параметра.",
            RiskLevel.Advanced, false,
            () => QueryPowerSetting(IdleDisableGuid).Ac == 1,
            () => SetPowerSetting(IdleDisableGuid, 1, "C-States отключены (IDLEDISABLE = 1)."),
            () => SetPowerSetting(IdleDisableGuid, 0, "C-States снова управляются Windows."));

        // ---------------- Memory, storage & services ----------------
        Make("disable-paging-executive", TweakCategory.Memory, "DisablePagingExecutive",
            "Удерживает ядро и драйверы в физической ОЗУ, не выгружая их в pagefile. Снижает задержки при обращении к системным компонентам. Требуется перезагрузка.",
            RiskLevel.Advanced, true,
            () => ReadInt(Lm, MemoryManagementKey, "DisablePagingExecutive") == 1,
            () =>
            {
                SetDword(Lm, MemoryManagementKey, "DisablePagingExecutive", 1);
                return TweakResult.Ok("Ядро остаётся в ОЗУ. Перезагрузите ПК.");
            },
            () =>
            {
                DeleteValue(Lm, MemoryManagementKey, "DisablePagingExecutive");
                return TweakResult.Ok("Поведение paging executive возвращено к Windows. Перезагрузите ПК.");
            });

        Make("svchost-split", TweakCategory.Memory, "SvcHostSplitThresholdInKB под вашу ОЗУ",
            "Автоматически выставляет порог разделения svchost равным объёму установленной RAM. Меньше процессов — меньше переключений контекста. Требуется перезагрузка.",
            RiskLevel.Advanced, true,
            () => ReadInt(Lm, MemoryManagementKey, "SvcHostSplitThresholdInKB") == (int)Math.Min(GetInstalledRamKb(), int.MaxValue),
            () =>
            {
                var ramKb = (int)Math.Min(GetInstalledRamKb(), int.MaxValue);
                SetDword(Lm, MemoryManagementKey, "SvcHostSplitThresholdInKB", ramKb);
                return TweakResult.Ok($"SvcHostSplitThresholdInKB = {ramKb:N0} KB. Перезагрузите ПК.");
            },
            () =>
            {
                DeleteValue(Lm, MemoryManagementKey, "SvcHostSplitThresholdInKB");
                return TweakResult.Ok("Порог svchost возвращён к значению Windows. Перезагрузите ПК.");
            });

        Make("ntfs-last-access", TweakCategory.Memory, "NTFS Last Access Update: OFF",
            "Отключает обновление времени последнего доступа к файлам (NtfsDisableLastAccessUpdate = 1). Меньше лишних записей на SSD/NVMe.",
            RiskLevel.Safe, false,
            () => ReadInt(Lm, FileSystemKey, "NtfsDisableLastAccessUpdate") == 1,
            () =>
            {
                SetDword(Lm, FileSystemKey, "NtfsDisableLastAccessUpdate", 1);
                return TweakResult.Ok("Last Access Update отключён.");
            },
            () =>
            {
                SetDword(Lm, FileSystemKey, "NtfsDisableLastAccessUpdate", unchecked((int)0x80000003));
                return TweakResult.Ok("Last Access Update возвращён к значению Windows по умолчанию.");
            });

        Make("search-indexing", TweakCategory.Memory, "Windows Search: OFF",
            "Останавливает и отключает службу WSearch. Индексация перестаёт нагружать диск и CPU в фоне; поиск в меню «Пуск» станет медленнее.",
            RiskLevel.Advanced, false,
            () => IsServiceMissingOrDisabled("WSearch"),
            () => SetService("WSearch", true, 2, "Windows Search"),
            () => SetService("WSearch", false, 2, "Windows Search"));

        Make("sysmain", TweakCategory.Memory, "SysMain / Superfetch: OFF",
            "Отключает службу SysMain. Убирает фоновое упреждающее чтение и нагрузку на диск, но на HDD и при холодном старте приложений может стать медленнее.",
            RiskLevel.Safe, false,
            () => IsServiceMissingOrDisabled("SysMain"),
            () => SetService("SysMain", true, 2, "SysMain"),
            () => SetService("SysMain", false, 2, "SysMain"));

        Make("telemetry-services", TweakCategory.Memory, "Telemetry: DiagTrack + WAP Push",
            "Останавливает и отключает службы DiagTrack и dmwappushservice. Прекращает сбор и отправку диагностических данных.",
            RiskLevel.Safe, false,
            () => IsServiceMissingOrDisabled("DiagTrack") && IsServiceMissingOrDisabled("dmwappushservice"),
            () => SetServiceGroup(new[] { ("DiagTrack", 2), ("dmwappushservice", 3) }, true, "Телеметрия"),
            () => SetServiceGroup(new[] { ("DiagTrack", 2), ("dmwappushservice", 3) }, false, "Телеметрия"));

        Make("telemetry-policy", TweakCategory.Memory, "AllowTelemetry: 0",
            "Политика запрещает Windows отправлять диагностические данные (кроме Security). Дополняет отключение служб.",
            RiskLevel.Safe, false,
            () => ReadInt(Lm, TelemetryPolicyKey, "AllowTelemetry") == 0,
            () =>
            {
                SetDword(Lm, TelemetryPolicyKey, "AllowTelemetry", 0);
                return TweakResult.Ok("AllowTelemetry = 0.");
            },
            () =>
            {
                DeleteValue(Lm, TelemetryPolicyKey, "AllowTelemetry");
                return TweakResult.Ok("Политика телеметрии удалена.");
            });

        Make("xbox-services", TweakCategory.Memory, "Xbox services: OFF",
            "Отключает 4 фоновые службы Xbox. Освобождает память и убирает фоновые прерывания. Не используйте, если играете в Game Pass / Xbox-игры с онлайн-сервисами.",
            RiskLevel.Advanced, false,
            () => new[] { "XblAuthManager", "XblGameSave", "XboxNetApiSvc", "XboxGipSvc" }
                .All(IsServiceMissingOrDisabled),
            () => SetServiceGroup(new[]
            {
                ("XblAuthManager", 3), ("XblGameSave", 3), ("XboxNetApiSvc", 3), ("XboxGipSvc", 3)
            }, true, "Xbox services"),
            () => SetServiceGroup(new[]
            {
                ("XblAuthManager", 3), ("XblGameSave", 3), ("XboxNetApiSvc", 3), ("XboxGipSvc", 3)
            }, false, "Xbox services"));

        Make("maps-broker", TweakCategory.Memory, "MapsBroker: OFF",
            "Отключает фоновую службу карт. Безопасно, если вы не пользуетесь приложением «Карты».",
            RiskLevel.Safe, false,
            () => IsServiceMissingOrDisabled("MapsBroker"),
            () => SetService("MapsBroker", true, 2, "MapsBroker"),
            () => SetService("MapsBroker", false, 2, "MapsBroker"));

        Make("print-spooler", TweakCategory.Memory, "Print Spooler: OFF",
            "Отключает диспетчер печати. Освобождает память и убирает потенциальную поверхность атаки. Не отключайте, если печатаете или используете «Печать в PDF».",
            RiskLevel.Advanced, false,
            () => IsServiceMissingOrDisabled("PrintSpooler"),
            () => SetService("PrintSpooler", true, 2, "Print Spooler"),
            () => SetService("PrintSpooler", false, 2, "Print Spooler"));

        Make("background-apps", TweakCategory.Memory, "UWP Background Apps: OFF",
            "Запрещает магазинным приложениям работать в фоне (GlobalUserDisabled + LetAppsRunInBackground = 2). Убирает фоновые пробуждения CPU и сетевой трафик.",
            RiskLevel.Safe, false,
            () => ReadInt(Cu, BackgroundAppsKey, "GlobalUserDisabled") == 1
                  && ReadInt(Lm, AppPrivacyPolicyKey, "LetAppsRunInBackground") == 2,
            () =>
            {
                SetDword(Cu, BackgroundAppsKey, "GlobalUserDisabled", 1);
                SetDword(Lm, AppPrivacyPolicyKey, "LetAppsRunInBackground", 2);
                return TweakResult.Ok("Фоновые UWP-приложения отключены.");
            },
            () =>
            {
                DeleteValue(Cu, BackgroundAppsKey, "GlobalUserDisabled");
                DeleteValue(Lm, AppPrivacyPolicyKey, "LetAppsRunInBackground");
                return TweakResult.Ok("Фоновые UWP-приложения снова разрешены.");
            });

        // ---------------- Network & hitreg ----------------
        Make("nagle", TweakCategory.Network, "Nagling: OFF (TcpAckFrequency / TCPNoDelay)",
            "Отключает алгоритм Нейгла и задержку ACK на всех активных интерфейсах: TcpAckFrequency = 1, TCPNoDelay = 1, TcpDelAckTicks = 0. Снижает сетевую задержку; может слегка увеличить трафик.",
            RiskLevel.Safe, false,
            IsNagleDisabled,
            () =>
            {
                var count = SetNagle(false);
                return count > 0
                    ? TweakResult.Ok($"Nagle отключён на {count} интерфейс(ах). Переподключите сеть.")
                    : TweakResult.Fail("Не найдено активных сетевых интерфейсов.");
            },
            () =>
            {
                var count = SetNagle(true);
                return TweakResult.Ok($"Nagle возвращён к значениям Windows на {count} интерфейс(ах).");
            });

        Make("tcp-global", TweakCategory.Network, "TCP global: ECN/timestamps off, RSS on",
            "netsh: ecncapability=disabled, timestamps=disabled, rss=enabled. Убирает лишние флаги из TCP-заголовков и оставляет масштабирование приёма включённым.",
            RiskLevel.Advanced, false,
            IsTcpGlobalOptimized,
            () => SetTcpGlobal(false),
            () => SetTcpGlobal(true));

        Make("network-throttling", TweakCategory.Network, "NetworkThrottlingIndex: OFF",
            "Убирает лимит мультимедийного сетевого троттлинга (0xFFFFFFFF). Пакеты игр и голосового чата не режутся планировщиком.",
            RiskLevel.Safe, false,
            () => ReadInt(Lm, MultimediaKey, "NetworkThrottlingIndex") == -1,
            () =>
            {
                SetDword(Lm, MultimediaKey, "NetworkThrottlingIndex", -1);
                return TweakResult.Ok("NetworkThrottlingIndex = 0xFFFFFFFF.");
            },
            () =>
            {
                SetDword(Lm, MultimediaKey, "NetworkThrottlingIndex", 10);
                return TweakResult.Ok("NetworkThrottlingIndex возвращён к 10.");
            });

        Make("nic-power", TweakCategory.Network, "NIC: EEE, Green Ethernet, Interrupt Moderation OFF",
            "Через PowerShell отключает энергосбережение сетевой карты, Energy Efficient Ethernet, Green Ethernet и Interrupt Moderation. Минимальная задержка пакетов; на Wi-Fi/слабых адаптерах возможна нестабильность.",
            RiskLevel.Advanced, false,
            IsNicPowerOptimized,
            () => SetNicPower(false),
            () => SetNicPower(true));

        return list;
    }

    // ---------------------------------------------------------------------
    //  Registry helpers (64-bit view, journaled)
    // ---------------------------------------------------------------------

    public static void SetDword(RegistryHive hive, string subKey, string name, int value)
        => SetValue(hive, subKey, name, value, Dw);

    public static void SetString(RegistryHive hive, string subKey, string name, string value)
        => SetValue(hive, subKey, name, value, RegistryValueKind.String);

    public static void SetBinary(RegistryHive hive, string subKey, string name, byte[] value)
        => SetValue(hive, subKey, name, value, RegistryValueKind.Binary);

    public static void SetValue(RegistryHive hive, string subKey, string name, object value, RegistryValueKind kind)
    {
        BackupManager.EnsureKeyBackup(hive, subKey);
        BackupManager.RecordRegistryValue(hive, subKey, name);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(subKey, true)
                        ?? throw new InvalidOperationException($"Не удалось открыть ключ {subKey}");
        key.SetValue(name, value, kind);
    }

    public static void DeleteValue(RegistryHive hive, string subKey, string name)
    {
        BackupManager.EnsureKeyBackup(hive, subKey);
        BackupManager.RecordRegistryValue(hive, subKey, name);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(subKey, true);
        key?.DeleteValue(name, false);
    }

    public static int? ReadInt(RegistryHive hive, string subKey, string name)
    {
        var value = ReadValue(hive, subKey, name);
        return value switch
        {
            int i => i,
            long l => unchecked((int)l),
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            byte[] bytes when bytes.Length >= 4 => BitConverter.ToInt32(bytes, 0),
            _ => null
        };
    }

    public static long? ReadLong(RegistryHive hive, string subKey, string name)
    {
        var value = ReadValue(hive, subKey, name);
        return value switch
        {
            long l => l,
            int i => i,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    public static string? ReadString(RegistryHive hive, string subKey, string name)
        => ReadValue(hive, subKey, name) as string;

    public static byte[]? ReadBinary(RegistryHive hive, string subKey, string name)
        => ReadValue(hive, subKey, name) as byte[];

    public static object? ReadValue(RegistryHive hive, string subKey, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey, false);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch
        {
            return null;
        }
    }

    private static bool ValueExists(RegistryHive hive, string subKey, string name)
        => ReadValue(hive, subKey, name) is not null;

    // ---------------------------------------------------------------------
    //  BCDEDIT
    // ---------------------------------------------------------------------

    public static string? BcdGet(string name)
    {
        var result = RunProcess(BcdEdit, "/enum", "{current}");
        if (!result.Ok)
        {
            return null;
        }

        foreach (var rawLine in result.Output.Split('\n'))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOfAny(new[] { ' ', '\t' });
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator];
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line[(separator + 1)..].Trim();
        }

        return null;
    }

    internal static bool BcdWriteRaw(string name, string? value)
    {
        if (value is null)
        {
            var result = RunProcess(BcdEdit, "/deletevalue", name);
            return result.Ok || BcdGet(name) is null;
        }

        return RunProcess(BcdEdit, "/set", name, value).Ok;
    }

    private static TweakResult SetBcd(string name, string value, string successMessage)
    {
        BackupManager.RecordBcdValue(name, BcdGet(name));
        return BcdWriteRaw(name, value)
            ? TweakResult.Ok(successMessage)
            : TweakResult.Fail($"bcdedit не смог установить {name} = {value}.");
    }

    private static TweakResult ClearBcd(string name, string successMessage)
    {
        var previous = BcdGet(name);
        if (previous is null)
        {
            return TweakResult.Ok($"{name} уже отсутствует. {successMessage}");
        }

        BackupManager.RecordBcdValue(name, previous);
        return BcdWriteRaw(name, null)
            ? TweakResult.Ok(successMessage)
            : TweakResult.Fail($"bcdedit не смог удалить {name}.");
    }

    private static bool IsTruthy(string value)
        => value.Equals("yes", StringComparison.OrdinalIgnoreCase)
           || value.Equals("true", StringComparison.OrdinalIgnoreCase)
           || value.Equals("on", StringComparison.OrdinalIgnoreCase)
           || value == "1";

    // ---------------------------------------------------------------------
    //  Services
    // ---------------------------------------------------------------------

    private static string ServiceKey(string name) => $@"SYSTEM\CurrentControlSet\Services\{name}";

    private static bool ServiceExists(string name) => ReadInt(Lm, ServiceKey(name), "Start") is not null;

    private static bool IsServiceMissingOrDisabled(string name)
    {
        var start = ReadInt(Lm, ServiceKey(name), "Start");
        return start is null || start == 4;
    }

    internal static void SetServiceStartRaw(string name, int startType)
    {
        var start = startType switch
        {
            2 => "auto",
            3 => "demand",
            4 => "disabled",
            _ => "demand"
        };
        RunProcess(ScExe, "config", name, "start=", start);
    }

    internal static void StartServiceRaw(string name) => RunProcess(ScExe, "start", name);

    private static void StopServiceRaw(string name) => RunProcess(ScExe, "stop", name);

    private static TweakResult SetService(string name, bool disable, int restoreStart, string displayName)
    {
        if (!ServiceExists(name))
        {
            return TweakResult.Ok($"{displayName}: служба не установлена — пропущено.");
        }

        if (disable)
        {
            BackupManager.RecordService(name);
            StopServiceRaw(name);
            SetServiceStartRaw(name, 4);
            var check = ReadInt(Lm, ServiceKey(name), "Start");
            return check == 4
                ? TweakResult.Ok($"{displayName}: служба отключена.")
                : TweakResult.Fail($"{displayName}: не удалось изменить тип запуска.");
        }

        SetServiceStartRaw(name, restoreStart);
        if (restoreStart != 4)
        {
            StartServiceRaw(name);
        }

        var verify = ReadInt(Lm, ServiceKey(name), "Start");
        return verify == restoreStart
            ? TweakResult.Ok($"{displayName}: служба снова включена.")
            : TweakResult.Fail($"{displayName}: не удалось восстановить тип запуска.");
    }

    private static TweakResult SetServiceGroup(
        IEnumerable<(string Name, int RestoreStart)> services,
        bool disable,
        string groupName)
    {
        var messages = new List<string>();
        var allOk = true;
        foreach (var (name, restoreStart) in services)
        {
            var result = SetService(name, disable, restoreStart, name);
            allOk &= result.Success;
            messages.Add(result.Message);
        }

        return allOk
            ? TweakResult.Ok($"{groupName}: готово.")
            : TweakResult.Fail(string.Join(" ", messages.Where(m => !m.Contains("готово", StringComparison.OrdinalIgnoreCase))));
    }

    private static TweakResult SetNvidiaTelemetry(bool enable)
    {
        var serviceResult = SetService("NvTelemetryContainer", !enable, 2, "NVIDIA Telemetry");
        var action = enable ? "Enable" : "Disable";
        var script = $@"
$ErrorActionPreference = 'SilentlyContinue'
$tasks = @(Get-ScheduledTask -TaskPath '\NVIDIA Corporation\*' | Where-Object {{ $_.TaskName -match 'NvTm|NvProfileUpdater|NvTelemetry' }})
foreach ($t in $tasks) {{ {action}-ScheduledTask -TaskName $t.TaskName -TaskPath $t.TaskPath | Out-Null }}
Write-Output ('GO_TASKS_' + $tasks.Count)
";
        var result = RunPowerShell(script, 60000);
        if (!serviceResult.Success)
        {
            return serviceResult;
        }

        return result.Ok
            ? TweakResult.Ok(enable ? "NVIDIA Telemetry снова включена." : "NVIDIA Telemetry отключена (служба + задачи).")
            : TweakResult.Fail($"Служба обновлена, но задачи NVIDIA: {result.Error}");
    }

    // ---------------------------------------------------------------------
    //  GPU class helpers (NVIDIA / AMD)
    // ---------------------------------------------------------------------

    private static bool HasNvidiaGpu() => HasGpuVendor("NVIDIA");

    private static bool HasAmdGpu() => HasGpuVendor("AMD") || HasGpuVendor("Radeon") || HasGpuVendor("ATI");

    private static bool HasGpuVendor(string vendor)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(Lm, RegistryView.Registry64);
            using var classKey = baseKey.OpenSubKey(DisplayClassKey, false);
            if (classKey is null)
            {
                return false;
            }

            foreach (var subKeyName in classKey.GetSubKeyNames())
            {
                using var gpu = classKey.OpenSubKey(subKeyName, false);
                var description = gpu?.GetValue("DriverDesc") as string ?? "";
                if (description.Contains(vendor, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // ignored
        }

        return false;
    }

    private static List<string> GetGpuClassSubKeys(bool nvidia)
    {
        var result = new List<string>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(Lm, RegistryView.Registry64);
            using var classKey = baseKey.OpenSubKey(DisplayClassKey, false);
            if (classKey is null)
            {
                return result;
            }

            foreach (var subKeyName in classKey.GetSubKeyNames())
            {
                if (subKeyName.Length != 4 || !subKeyName.All(char.IsDigit))
                {
                    continue;
                }

                using var gpu = classKey.OpenSubKey(subKeyName, false);
                var description = gpu?.GetValue("DriverDesc") as string ?? "";
                var matches = nvidia
                    ? description.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                    : description.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                      || description.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                      || description.Contains("ATI", StringComparison.OrdinalIgnoreCase);
                if (matches)
                {
                    result.Add($"{DisplayClassKey}\\{subKeyName}");
                }
            }
        }
        catch
        {
            // ignored
        }

        return result;
    }

    private static bool AllGpuValuesEqual(bool nvidia, string valueName, int expected)
    {
        var keys = GetGpuClassSubKeys(nvidia);
        return keys.Count > 0 && keys.All(key => ReadInt(Lm, key, valueName) == expected);
    }

    private static TweakResult SetAllGpuValues(bool nvidia, string valueName, int value, string message)
    {
        var keys = GetGpuClassSubKeys(nvidia);
        if (keys.Count == 0)
        {
            return TweakResult.Fail("Совместимая видеокарта не найдена.");
        }

        foreach (var key in keys)
        {
            SetDword(Lm, key, valueName, value);
        }

        return TweakResult.Ok(message);
    }

    private static TweakResult DeleteAllGpuValues(bool nvidia, string valueName, string message)
    {
        var keys = GetGpuClassSubKeys(nvidia);
        foreach (var key in keys)
        {
            DeleteValue(Lm, key, valueName);
        }

        return TweakResult.Ok(message);
    }

    // ---------------------------------------------------------------------
    //  MSI mode + interrupt affinity
    // ---------------------------------------------------------------------

    private static string DeviceBaseKey(string deviceKey) => $@"{PciEnumKey}\{deviceKey}";

    private static string MsiKey(string deviceKey)
        => DeviceBaseKey(deviceKey) + @"\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";

    private static string AffinityKey(string deviceKey)
        => DeviceBaseKey(deviceKey) + @"\Device Parameters\Interrupt Management\Affinity Policy";

    public static (bool MsiEnabled, int Priority) ReadMsiState(string deviceKey)
    {
        var msi = ReadInt(Lm, MsiKey(deviceKey), "MSISupported") == 1;
        var priority = ReadInt(Lm, AffinityKey(deviceKey), "DevicePriority") ?? 0;
        return (msi, priority);
    }

    public static TweakResult SetMsiMode(string deviceKey, bool enable)
    {
        try
        {
            if (enable)
            {
                SetDword(Lm, MsiKey(deviceKey), "MSISupported", 1);
                SetDword(Lm, AffinityKey(deviceKey), "DevicePriority", 3);
                return TweakResult.Ok("MSI включён, DevicePriority = High. Перезагрузите ПК.");
            }

            SetDword(Lm, MsiKey(deviceKey), "MSISupported", 0);
            DeleteValue(Lm, AffinityKey(deviceKey), "DevicePriority");
            return TweakResult.Ok("MSI выключен. Перезагрузите ПК.");
        }
        catch (UnauthorizedAccessException)
        {
            return TweakResult.Fail(
                "Windows не разрешил запись в Enum\\PCI. Запустите приложение от администратора " +
                "или используйте официальную утилиту производителя.");
        }
    }

    public static TweakResult SetInterruptAffinity(string deviceKey, int core)
    {
        try
        {
            if (core < 0)
            {
                DeleteValue(Lm, AffinityKey(deviceKey), "AssignmentSetOverride");
                DeleteValue(Lm, AffinityKey(deviceKey), "DevicePolicy");
                DeleteValue(Lm, AffinityKey(deviceKey), "DevicePriority");
                return TweakResult.Ok("Прерывания снова распределяет Windows. Перезагрузите ПК.");
            }

            if (core > 63)
            {
                return TweakResult.Fail("Поддерживаются ядра 0–63.");
            }

            var mask = BitConverter.GetBytes(1UL << core);
            SetDword(Lm, AffinityKey(deviceKey), "DevicePolicy", 4); // IrqPolicySpecifiedProcessors
            SetDword(Lm, AffinityKey(deviceKey), "DevicePriority", 3);
            SetBinary(Lm, AffinityKey(deviceKey), "AssignmentSetOverride", mask);
            return TweakResult.Ok($"Прерывания привязаны к Core {core}. Перезагрузите ПК.");
        }
        catch (UnauthorizedAccessException)
        {
            return TweakResult.Fail("Нет доступа к ветке Enum\\PCI для изменения affinity.");
        }
    }

    public static List<PciDeviceInfo> ScanPciDevices()
    {
        var devices = new List<PciDeviceInfo>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(Lm, RegistryView.Registry64);
            using var pci = baseKey.OpenSubKey(PciEnumKey, false);
            if (pci is null)
            {
                return devices;
            }

            foreach (var deviceName in pci.GetSubKeyNames())
            {
                using var device = pci.OpenSubKey(deviceName, false);
                if (device is null)
                {
                    continue;
                }

                foreach (var instanceName in device.GetSubKeyNames())
                {
                    using var instance = device.OpenSubKey(instanceName, false);
                    using var parameters = instance?.OpenSubKey("Device Parameters", false);
                    if (instance is null || parameters is null)
                    {
                        continue;
                    }

                    var classGuid = (instance.GetValue("ClassGUID") as string ?? "").ToLowerInvariant();
                    string? kind = classGuid switch
                    {
                        "{4d36e968-e325-11ce-bfc1-08002be10318}" => "GPU",
                        "{4d36e972-e325-11ce-bfc1-08002be10318}" => "Network",
                        "{36fc9e60-c465-11cf-8056-444553540000}" => "USB",
                        _ => null
                    };
                    if (kind is null)
                    {
                        continue;
                    }

                    var description = CleanDeviceName(instance.GetValue("FriendlyName") as string)
                                      ?? CleanDeviceName(instance.GetValue("DeviceDesc") as string)
                                      ?? deviceName;

                    var deviceKey = $@"{deviceName}\{instanceName}";
                    var msi = ReadInt(Lm, MsiKey(deviceKey), "MSISupported") == 1;
                    var priority = ReadInt(Lm, AffinityKey(deviceKey), "DevicePriority") ?? 0;
                    devices.Add(new PciDeviceInfo(
                        deviceKey,
                        description,
                        kind,
                        msi,
                        priority,
                        ReadAssignedCore(deviceKey)));
                }
            }
        }
        catch
        {
            // ignored
        }

        return devices
            .GroupBy(d => d.DeviceKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(d => d.Kind switch { "GPU" => 0, "USB" => 1, _ => 2 })
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string? CleanDeviceName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var index = value.LastIndexOf(';');
        var result = index >= 0 ? value[(index + 1)..] : value;
        return result.Trim();
    }

    private static int ReadAssignedCore(string deviceKey)
    {
        var bytes = ReadBinary(Lm, AffinityKey(deviceKey), "AssignmentSetOverride");
        if (bytes is null || bytes.Length == 0)
        {
            return -1;
        }

        ulong mask = bytes.Length >= 8 ? BitConverter.ToUInt64(bytes, 0) : BitConverter.ToUInt32(bytes, 0);
        for (var i = 0; i < 64; i++)
        {
            if ((mask & (1UL << i)) != 0)
            {
                return i;
            }
        }

        return -1;
    }

    // ---------------------------------------------------------------------
    //  Power plans
    // ---------------------------------------------------------------------

    public static (Guid? Guid, string Name) GetActivePowerPlan()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\cimv2\power",
                "SELECT ElementName, InstanceID FROM Win32_PowerPlan WHERE IsActive = TRUE");
            using var results = searcher.Get();
            foreach (ManagementBaseObject item in results)
            {
                using var obj = item;
                var instanceId = obj["InstanceID"]?.ToString() ?? "";
                var name = obj["ElementName"]?.ToString() ?? "";
                var match = Regex.Match(
                    instanceId,
                    @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
                if (match.Success)
                {
                    return (Guid.Parse(match.Value), string.IsNullOrWhiteSpace(name) ? match.Value : name);
                }
            }
        }
        catch
        {
            // WMI power provider can be unavailable on stripped-down systems.
        }

        var result = RunProcess(PowerCfg, "/getactivescheme");
        if (!result.Ok)
        {
            return (null, "Неизвестно");
        }

        var guidMatch = Regex.Match(result.Output, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        if (!guidMatch.Success)
        {
            return (null, result.Output.Trim());
        }

        var nameMatch = Regex.Match(result.Output, @"\(([^)]+)\)");
        var fallbackName = nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : guidMatch.Value;
        return (Guid.Parse(guidMatch.Value), fallbackName);
    }

    public static string GetActivePowerPlanName() => GetActivePowerPlan().Name;

    public static bool IsUltimatePerformanceActive()
    {
        var (guid, name) = GetActivePowerPlan();
        return guid == Guid.Parse(UltimatePerformanceGuid)
               || name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Максимальная производительность", StringComparison.OrdinalIgnoreCase);
    }

    public static TweakResult EnableUltimatePerformance()
    {
        var previous = GetActivePowerPlan().Guid;
        BackupManager.RecordPowerScheme(previous);

        var duplicate = RunProcess(PowerCfg, "-duplicatescheme", UltimatePerformanceGuid);
        if (!duplicate.Ok)
        {
            return TweakResult.Fail($"powercfg не смог создать схему: {duplicate.Error.Trim()}");
        }

        var match = Regex.Match(
            duplicate.Output,
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        Guid? target = match.Success ? Guid.Parse(match.Value) : null;

        if (target is null)
        {
            var list = RunProcess(PowerCfg, "/list");
            var line = list.Output.Split('\n')
                .FirstOrDefault(l => l.Contains("Ultimate", StringComparison.OrdinalIgnoreCase)
                                     || l.Contains("Максимальная производительность", StringComparison.OrdinalIgnoreCase));
            var listMatch = line is null
                ? null
                : Regex.Match(line, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            if (listMatch is { Success: true })
            {
                target = Guid.Parse(listMatch.Value);
            }
        }

        if (target is null)
        {
            return TweakResult.Fail("Схема Ultimate Performance не найдена в выводе powercfg.");
        }

        var activate = RunProcess(PowerCfg, "/setactive", target.Value.ToString());
        if (!activate.Ok)
        {
            return TweakResult.Fail($"Не удалось активировать схему: {activate.Error.Trim()}");
        }

        UnhidePowerSetting(CoreParkingGuid);
        UnhidePowerSetting(IdleDisableGuid);
        return TweakResult.Ok("Ultimate Performance активирована. Рекомендуется перезагрузка.");
    }

    public static void SetActivePowerPlan(Guid guid)
        => RunProcess(PowerCfg, "/setactive", guid.ToString());

    private static void UnhidePowerSetting(string settingGuid)
        => RunProcess(PowerCfg, "-attributes", SubProcessor, settingGuid, "-ATTRIB_HIDE");

    private static (uint? Ac, uint? Dc) QueryPowerSetting(string settingGuid)
    {
        var result = RunProcess(PowerCfg, "/q", "SCHEME_CURRENT", SubProcessor, settingGuid);
        if (!result.Ok)
        {
            return (null, null);
        }

        uint? ac = null;
        uint? dc = null;
        var values = new List<uint>();

        foreach (var rawLine in result.Output.Split('\n'))
        {
            var line = rawLine.Trim();
            var index = line.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var hex = new string(line[(index + 2)..].TakeWhile(Uri.IsHexDigit).ToArray());
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number))
            {
                continue;
            }

            values.Add(number);
            var lower = line.ToLowerInvariant();
            var isAc = lower.Contains(" ac ") || lower.Contains("ac power") || lower.Contains("от сети") || lower.Contains("сети:");
            var isDc = lower.Contains(" dc ") || lower.Contains("dc power") || lower.Contains("батаре") || lower.Contains("батареи:");
            var isCurrent = lower.Contains("current") || lower.Contains("текущ");
            if (isCurrent && isAc)
            {
                ac ??= number;
            }
            else if (isCurrent && isDc)
            {
                dc ??= number;
            }
        }

        if (ac is null && values.Count >= 2)
        {
            ac = values[^2];
            dc ??= values[^1];
        }

        return (ac, dc);
    }

    private static TweakResult SetPowerSetting(string settingGuid, uint value, string message)
    {
        UnhidePowerSetting(settingGuid);
        var ac = RunProcess(PowerCfg, "/setacvalueindex", "SCHEME_CURRENT", SubProcessor, settingGuid, value.ToString());
        var dc = RunProcess(PowerCfg, "/setdcvalueindex", "SCHEME_CURRENT", SubProcessor, settingGuid, value.ToString());
        RunProcess(PowerCfg, "/setactive", "SCHEME_CURRENT");
        return ac.Ok && dc.Ok
            ? TweakResult.Ok(message)
            : TweakResult.Fail($"powercfg: {ac.Error} {dc.Error}".Trim());
    }

    // ---------------------------------------------------------------------
    //  Network
    // ---------------------------------------------------------------------

    private static List<string> GetActiveInterfaceKeys()
    {
        var result = new List<string>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(Lm, RegistryView.Registry64);
            using var interfaces = baseKey.OpenSubKey(TcpipInterfacesKey, false);
            if (interfaces is null)
            {
                return result;
            }

            foreach (var name in interfaces.GetSubKeyNames())
            {
                using var iface = interfaces.OpenSubKey(name, false);
                if (iface is null)
                {
                    continue;
                }

                var dhcp = iface.GetValue("DhcpIPAddress") as string;
                var staticIp = iface.GetValue("IPAddress");
                var active = !string.IsNullOrWhiteSpace(dhcp)
                             || (staticIp is string[] addresses && addresses.Any(a => !string.IsNullOrWhiteSpace(a)))
                             || (staticIp is string text && !string.IsNullOrWhiteSpace(text));
                if (active)
                {
                    result.Add(name);
                }
            }
        }
        catch
        {
            // ignored
        }

        return result;
    }

    private static bool IsNagleDisabled()
    {
        var keys = GetActiveInterfaceKeys();
        if (keys.Count == 0)
        {
            return false;
        }

        return keys.All(key =>
            ReadInt(Lm, $"{TcpipInterfacesKey}\\{key}", "TcpAckFrequency") == 1
            && ReadInt(Lm, $"{TcpipInterfacesKey}\\{key}", "TCPNoDelay") == 1
            && ReadInt(Lm, $"{TcpipInterfacesKey}\\{key}", "TcpDelAckTicks") == 0);
    }

    private static int SetNagle(bool revert)
    {
        var keys = GetActiveInterfaceKeys();
        foreach (var key in keys)
        {
            var path = $"{TcpipInterfacesKey}\\{key}";
            if (revert)
            {
                DeleteValue(Lm, path, "TcpAckFrequency");
                DeleteValue(Lm, path, "TCPNoDelay");
                DeleteValue(Lm, path, "TcpDelAckTicks");
            }
            else
            {
                SetDword(Lm, path, "TcpAckFrequency", 1);
                SetDword(Lm, path, "TCPNoDelay", 1);
                SetDword(Lm, path, "TcpDelAckTicks", 0);
            }
        }

        return keys.Count;
    }

    private static bool IsTcpGlobalOptimized()
    {
        var result = RunProcess(NetSh, "int", "tcp", "show", "global");
        if (!result.Ok)
        {
            return false;
        }

        var ecn = FindLineState(result.Output, "ECN");
        var timestamps = FindLineState(result.Output, "Timestamp", "Метк", "1323");
        var rss = FindLineState(result.Output, "RSS", "Масштабирование приема", "Масштабирование приёма");
        return ecn == false && timestamps == false && rss == true;
    }

    private static bool? FindLineState(string output, params string[] keywords)
    {
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            var lower = line.ToLowerInvariant();
            if (!keywords.Any(k => lower.Contains(k.ToLowerInvariant())))
            {
                continue;
            }

            if (lower.Contains("disabled") || lower.Contains("отключ"))
            {
                return false;
            }

            if (lower.Contains("enabled") || lower.Contains("включ"))
            {
                return true;
            }
        }

        return null;
    }

    private static TweakResult SetTcpGlobal(bool revert)
    {
        var result = revert
            ? RunProcess(NetSh, "int", "tcp", "set", "global",
                "ecncapability=default", "timestamps=default", "rss=default")
            : RunProcess(NetSh, "int", "tcp", "set", "global",
                "ecncapability=disabled", "timestamps=disabled", "rss=enabled");

        return result.Ok
            ? TweakResult.Ok(revert ? "TCP-параметры возвращены к значениям Windows." : "TCP global оптимизирован.")
            : TweakResult.Fail($"netsh: {(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error).Trim()}");
    }

    private static bool IsNicPowerOptimized()
    {
        var script = @"
$ErrorActionPreference = 'SilentlyContinue'
$adapters = @(Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' })
if ($adapters.Count -eq 0) { 'GO_NOADAPTER'; exit }
$optimized = $true
foreach ($a in $adapters) {
  $pm = Get-NetAdapterPowerManagement -Name $a.Name
  if ($pm -and ([string]$pm.AllowComputerToTurnOffDevice) -ne 'Disabled') { $optimized = $false }
  $props = @(Get-NetAdapterAdvancedProperty -Name $a.Name | Where-Object { $_.DisplayName -match 'Energy|Green|Moderation|Eco' })
  foreach ($p in $props) {
    if (([string]$p.DisplayValue) -notmatch '^(Disabled|Off|0)$') { $optimized = $false }
  }
}
if ($optimized) { 'GO_OPTIMIZED' } else { 'GO_DEFAULT' }
";
        var result = RunPowerShell(script, 60000);
        return result.Ok && result.Output.Contains("GO_OPTIMIZED", StringComparison.Ordinal);
    }

    private static TweakResult SetNicPower(bool revert)
    {
        var script = revert
            ? @"
$ErrorActionPreference = 'SilentlyContinue'
$adapters = @(Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' })
foreach ($a in $adapters) {
  Enable-NetAdapterPowerManagement -Name $a.Name -NoRestart -ErrorAction SilentlyContinue
  $props = @(Get-NetAdapterAdvancedProperty -Name $a.Name | Where-Object { $_.DisplayName -match 'Energy|Green|Moderation|Eco' })
  foreach ($p in $props) {
    Reset-NetAdapterAdvancedProperty -Name $a.Name -DisplayName $p.DisplayName -NoRestart -ErrorAction SilentlyContinue
  }
}
'GO_DONE'
"
            : @"
$ErrorActionPreference = 'SilentlyContinue'
$adapters = @(Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' })
foreach ($a in $adapters) {
  Disable-NetAdapterPowerManagement -Name $a.Name -NoRestart -ErrorAction SilentlyContinue
  $props = @(Get-NetAdapterAdvancedProperty -Name $a.Name | Where-Object { $_.DisplayName -match 'Energy|Green|Moderation|Eco' })
  foreach ($p in $props) {
    Set-NetAdapterAdvancedProperty -Name $a.Name -DisplayName $p.DisplayName -DisplayValue 'Disabled' -NoRestart -ErrorAction SilentlyContinue
    if (-not $?) {
      Set-NetAdapterAdvancedProperty -Name $a.Name -DisplayName $p.DisplayName -RegistryValue 0 -NoRestart -ErrorAction SilentlyContinue
    }
  }
}
'GO_DONE'
";

        var result = RunPowerShell(script, 120000);
        return result.Ok && result.Output.Contains("GO_DONE", StringComparison.Ordinal)
            ? TweakResult.Ok(revert ? "Энергосбережение NIC возвращено к значениям Windows." : "Энергосбережение NIC отключено (EEE / Green / Moderation / PM).")
            : TweakResult.Fail($"PowerShell: {result.Error.Trim()}");
    }

    public static TweakResult ResetNetworkToDefaults()
    {
        var tcp = SetTcpGlobal(true);
        var nic = SetNicPower(true);
        return tcp.Success && nic.Success
            ? TweakResult.Ok("Сеть и параметры адаптеров возвращены к значениям Windows.")
            : TweakResult.Fail($"{tcp.Message} {nic.Message}".Trim());
    }

    // ---------------------------------------------------------------------
    //  Native calls
    // ---------------------------------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalKilobytes);

    [DllImport("ntdll.dll")]
    private static extern int NtSetTimerResolution(uint desiredResolution, bool setResolution, out uint currentResolution);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    public static double GetCpuUsage()
    {
        lock (CpuGate)
        {
            if (_cpuCounter is null && !_cpuCounterUnavailable)
            {
                try
                {
                    _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                    _ = _cpuCounter.NextValue();
                }
                catch
                {
                    _cpuCounterUnavailable = true;
                }
            }

            if (_cpuCounter is not null)
            {
                try
                {
                    var value = _cpuCounter.NextValue();
                    return Math.Clamp(value, 0, 100);
                }
                catch
                {
                    _cpuCounter.Dispose();
                    _cpuCounter = null;
                    _cpuCounterUnavailable = true;
                }
            }

            return GetCpuUsageNative();
        }
    }

    private static double GetCpuUsageNative()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return 0;
        }

        var idleTicks = (long)idle.ToUInt64();
        var kernelTicks = (long)kernel.ToUInt64();
        var userTicks = (long)user.ToUInt64();

        if (_prevKernel == 0 && _prevUser == 0)
        {
            _prevIdle = idleTicks;
            _prevKernel = kernelTicks;
            _prevUser = userTicks;
            return 0;
        }

        var idleDelta = idleTicks - _prevIdle;
        var kernelDelta = kernelTicks - _prevKernel;
        var userDelta = userTicks - _prevUser;
        _prevIdle = idleTicks;
        _prevKernel = kernelTicks;
        _prevUser = userTicks;

        var total = kernelDelta + userDelta;
        if (total <= 0)
        {
            return 0;
        }

        return Math.Clamp((total - idleDelta) * 100.0 / total, 0, 100);
    }

    public static (ulong TotalBytes, ulong AvailableBytes, uint LoadPercent) GetMemoryStatus()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return (0, 0, 0);
        }

        return (status.TotalPhys, status.AvailPhys, status.MemoryLoad);
    }

    public static ulong GetInstalledRamKb()
    {
        return GetPhysicallyInstalledSystemMemory(out var kilobytes) ? kilobytes : 0;
    }

    public static void PurgeStandbyList(out string message)
    {
        try
        {
            EnablePrivilege("SeProfileSingleProcessPrivilege");
            const int systemMemoryListInformation = 80;
            const int memoryPurgeStandbyList = 4;
            var buffer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(buffer, memoryPurgeStandbyList);
                var status = NtSetSystemInformation(systemMemoryListInformation, buffer, sizeof(int));
                message = status == 0
                    ? "Standby List очищен. Кэш ОЗУ освобождён."
                    : $"NtSetSystemInformation вернул 0x{status:X8}. Нужны права администратора.";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            message = $"Ошибка очистки Standby List: {ex.Message}";
        }
    }

    private static void EnablePrivilege(string privilegeName)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out var token))
        {
            return;
        }

        try
        {
            if (!LookupPrivilegeValue(null, privilegeName, out var luid))
            {
                return;
            }

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = 0x00000002 // SE_PRIVILEGE_ENABLED
            };
            AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static bool IsHighTimerResolution()
    {
        return NtQueryTimerResolution(out _, out _, out var current) == 0 && current <= 5000;
    }

    private static TweakResult SetTimerResolution(uint resolution, bool enable, string message)
    {
        var status = NtSetTimerResolution(resolution, enable, out _);
        return status == 0
            ? TweakResult.Ok(message)
            : TweakResult.Fail($"NtSetTimerResolution вернул 0x{status:X8}.");
    }

    // ---------------------------------------------------------------------
    //  System information / WMI
    // ---------------------------------------------------------------------

    public static string GetCpuName()
    {
        var name = WmiScalar("SELECT Name FROM Win32_Processor", "Name");
        return string.IsNullOrWhiteSpace(name) ? "Неизвестный CPU" : name.Trim();
    }

    public static string GetGpuNames()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            using var results = searcher.Get();
            var names = new List<string>();
            foreach (ManagementBaseObject item in results)
            {
                using var obj = item;
                var name = obj["Name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }

            return names.Count == 0 ? "Неизвестный GPU" : string.Join(" / ", names);
        }
        catch
        {
            return "Неизвестный GPU";
        }
    }

    public static string GetOsName()
    {
        var caption = WmiScalar("SELECT Caption FROM Win32_OperatingSystem", "Caption");
        var version = WmiScalar("SELECT Version FROM Win32_OperatingSystem", "Version");
        return string.IsNullOrWhiteSpace(caption)
            ? Environment.OSVersion.VersionString
            : $"{caption.Trim()} {version}";
    }

    public static int GetPhysicalCoreCount()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT NumberOfCores FROM Win32_Processor");
            using var results = searcher.Get();
            var total = 0;
            foreach (ManagementBaseObject item in results)
            {
                using var obj = item;
                total += Convert.ToInt32(obj["NumberOfCores"] ?? 0, CultureInfo.InvariantCulture);
            }

            return total > 0 ? total : Math.Max(1, Environment.ProcessorCount / 2);
        }
        catch
        {
            return Math.Max(1, Environment.ProcessorCount / 2);
        }
    }

    public static List<string> GetActiveNetworkAdapters()
    {
        var list = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Description FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");
            using var results = searcher.Get();
            foreach (ManagementBaseObject item in results)
            {
                using var obj = item;
                var description = obj["Description"]?.ToString();
                if (!string.IsNullOrWhiteSpace(description))
                {
                    list.Add(description.Trim());
                }
            }
        }
        catch
        {
            // ignored
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? WmiScalar(string query, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            using var results = searcher.Get();
            foreach (ManagementBaseObject item in results)
            {
                using var obj = item;
                return obj[property]?.ToString();
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    // ---------------------------------------------------------------------
    //  Process / PowerShell plumbing
    // ---------------------------------------------------------------------

    private static (bool Ok, string Output, string Error) RunProcess(
        string fileName,
        params string[] arguments)
        => RunProcess(fileName, arguments, 30000);

    private static (bool Ok, string Output, string Error) RunProcess(
        string fileName,
        string[] arguments,
        int timeoutMs)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = ConsoleEncoding,
                StandardErrorEncoding = ConsoleEncoding
            };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = info };
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // ignored
                }

                return (false, "", "timeout");
            }

            return (process.ExitCode == 0,
                outputTask.GetAwaiter().GetResult(),
                errorTask.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }

    private static (bool Ok, string Output, string Error) RunPowerShell(string script, int timeoutMs)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return RunProcess(
            PowerShellExe,
            new[]
            {
                "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-EncodedCommand", encoded
            },
            timeoutMs);
    }

    // ---------------------------------------------------------------------
    //  Presets
    // ---------------------------------------------------------------------

    public static readonly string[] EsportsPreset =
    {
        "bcd-dynamictick", "bcd-tscsync", "bcd-hpet", "timer-resolution",
        "mmcss-responsiveness", "mmcss-games", "hags", "game-mode", "gamedvr", "mpo",
        "tdr", "nvidia-telemetry", "amd-ulps", "ultimate-performance", "core-parking",
        "disable-paging-executive", "svchost-split", "ntfs-last-access", "search-indexing",
        "sysmain", "telemetry-services", "telemetry-policy", "background-apps",
        "nagle", "tcp-global", "network-throttling", "nic-power"
    };

    public static readonly string[] BalancedPreset =
    {
        "mmcss-games", "hags", "game-mode", "gamedvr", "ntfs-last-access",
        "telemetry-policy", "background-apps", "nagle", "network-throttling"
    };

    public static TweakResult ApplyPrioritySeparation(int value)
    {
        SetDword(Lm, PriorityControlKey, "Win32PrioritySeparation", value);
        return TweakResult.Ok($"Win32PrioritySeparation = 0x{value:X2} ({value}).");
    }

    public static int GetPrioritySeparation() => ReadInt(Lm, PriorityControlKey, "Win32PrioritySeparation") ?? 2;

    public static bool IsHagsEnabled() => ReadInt(Lm, GraphicsDriversKey, "HwSchMode") == 2;

    public static bool IsGameModeEnabled()
        => ReadInt(Cu, GameBarKey, "AutoGameModeEnabled") == 1
           && ReadInt(Cu, GameBarKey, "AllowAutoGameMode") == 1;

    public static TweakResult CleanBootOverrides()
    {
        var messages = new List<string>();
        foreach (var name in new[] { "disabledynamictick", "tscsyncpolicy", "useplatformclock" })
        {
            if (BcdGet(name) is not null)
            {
                BackupManager.RecordBcdValue(name, BcdGet(name));
                messages.Add(BcdWriteRaw(name, null) ? $"{name}: удалено" : $"{name}: ошибка");
            }
        }

        return TweakResult.Ok(messages.Count == 0 ? "Загрузочные параметры уже стоковые." : string.Join(", ", messages));
    }

    /// <summary>Restore-stock helper: BCD overrides, Balanced plan, TCP stack and NIC defaults.</summary>
    public static TweakResult RestoreStockExtras()
    {
        var bcd = CleanBootOverrides();
        SetActivePowerPlan(Guid.Parse(BalancedGuid));
        var net = ResetNetworkToDefaults();
        return TweakResult.Ok($"{(bcd.Success ? "BCD очищен." : bcd.Message)} Питание: Balanced. {net.Message}");
    }
}
