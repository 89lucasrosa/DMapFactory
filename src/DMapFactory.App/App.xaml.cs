using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DMapFactory.Acquisition;
using DMapFactory.App.Services;
using DMapFactory.App.ViewModels;
using DMapFactory.App.Views;
using DMapFactory.Drivers.OpcUa;
using DMapFactory.Localization;
using Microsoft.Extensions.Logging;
using Serilog;

namespace DMapFactory.App;

/// <summary>
/// Ponto de entrada: configura logs (Serilog), serviços e idioma, e abre a janela principal imediatamente.
/// </summary>
public partial class App : Application
{
    private ILoggerFactory? _loggerFactory;
    private MainViewModel? _mainViewModel;
    private int _isShuttingDown;

    public App()
    {
        // Define renderização por software antes mesmo de o MediaContext do WPF ser criado.
        // Evita travamentos do driver Direct3D9Ex em máquinas virtuais (VMware SVGA 3D, Hyper-V, RDP) no Windows 10.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        var sw = Stopwatch.StartNew();
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Log.Fatal(ex, "Exceção não tratada");
            WriteCrashFile(ex);
        };

        try
        {
            base.OnStartup(e);

            var settings = new SettingsService();
            try
            {
                settings.Load();
                Loc.Instance.Culture = CultureInfo.GetCultureInfo(
                    string.IsNullOrWhiteSpace(settings.Current.Language) ? "pt-BR" : settings.Current.Language);
            }
            catch
            {
                Loc.Instance.Culture = CultureInfo.GetCultureInfo("pt-BR");
            }

            var uiLogSink = new UiLogSink();
            uiLogSink.AttachToDispatcher();

            var logConfig = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.WithProperty("WindowsUser", Environment.UserName)
                .WriteTo.Sink(uiLogSink, Serilog.Events.LogEventLevel.Information);

            try
            {
                Directory.CreateDirectory(SettingsService.LogsFolder);
                logConfig = logConfig.WriteTo.File(
                    Path.Combine(SettingsService.LogsFolder, "dmapfactory-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 31,
                    shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}");
            }
            catch
            {
                // Se a pasta de logs estiver bloqueada, continua com o log de interface normalmente
            }

            Log.Logger = logConfig.CreateLogger();
            _loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog(Log.Logger, dispose: false));

            var securityManager = new OpcUaSecurityManager
            {
                AutoAcceptUntrustedServerCertificates = settings.Current.AutoAcceptUntrustedCertificates,
                UntrustedCertificatePrompt = cert =>
                {
                    return Current.Dispatcher.Invoke(() =>
                    {
                        string uriLine = string.IsNullOrWhiteSpace(cert.ApplicationUri)
                            ? ""
                            : $"\nURI da Aplicação: {cert.ApplicationUri}";
                        string message =
                            "O servidor OPC UA apresentou um certificado que ainda não está na lista de confiáveis (Trusted):\n\n" +
                            $"Requerente (Subject): {cert.Subject}\n" +
                            $"Emissor (Issuer): {cert.Issuer}{uriLine}\n" +
                            $"Thumbprint (SHA-1): {cert.Thumbprint}\n" +
                            $"Validade: {cert.NotBeforeLocal:dd/MM/yyyy HH:mm} até {cert.NotAfterLocal:dd/MM/yyyy HH:mm}\n\n" +
                            "Deseja CONFIAR neste certificado de servidor (mover para Trusted) e continuar a conexão?\n" +
                            "• Clique em 'Sim' para Confiar e Conectar.\n" +
                            "• Clique em 'Não' para Rejeitar o certificado (será gravado na lista de Rejeitados).";

                        var owner = Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsLoaded)
                                    ?? Current.Windows.OfType<Window>().LastOrDefault(w => w.IsVisible && w.IsLoaded)
                                    ?? (Current.MainWindow is { IsLoaded: true } mw ? mw : null);
                        var result = owner is not null
                            ? MessageBox.Show(owner, message, "Certificado de Servidor OPC UA Desconhecido", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                            : MessageBox.Show(message, "Certificado de Servidor OPC UA Desconhecido", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                        return result == MessageBoxResult.Yes;
                    });
                }
            };
            var liveSnapshot = new LiveSnapshot();
            var sessionManager = new SessionManager(liveSnapshot);

            _mainViewModel = new MainViewModel(
                settings,
                securityManager,
                sessionManager,
                uiLogSink,
                _loggerFactory.CreateLogger<MainViewModel>(),
                _loggerFactory.CreateLogger<OpcUaDriver>());

            var appLogger = _loggerFactory.CreateLogger<App>();
            appLogger.LogInformation("{Message}", Loc.Instance.Format("Log_AppStarted", MainViewModel.AppVersion));

            var window = new MainWindow(_mainViewModel);
            MainWindow = window;
            window.Closed += (_, _) => PerformCleanExit();

            // Garante que a janela caiba e fique visível dentro da área útil do monitor (mesmo em resoluções menores de VM)
            var workArea = SystemParameters.WorkArea;
            if (workArea.Width >= 640 && workArea.Height >= 480)
            {
                window.Width = Math.Min(window.Width, Math.Max(800, workArea.Width - 24));
                window.Height = Math.Min(window.Height, Math.Max(540, workArea.Height - 24));
                window.Left = workArea.Left + Math.Max(0, (workArea.Width - window.Width) / 2);
                window.Top = workArea.Top + Math.Max(0, (workArea.Height - window.Height) / 2);
            }

            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();

            appLogger.LogDebug(
                "Usuário Windows: {User}; idioma: {Lang}; janela exibida em {ElapsedMs} ms (RenderMode={RenderMode})",
                Environment.UserName,
                settings.Current.Language,
                sw.ElapsedMilliseconds,
                RenderOptions.ProcessRenderMode);

            // MG-05: Instâncias múltiplas permitidas; remoção de KillStaleBackgroundInstances destrutivo.
        }
        catch (Exception ex)
        {
            WriteCrashFile(ex);
            MessageBox.Show(
                $"Falha ao iniciar o DMapFactory:\n\n{ex}",
                "DMapFactory — Erro de Inicialização",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Environment.Exit(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        PerformCleanExit();
        base.OnExit(e);
    }

    private void PerformCleanExit()
    {
        if (Interlocked.Exchange(ref _isShuttingDown, 1) != 0)
            return;

        try
        {
            var vm = _mainViewModel;
            if (vm is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await vm.ShutdownAsync().ConfigureAwait(false);
                    }
                    catch { }
                }).Wait(TimeSpan.FromMilliseconds(1500));
            }

            _loggerFactory?.Dispose();
            Log.CloseAndFlush();
        }
        catch { }
        finally
        {
            Environment.Exit(0);
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Erro não tratado na interface");
        WriteCrashFile(e.Exception);
        MessageBox.Show(e.Exception.Message, "DMapFactory", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void WriteCrashFile(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            string crashPath = Path.Combine(AppContext.BaseDirectory, "dmapfactory-crash.log");
            File.AppendAllText(crashPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }
}

