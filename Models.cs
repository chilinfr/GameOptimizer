using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace GameOptimizer;

public enum TweakCategory
{
    Dashboard,
    Kernel,
    Gpu,
    Cpu,
    Memory,
    Network,
    Backup
}

public enum RiskLevel
{
    Safe,
    Advanced,
    Risky
}

public readonly record struct TweakResult(bool Success, string Message)
{
    public static TweakResult Ok(string message) => new(true, message);
    public static TweakResult Fail(string message) => new(false, message);
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _isRunning;

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Непредвиденная ошибка:\n\n{ex.Message}",
                "GameOptimizer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// One UI card = one reversible system tweak. State is always re-read from the OS.
/// </summary>
public sealed class TweakItem : ObservableObject
{
    private readonly Func<bool> _readState;
    private readonly Func<TweakResult> _apply;
    private readonly Func<TweakResult> _revert;
    private readonly Action<string, bool> _notify;
    private readonly Action _changed;
    private bool _isOn;
    private bool _isBusy;
    private string _statusText = "Проверка…";

    public TweakItem(
        string id,
        TweakCategory category,
        string title,
        string description,
        RiskLevel risk,
        bool requiresReboot,
        Func<bool> readState,
        Func<TweakResult> apply,
        Func<TweakResult> revert,
        Action<string, bool> notify,
        Action changed,
        string? warning = null)
    {
        Id = id;
        Category = category;
        Title = title;
        Description = description;
        Risk = risk;
        RequiresReboot = requiresReboot;
        _readState = readState;
        _apply = apply;
        _revert = revert;
        _notify = notify;
        _changed = changed;
        Warning = warning;
        CategoryName = CategoryNames.TryGetValue(category, out var name) ? name : category.ToString();
        ToggleCommand = new AsyncRelayCommand(_ => ToggleAsync(), _ => !IsBusy);
        Refresh();
    }

    public string Id { get; }
    public TweakCategory Category { get; }
    public string CategoryName { get; }
    public string Title { get; }
    public string Description { get; }
    public RiskLevel Risk { get; }
    public bool RequiresReboot { get; }
    public string? Warning { get; private set; }

    public string RiskLabel => Risk switch
    {
        RiskLevel.Safe => "SAFE",
        RiskLevel.Advanced => "ADVANCED",
        _ => "RISKY"
    };

    public bool IsOn
    {
        get => _isOn;
        private set
        {
            if (SetProperty(ref _isOn, value))
            {
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                if (ToggleCommand is AsyncRelayCommand command)
                {
                    command.RaiseCanExecuteChanged();
                }
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string StateText => IsOn ? "ВКЛЮЧЕНО" : "ВЫКЛЮЧЕНО";

    public ICommand ToggleCommand { get; }

    public bool QueryState()
    {
        try
        {
            return _readState();
        }
        catch
        {
            return false;
        }
    }

    public void SetState(bool value)
    {
        IsOn = value;
        StatusText = value ? "Включено" : "Выключено";
    }

    public void Refresh()
    {
        try
        {
            IsOn = _readState();
            StatusText = IsOn ? "Включено" : "Выключено";
        }
        catch (Exception ex)
        {
            IsOn = false;
            StatusText = "Ошибка чтения";
            Warning = ex.Message;
        }
    }

    /// <summary>Used by presets; caller is responsible for restore point and state refresh.</summary>
    public TweakResult ApplyForPreset() => _apply();

    /// <summary>Used by profile import; caller is responsible for state refresh.</summary>
    public TweakResult RevertForPreset() => _revert();

    private async Task ToggleAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var turnOn = !IsOn;

        if (turnOn && Risk == RiskLevel.Risky)
        {
            var answer = MessageBox.Show(
                Application.Current?.MainWindow,
                $"ВНИМАНИЕ!\n\n{Title}\n\n{Warning ?? Description}\n\nПродолжить?",
                "Потенциально небезопасный твик",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        IsBusy = true;
        try
        {
            StatusText = "Точка восстановления…";
            var restorePoint = await BackupManager.EnsureRestorePointAsync();
            if (!restorePoint.Success && !BackupManager.RestorePointWarningAcknowledged)
            {
                var answer = MessageBox.Show(
                    Application.Current?.MainWindow,
                    $"Не удалось создать точку восстановления:\n\n{restorePoint.Message}\n\n" +
                    "Изменения будут журналироваться, и их можно откатить кнопкой «Полный откат».\n\n" +
                    "Продолжить без точки восстановления?",
                    "Безопасность системы",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    StatusText = "Отменено";
                    return;
                }

                BackupManager.RestorePointWarningAcknowledged = true;
            }

            StatusText = turnOn ? "Применение…" : "Откат…";
            var result = await Task.Run(() => turnOn ? _apply() : _revert());
            Refresh();
            _notify(result.Message, result.Success);
            _changed();
        }
        catch (Exception ex)
        {
            _notify($"Ошибка: {ex.Message}", false);
        }
        finally
        {
            IsBusy = false;
            Refresh();
            if (!IsOn)
            {
                StatusText = "Выключено";
            }
        }
    }

    private static readonly Dictionary<TweakCategory, string> CategoryNames = new()
    {
        [TweakCategory.Dashboard] = "Dashboard",
        [TweakCategory.Kernel] = "Deep Kernel & BCD",
        [TweakCategory.Gpu] = "GPU & Interrupts",
        [TweakCategory.Cpu] = "CPU & Power",
        [TweakCategory.Memory] = "Memory & Storage",
        [TweakCategory.Network] = "Network & Hitreg",
        [TweakCategory.Backup] = "Backups & Config"
    };
}

public sealed class NavigationItem
{
    public NavigationItem(string title, string iconData, int index)
    {
        Title = title;
        Icon = Geometry.Parse(iconData);
        Index = index;
    }

    public string Title { get; }
    public Geometry Icon { get; }
    public int Index { get; }
}

public sealed class PriorityOption
{
    public PriorityOption(int value, string label, string hint)
    {
        Value = value;
        Label = label;
        Hint = hint;
    }

    public int Value { get; }
    public string Label { get; }
    public string Hint { get; }
}

public sealed class CoreOption
{
    public CoreOption(int index)
    {
        Index = index;
        Label = index < 0 ? "Auto (Windows)" : $"Core {index}";
    }

    public int Index { get; }
    public string Label { get; }
}

public sealed record PciDeviceInfo(
    string DeviceKey,
    string Name,
    string Kind,
    bool MsiEnabled,
    int DevicePriority,
    int AssignedCore);

public sealed class PciDeviceItem : ObservableObject
{
    private readonly PciDeviceInfo _info;
    private readonly Action<string, bool> _notify;
    private bool _isMsiEnabled;
    private bool _isBusy;
    private int _priority;

    public PciDeviceItem(PciDeviceInfo info, Action<string, bool> notify)
    {
        _info = info;
        _notify = notify;
        _isMsiEnabled = info.MsiEnabled;
        _priority = info.DevicePriority;
        ToggleCommand = new AsyncRelayCommand(_ => ToggleAsync(), _ => !IsBusy);
    }

    public string DeviceKey => _info.DeviceKey;
    public string Name => _info.Name;
    public string KindLabel => _info.Kind;
    public string StatusText => IsMsiEnabled
        ? $"MSI ON · priority {(Priority == 3 ? "High" : Priority == 2 ? "Normal" : "Low")}"
        : "MSI OFF · legacy line interrupts";

    public bool IsMsiEnabled
    {
        get => _isMsiEnabled;
        private set
        {
            if (SetProperty(ref _isMsiEnabled, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public int Priority
    {
        get => _priority;
        private set
        {
            if (SetProperty(ref _priority, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value) && ToggleCommand is AsyncRelayCommand command)
            {
                command.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand ToggleCommand { get; }

    private async Task ToggleAsync()
    {
        IsBusy = true;
        try
        {
            var enable = !IsMsiEnabled;
            await BackupManager.EnsureRestorePointAsync();
            var result = await Task.Run(() => TweaksEngine.SetMsiMode(DeviceKey, enable));
            _notify(result.Message, result.Success);
            Refresh();
        }
        catch (Exception ex)
        {
            _notify($"Ошибка MSI: {ex.Message}", false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Refresh()
    {
        try
        {
            var state = TweaksEngine.ReadMsiState(DeviceKey);
            IsMsiEnabled = state.MsiEnabled;
            Priority = state.Priority;
        }
        catch
        {
            IsMsiEnabled = false;
        }
    }
}

public sealed class AffinityDeviceItem : ObservableObject
{
    private readonly PciDeviceInfo _info;
    private readonly Action<string, bool> _notify;
    private CoreOption _selectedCore;
    private bool _isBusy;
    private string _currentText;
    private string _assignedText;

    public AffinityDeviceItem(PciDeviceInfo info, IEnumerable<CoreOption> cores, Action<string, bool> notify)
    {
        _info = info;
        _notify = notify;
        CoreOptions = new ObservableCollection<CoreOption>(cores);
        _selectedCore = CoreOptions.FirstOrDefault(c => c.Index == info.AssignedCore) ?? CoreOptions.First();
        _currentText = info.AssignedCore >= 0 ? $"Core {info.AssignedCore}" : "Auto";
        _assignedText = _currentText;
        ApplyCommand = new AsyncRelayCommand(_ => ApplyAsync(), _ => !IsBusy);
    }

    public string DeviceKey => _info.DeviceKey;
    public string Name => _info.Name;
    public string KindLabel => _info.Kind;
    public ObservableCollection<CoreOption> CoreOptions { get; }
    public ICommand ApplyCommand { get; }

    public CoreOption SelectedCore
    {
        get => _selectedCore;
        set => SetProperty(ref _selectedCore, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value) && ApplyCommand is AsyncRelayCommand command)
            {
                command.RaiseCanExecuteChanged();
            }
        }
    }

    public string CurrentText
    {
        get => _currentText;
        private set => SetProperty(ref _currentText, value);
    }

    public string AssignedText
    {
        get => _assignedText;
        private set => SetProperty(ref _assignedText, value);
    }

    private async Task ApplyAsync()
    {
        IsBusy = true;
        try
        {
            var core = SelectedCore.Index;
            await BackupManager.EnsureRestorePointAsync();
            var result = await Task.Run(() => TweaksEngine.SetInterruptAffinity(DeviceKey, core));
            _notify(result.Message, result.Success);
            CurrentText = core >= 0 ? $"Core {core}" : "Auto";
            AssignedText = CurrentText;
        }
        catch (Exception ex)
        {
            _notify($"Ошибка affinity: {ex.Message}", false);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed class BackupEntry
{
    public string Name { get; init; } = "";
    public string CreatedText { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string FullPath { get; init; } = "";
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool flag && flag ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility visibility && visibility != Visibility.Visible;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool flag ? !flag : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool flag && !flag;
}

public sealed class RiskToForegroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is RiskLevel risk
            ? risk switch
            {
                RiskLevel.Safe => new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)),
                RiskLevel.Advanced => new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)),
                _ => new SolidColorBrush(Color.FromRgb(0xFB, 0x71, 0x85))
            }
            : Brushes.White;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class RiskToBackgroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is RiskLevel risk
            ? risk switch
            {
                RiskLevel.Safe => new SolidColorBrush(Color.FromArgb(0x24, 0x34, 0xD3, 0x99)),
                RiskLevel.Advanced => new SolidColorBrush(Color.FromArgb(0x24, 0xFB, 0xBF, 0x24)),
                _ => new SolidColorBrush(Color.FromArgb(0x24, 0xFB, 0x71, 0x85))
            }
            : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class BoolToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true
            ? new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99))
            : new SolidColorBrush(Color.FromRgb(0xFB, 0x71, 0x85));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
