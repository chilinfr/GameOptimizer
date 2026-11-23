using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace GameOptimizer;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    public static bool IsAdministrator { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, @"Global\GameOptimizer_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "GameOptimizer уже запущен.",
                "GameOptimizer",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        using (var identity = WindowsIdentity.GetCurrent())
        {
            var principal = new WindowsPrincipal(identity);
            IsAdministrator = principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        base.OnStartup(e);

        if (!IsAdministrator)
        {
            MessageBox.Show(
                "Приложение запущено без прав администратора.\n\n" +
                "Часть твиков (реестр HKLM, bcdedit, powercfg, службы) не сможет примениться. " +
                "Закройте приложение и запустите GameOptimizer.exe от имени администратора.",
                "Ограниченные права",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException(e.Exception);
        MessageBox.Show(
            $"Произошла непредвиденная ошибка:\n\n{e.Exception.Message}\n\n" +
            "Подробности записаны в Backups\\crash.log.",
            "GameOptimizer",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            LogException(exception);
        }
    }

    private static void LogException(Exception exception)
    {
        try
        {
            var path = Path.Combine(BackupManager.BaseDirectory, "crash.log");
            File.AppendAllText(
                path,
                $"[{DateTime.Now:O}] {exception}\n\n",
                Encoding.UTF8);
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
