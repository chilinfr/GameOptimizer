using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace GameOptimizer;

public readonly record struct BackupResult(bool Success, string Message);

public sealed class ChangeRecord
{
    public string Time { get; set; } = DateTime.Now.ToString("O");
    public string Type { get; set; } = "";
    public string Hive { get; set; } = "";
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Existed { get; set; }
    public string Kind { get; set; } = "String";
    public string? Data { get; set; }
    public string? ServiceName { get; set; }
    public int? PreviousStart { get; set; }
    public bool WasRunning { get; set; }
    public string? BcdName { get; set; }
    public string? BcdPrevious { get; set; }
    public string? PreviousScheme { get; set; }
}

public sealed class ProfileItem
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; }
}

public sealed class OptimizerProfile
{
    public string App { get; set; } = "GameOptimizer";
    public string Version { get; set; } = "1.0";
    public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public List<ProfileItem> Items { get; set; } = new();
}

/// <summary>
/// Creates restore points, exports registry branches, keeps a precise rollback journal
/// and handles profile import/export. Everything is stored in a "Backups" folder next to the exe
/// (or LocalAppData if that folder is not writable).
/// </summary>
public static class BackupManager
{
    private static readonly object Gate = new();
    private static readonly List<ChangeRecord> Journal = new();
    private static readonly HashSet<string> ExportedKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static Task<BackupResult>? _restorePointTask;
    private static bool _restorePointDone;
    private static bool _journalLoaded;
    private static string? _baseDirectory;

    public static bool RestorePointWarningAcknowledged { get; set; }
    public static bool RestorePointCreated => _restorePointDone;
    public static int JournalCount
    {
        get
        {
            EnsureJournalLoaded();
            lock (Gate)
            {
                return Journal.Count;
            }
        }
    }

    public static string BaseDirectory
    {
        get
        {
            if (_baseDirectory is not null)
            {
                return _baseDirectory;
            }

            var preferred = Path.Combine(ExecutableDirectory, "Backups");
            try
            {
                Directory.CreateDirectory(preferred);
                _baseDirectory = preferred;
            }
            catch
            {
                var fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GameOptimizer",
                    "Backups");
                Directory.CreateDirectory(fallback);
                _baseDirectory = fallback;
            }

            return _baseDirectory;
        }
    }

    public static string RegistryBackupDirectory => Path.Combine(BaseDirectory, "Registry");
    public static string JournalPath => Path.Combine(BaseDirectory, "journal.json");

    /// <summary>
    /// For single-file apps AppContext.BaseDirectory points to the extraction folder,
    /// while Backups must live next to the real .exe.
    /// </summary>
    private static string ExecutableDirectory
    {
        get
        {
            try
            {
                var processPath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(processPath))
                {
                    var directory = Path.GetDirectoryName(processPath);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        return directory;
                    }
                }
            }
            catch
            {
                // Fall back to AppContext below.
            }

            return AppContext.BaseDirectory;
        }
    }

    public static async Task<BackupResult> EnsureRestorePointAsync()
    {        Task<BackupResult> task;
        lock (Gate)
        {
            if (_restorePointDone)
            {
                return new BackupResult(true, "Точка восстановления уже создана.");
            }

            task = _restorePointTask ??= Task.Run(CreateRestorePoint);
        }

        var result = await task.ConfigureAwait(false);
        if (result.Success)
        {
            lock (Gate)
            {
                _restorePointDone = true;
            }
        }

        return result;
    }

    public static void ResetRestorePointAttempt()
    {
        lock (Gate)
        {
            _restorePointTask = null;
            _restorePointDone = false;
        }
    }

    private static BackupResult CreateRestorePoint()
    {        try
        {
            var scope = new ManagementScope(@"\\.\root\default");
            scope.Connect();
            using var systemRestore = new ManagementClass(scope, new ManagementPath("SystemRestore"), null);
            using var input = systemRestore.GetMethodParameters("CreateRestorePoint");
            input["Description"] = "GameOptimizer automatic safety point";
            input["RestorePointType"] = 0; // APPLICATION_INSTALL
            input["EventType"] = 100;      // BEGIN_SYSTEM_CHANGE
            using var output = systemRestore.InvokeMethod("CreateRestorePoint", input, null);
            var code = Convert.ToInt32(output?["ReturnValue"] ?? -1, CultureInfo.InvariantCulture);
            if (code == 0)
            {
                return new BackupResult(true, "Точка восстановления создана.");
            }

            return new BackupResult(false,
                $"WMI вернул код {code}. Проверьте, что «Защита системы» включена для диска C:. " +
                "Изменения всё равно будут записаны в журнал отката.");
        }
        catch (Exception ex)
        {
            return new BackupResult(false,
                $"{ex.Message}. Включите «Защиту системы» (System Protection) для системного диска.");
        }
    }

    public static void EnsureKeyBackup(RegistryHive hive, string subKey)
    {
        var id = $"{hive}\\{subKey}";
        lock (Gate)
        {
            if (!ExportedKeys.Add(id))
            {
                return;
            }
        }

        try
        {
            Directory.CreateDirectory(RegistryBackupDirectory);
            var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Sanitize(subKey)}.reg";
            var path = Path.Combine(RegistryBackupDirectory, fileName);
            var hiveName = hive switch
            {
                RegistryHive.LocalMachine => "HKLM",
                RegistryHive.CurrentUser => "HKCU",
                RegistryHive.ClassesRoot => "HKCR",
                RegistryHive.Users => "HKU",
                RegistryHive.CurrentConfig => "HKCC",
                _ => "HKLM"
            };

            var result = RunProcess(
                Path.Combine(Environment.SystemDirectory, "reg.exe"),
                new[] { "export", $"{hiveName}\\{subKey}", path, "/y" },
                30000);
            // A missing key is fine: there is simply nothing to export yet.
            _ = result;
        }
        catch
        {
            // Export is a convenience; the precise journal below is the real rollback source.
        }
    }

    public static void RecordRegistryValue(RegistryHive hive, string subKey, string name)
    {
        var record = new ChangeRecord
        {
            Type = "Registry",
            Hive = hive.ToString(),
            Key = subKey,
            Name = name
        };

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey, false);
            if (key is not null)
            {
                var names = key.GetValueNames();
                if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                {
                    var kind = key.GetValueKind(name);
                    var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    record.Existed = true;
                    record.Kind = kind.ToString();
                    record.Data = EncodeValue(value, kind);
                }
            }
        }
        catch
        {
            record.Existed = false;
        }

        AddRecord(record);
    }

    public static void RecordService(string serviceName)
    {
        var record = new ChangeRecord
        {
            Type = "Service",
            ServiceName = serviceName,
            PreviousStart = GetServiceStart(serviceName),
            WasRunning = IsServiceRunning(serviceName)
        };
        AddRecord(record);
    }

    public static void RecordBcdValue(string name, string? previousValue)
    {
        AddRecord(new ChangeRecord
        {
            Type = "Bcd",
            BcdName = name,
            BcdPrevious = previousValue
        });
    }

    public static void RecordPowerScheme(Guid? previousScheme)
    {
        AddRecord(new ChangeRecord
        {
            Type = "PowerScheme",
            PreviousScheme = previousScheme?.ToString()
        });
    }

    private static void AddRecord(ChangeRecord record)
    {
        EnsureJournalLoaded();
        lock (Gate)
        {
            Journal.Add(record);
            SaveJournalLocked();
        }
    }

    public static async Task<(bool Success, string Message, int Count)> RollbackAsync()
    {
        EnsureJournalLoaded();
        List<ChangeRecord> snapshot;
        lock (Gate)
        {
            snapshot = Journal.ToList();
        }

        if (snapshot.Count == 0)
        {
            return (true, "Журнал изменений пуст — откатывать нечего.", 0);
        }

        var restored = 0;
        var errors = new List<string>();
        await Task.Run(() =>
        {
            for (var i = snapshot.Count - 1; i >= 0; i--)
            {
                try
                {
                    RestoreRecord(snapshot[i]);
                    restored++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{snapshot[i].Type} {snapshot[i].Key}{snapshot[i].BcdName}: {ex.Message}");
                }
            }
        }).ConfigureAwait(false);

        ArchiveJournal();

        if (errors.Count > 0)
        {
            try
            {
                File.WriteAllLines(
                    Path.Combine(BaseDirectory, "rollback_errors.log"),
                    errors);
            }
            catch
            {
                // ignored
            }

            return (false,
                $"Откатено {restored} из {snapshot.Count} изменений. Ошибок: {errors.Count}. " +
                $"Подробности: {Path.Combine(BaseDirectory, "rollback_errors.log")}",
                restored);
        }

        return (true, $"Откатено {restored} изменений. Перезагрузите ПК, чтобы ядро и драйверы применили настройки.", restored);
    }

    private static void RestoreRecord(ChangeRecord record)
    {
        switch (record.Type)
        {
            case "Registry":
                RestoreRegistry(record);
                break;
            case "Service":
                RestoreService(record);
                break;
            case "Bcd":
                TweaksEngine.BcdWriteRaw(record.BcdName ?? "", record.BcdPrevious);
                break;
            case "PowerScheme":
                if (Guid.TryParse(record.PreviousScheme, out var guid))
                {
                    TweaksEngine.SetActivePowerPlan(guid);
                }
                break;
        }
    }

    private static void RestoreRegistry(ChangeRecord record)
    {
        if (!Enum.TryParse<RegistryHive>(record.Hive, out var hive))
        {
            hive = RegistryHive.LocalMachine;
        }

        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        if (!record.Existed)
        {
            using var existing = baseKey.OpenSubKey(record.Key, true);
            existing?.DeleteValue(record.Name, false);
            return;
        }

        using var key = baseKey.CreateSubKey(record.Key, true);
        if (key is null)
        {
            throw new InvalidOperationException($"Не удалось открыть ключ {record.Key}");
        }

        if (!Enum.TryParse<RegistryValueKind>(record.Kind, out var kind))
        {
            kind = RegistryValueKind.String;
        }

        var value = DecodeValue(record.Data, kind);
        key.SetValue(record.Name, value, kind);
    }

    private static void RestoreService(ChangeRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.ServiceName))
        {
            return;
        }

        if (record.PreviousStart is null)
        {
            return; // service did not exist
        }

        TweaksEngine.SetServiceStartRaw(record.ServiceName, record.PreviousStart.Value);
        if (record.WasRunning && record.PreviousStart.Value != 4)
        {
            TweaksEngine.StartServiceRaw(record.ServiceName);
        }
    }

    private static string? EncodeValue(object? value, RegistryValueKind kind)
    {
        if (value is null)
        {
            return null;
        }

        return kind switch
        {
            RegistryValueKind.DWord => Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            RegistryValueKind.QWord => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            RegistryValueKind.Binary => Convert.ToBase64String((byte[])value),
            RegistryValueKind.MultiString => string.Join("\u001f", (string[])value),
            _ => value.ToString()
        };
    }

    private static object DecodeValue(string? data, RegistryValueKind kind)
    {
        data ??= "";
        return kind switch
        {
            RegistryValueKind.DWord => int.Parse(data, CultureInfo.InvariantCulture),
            RegistryValueKind.QWord => long.Parse(data, CultureInfo.InvariantCulture),
            RegistryValueKind.Binary => Convert.FromBase64String(data),
            RegistryValueKind.MultiString => data.Split('\u001f'),
            _ => data
        };
    }

    private static int? GetServiceStart(string name)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}", false);
            var value = key?.GetValue("Start");
            return value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsServiceRunning(string name)
    {
        try
        {
            var result = RunProcess(
                Path.Combine(Environment.SystemDirectory, "sc.exe"),
                new[] { "query", name },
                10000);
            return result.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string GetBackupsSizeText()
    {
        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(BaseDirectory, "*", SearchOption.AllDirectories))
            {
                total += new FileInfo(file).Length;
            }

            return total > 1024 * 1024
                ? $"{total / 1024.0 / 1024.0:0.0} MB"
                : $"{total / 1024.0:0} KB";
        }
        catch
        {
            return "—";
        }
    }

    public static List<BackupEntry> GetBackupEntries(int max = 40)
    {
        var list = new List<BackupEntry>();
        try
        {
            if (!Directory.Exists(BaseDirectory))
            {
                return list;
            }

            foreach (var file in new DirectoryInfo(BaseDirectory).GetFiles("*.json", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(f => f.LastWriteTime)
                         .Take(max))
            {
                list.Add(new BackupEntry
                {
                    Name = file.Name,
                    CreatedText = file.LastWriteTime.ToString("dd.MM.yyyy HH:mm"),
                    SizeText = FormatSize(file.Length),
                    FullPath = file.FullName
                });
            }

            if (Directory.Exists(RegistryBackupDirectory))
            {
                foreach (var file in new DirectoryInfo(RegistryBackupDirectory).GetFiles("*.reg")
                             .OrderByDescending(f => f.LastWriteTime)
                             .Take(max))
                {
                    list.Add(new BackupEntry
                    {
                        Name = file.Name,
                        CreatedText = file.LastWriteTime.ToString("dd.MM.yyyy HH:mm"),
                        SizeText = FormatSize(file.Length),
                        FullPath = file.FullName
                    });
                }
            }
        }
        catch
        {
            // ignored
        }

        return list.OrderByDescending(x => x.CreatedText).Take(max).ToList();
    }

    private static string FormatSize(long bytes)
        => bytes > 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    public static void ExportProfile(string path, IEnumerable<TweakItem> tweaks)
    {
        var profile = new OptimizerProfile
        {
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            Items = tweaks.Select(t => new ProfileItem { Id = t.Id, Enabled = t.IsOn }).ToList()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions));
    }

    public static OptimizerProfile LoadProfile(string path)
    {
        var json = File.ReadAllText(path);
        var profile = JsonSerializer.Deserialize<OptimizerProfile>(json, JsonOptions)
                      ?? throw new InvalidDataException("Пустой профиль.");
        if (!string.Equals(profile.App, "GameOptimizer", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Это не профиль GameOptimizer.");
        }

        return profile;
    }

    public static (bool Success, string Message) ImportRegFile(string path)
    {
        try
        {
            var result = RunProcess(
                Path.Combine(Environment.SystemDirectory, "reg.exe"),
                new[] { "import", path },
                60000);
            return result.Ok
                ? (true, $"Ветка реестра импортирована: {Path.GetFileName(path)}. Перезагрузите ПК.")
                : (false, $"reg import: {(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error).Trim()}");
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка импорта .reg: {ex.Message}");
        }
    }

    private static void EnsureJournalLoaded()
    {
        if (_journalLoaded)
        {
            return;
        }

        lock (Gate)
        {
            if (_journalLoaded)
            {
                return;
            }

            _journalLoaded = true;
            try
            {
                if (!File.Exists(JournalPath))
                {
                    return;
                }

                var json = File.ReadAllText(JournalPath);
                var records = JsonSerializer.Deserialize<List<ChangeRecord>>(json, JsonOptions);
                if (records is not null)
                {
                    Journal.AddRange(records);
                }
            }
            catch
            {
                try
                {
                    File.Move(JournalPath, Path.Combine(BaseDirectory, $"journal_corrupt_{DateTime.Now:yyyyMMdd_HHmmss}.json"), true);
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    private static void SaveJournalLocked()
    {
        try
        {
            Directory.CreateDirectory(BaseDirectory);
            var temp = JournalPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Journal, JsonOptions));
            File.Move(temp, JournalPath, true);
        }
        catch
        {
            // ignored
        }
    }

    private static void ArchiveJournal()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(JournalPath))
                {
                    var archive = Path.Combine(BaseDirectory, $"journal_{DateTime.Now:yyyyMMdd_HHmmss}_rolledback.json");
                    File.Move(JournalPath, archive, true);
                }
            }
            catch
            {
                // ignored
            }

            Journal.Clear();
        }
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Replace('\\', '_').Replace('/', '_');
        return result.Length > 80 ? result[^80..] : result;
    }

    private static (bool Ok, string Output, string Error) RunProcess(
        string fileName,
        IEnumerable<string> arguments,
        int timeoutMs)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
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
}
