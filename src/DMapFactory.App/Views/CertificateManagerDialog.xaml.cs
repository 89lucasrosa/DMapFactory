using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DMapFactory.App.Services;
using DMapFactory.Drivers.OpcUa;
using DMapFactory.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DMapFactory.App.Views;

public partial class CertificateManagerDialog : Window
{
    private readonly OpcUaSecurityManager _securityManager;
    private readonly ILogger _logger;
    private readonly SettingsService? _settings;
    private bool _suppressAutoAcceptEvent;

    private IReadOnlyList<CertificateInfo> _allServerCerts = [];
    private IReadOnlyList<CertificateInfo> _trustedCerts = [];
    private IReadOnlyList<CertificateInfo> _rejectedCerts = [];
    private List<DiscoveredServerCertificate> _discoveredCerts = [];

    public CertificateManagerDialog(
        OpcUaSecurityManager securityManager,
        ILogger logger,
        SettingsService? settings = null,
        IReadOnlyList<string>? knownEndpointUrls = null)
    {
        InitializeComponent();
        _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settings = settings;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.F1)
            {
                e.Handled = true;
                HelpService.OpenHelp(HelpTopicIds.Certificates);
            }
        };

        _suppressAutoAcceptEvent = true;
        AutoAcceptServerCertsCheck.IsChecked = _securityManager.AutoAcceptUntrustedServerCertificates;
        _suppressAutoAcceptEvent = false;

        PkiPathBox.Text = _securityManager.PkiRoot;

        // Padrões de geração de certificado do cliente (Q2: 2 anos de validade e 2048 bits)
        var today = DateTime.Today;
        GenNotBeforePicker.SelectedDate = today;
        GenNotAfterPicker.SelectedDate = today.AddYears(2);
        GenKeySizeCombo.SelectedIndex = 0;

        // Popula URLs conhecidas para a aba de Descoberta de Certificados
        var urls = new List<string>();
        if (knownEndpointUrls is { Count: > 0 })
        {
            foreach (var u in knownEndpointUrls)
            {
                if (!string.IsNullOrWhiteSpace(u) && !urls.Contains(u, StringComparer.OrdinalIgnoreCase))
                    urls.Add(u);
            }
        }
        if (urls.Count == 0)
        {
            urls.Add("opc.tcp://localhost:4840");
        }
        DiscoveryUrlCombo.ItemsSource = urls;
        DiscoveryUrlCombo.Text = urls[0];

        UpdateServerButtonsState();
        UpdateDiscoveryButtonsState();

        Loaded += async (_, _) => await RefreshAllAsync();
    }

    private async Task RefreshAllAsync()
    {
        try
        {
            var appCert = await _securityManager.GetApplicationCertificateInfoAsync();
            if (appCert is not null)
            {
                ClientSubjectBox.Text = appCert.Subject;
                ClientUriBox.Text = string.IsNullOrWhiteSpace(appCert.ApplicationUri) ? "—" : appCert.ApplicationUri;
                ClientThumbprintBox.Text = appCert.Thumbprint;
                ClientValidityText.Text =
                    $"{appCert.NotBeforeLocal:dd/MM/yyyy HH:mm}  ..  {appCert.NotAfterLocal:dd/MM/yyyy HH:mm}  —  {appCert.ValidityStatusText}";
                ClientKeyInfoText.Text = appCert.KeySize > 0
                    ? $"RSA {appCert.KeySize} bits ({appCert.SignatureAlgorithm})"
                    : appCert.SignatureAlgorithm;
            }
            else
            {
                ClientSubjectBox.Text = "—";
                ClientUriBox.Text = "—";
                ClientThumbprintBox.Text = "—";
                ClientValidityText.Text = "—";
                ClientKeyInfoText.Text = "—";
            }

            _trustedCerts = await _securityManager.GetTrustedServerCertificatesAsync();
            _rejectedCerts = await _securityManager.GetRejectedServerCertificatesAsync();
            _allServerCerts = await _securityManager.GetAllServerCertificatesAsync();

            FilterAllRadio.Content = Loc.Instance.Format("CertDlg_Filter_All", _allServerCerts.Count);
            FilterTrustedRadio.Content = Loc.Instance.Format("CertDlg_Filter_Trusted", _trustedCerts.Count);
            FilterRejectedRadio.Content = Loc.Instance.Format("CertDlg_Filter_Rejected", _rejectedCerts.Count);

            ApplyServerFilter();
            SyncDiscoveredStatusesWithStores();
        }
        catch (Exception ex)
        {
            string msg = Loc.Instance.Format("Log_CertOperationError", ex.Message);
            StatusText.Text = msg;
            _logger.LogError(ex, "{Message}", msg);
        }
    }

    private void ApplyServerFilter()
    {
        if (ServersGrid is null) return;

        string? previousThumb = (ServersGrid.SelectedItem as CertificateInfo)?.Thumbprint;
        IReadOnlyList<CertificateInfo> source =
            FilterTrustedRadio?.IsChecked == true ? _trustedCerts :
            FilterRejectedRadio?.IsChecked == true ? _rejectedCerts :
            _allServerCerts;

        ServersGrid.ItemsSource = source;

        if (!string.IsNullOrWhiteSpace(previousThumb))
        {
            var match = source.FirstOrDefault(c => string.Equals(c.Thumbprint, previousThumb, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                ServersGrid.SelectedItem = match;
            }
        }

        if (ServersGrid.SelectedItem is null && source.Count > 0)
        {
            ServersGrid.SelectedIndex = 0;
        }

        UpdateServerButtonsState();
    }

    private void SyncDiscoveredStatusesWithStores()
    {
        if (_discoveredCerts.Count == 0) return;

        var trustedSet = new HashSet<string>(_trustedCerts.Select(c => c.Thumbprint), StringComparer.OrdinalIgnoreCase);
        var rejectedSet = new HashSet<string>(_rejectedCerts.Select(c => c.Thumbprint), StringComparer.OrdinalIgnoreCase);
        string? selectedThumb = (DiscoveredCertsGrid.SelectedItem as DiscoveredServerCertificate)?.Certificate.Thumbprint;

        var updated = new List<DiscoveredServerCertificate>(_discoveredCerts.Count);
        foreach (var item in _discoveredCerts)
        {
            var newStatus = trustedSet.Contains(item.Certificate.Thumbprint)
                ? CertificateTrustStatus.Trusted
                : rejectedSet.Contains(item.Certificate.Thumbprint)
                    ? CertificateTrustStatus.Rejected
                    : CertificateTrustStatus.Unknown;

            updated.Add(item with { Certificate = item.Certificate with { TrustStatus = newStatus } });
        }

        _discoveredCerts = updated;
        DiscoveredCertsGrid.ItemsSource = null;
        DiscoveredCertsGrid.ItemsSource = _discoveredCerts;

        if (!string.IsNullOrWhiteSpace(selectedThumb))
        {
            DiscoveredCertsGrid.SelectedItem = _discoveredCerts.FirstOrDefault(
                d => string.Equals(d.Certificate.Thumbprint, selectedThumb, StringComparison.OrdinalIgnoreCase));
        }

        UpdateDiscoveryButtonsState();
    }

    private void ServerFilter_Changed(object sender, RoutedEventArgs e)
    {
        ApplyServerFilter();
    }

    private async void RefreshServers_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAllAsync();
        StatusText.Text = Loc.Instance["CertDlg_StatusRefreshed"];
    }

    private void ServersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateServerButtonsState();
    }

    private void UpdateServerButtonsState()
    {
        if (ServersGrid is null) return;

        if (ServersGrid.SelectedItem is CertificateInfo selected)
        {
            SelectedServerSubjectBox.Text = $"{selected.Subject} (Issuer: {selected.Issuer})";
            SelectedServerUriBox.Text = string.IsNullOrWhiteSpace(selected.ApplicationUri) ? "—" : selected.ApplicationUri;
            SelectedServerThumbBox.Text = selected.Thumbprint;
            string keyPart = selected.KeySize > 0 ? $"RSA {selected.KeySize} bits — " : "";
            SelectedServerValidityBox.Text = $"{keyPart}{selected.ValidityRangeText} ({selected.ValidityStatusText})";

            TrustServerBtn.IsEnabled = selected.TrustStatus != CertificateTrustStatus.Trusted;
            RejectServerBtn.IsEnabled = selected.TrustStatus != CertificateTrustStatus.Rejected;
            ExportServerBtn.IsEnabled = true;
            RemoveServerBtn.IsEnabled = true;
        }
        else
        {
            SelectedServerSubjectBox.Text = "—";
            SelectedServerUriBox.Text = "—";
            SelectedServerThumbBox.Text = "—";
            SelectedServerValidityBox.Text = "—";

            TrustServerBtn.IsEnabled = false;
            RejectServerBtn.IsEnabled = false;
            ExportServerBtn.IsEnabled = false;
            RemoveServerBtn.IsEnabled = false;
        }
    }

    private void DiscoveredCertsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDiscoveryButtonsState();
    }

    private void UpdateDiscoveryButtonsState()
    {
        if (DiscoveredCertsGrid is null) return;

        if (DiscoveredCertsGrid.SelectedItem is DiscoveredServerCertificate item)
        {
            TrustDiscoveredBtn.IsEnabled = item.Certificate.TrustStatus != CertificateTrustStatus.Trusted;
            RejectDiscoveredBtn.IsEnabled = item.Certificate.TrustStatus != CertificateTrustStatus.Rejected;
            ExportDiscoveredBtn.IsEnabled = true;
        }
        else
        {
            TrustDiscoveredBtn.IsEnabled = false;
            RejectDiscoveredBtn.IsEnabled = false;
            ExportDiscoveredBtn.IsEnabled = false;
        }
    }

    private void AutoAcceptServerCertsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoAcceptEvent) return;
        bool autoAccept = AutoAcceptServerCertsCheck.IsChecked == true;
        _securityManager.AutoAcceptUntrustedServerCertificates = autoAccept;

        if (_settings is not null)
        {
            _settings.Current.AutoAcceptUntrustedCertificates = autoAccept;
            _settings.Save();
        }

        if (autoAccept)
        {
            string msg = Loc.Instance["Log_CertAutoAcceptEnabled"];
            StatusText.Text = msg;
            _logger.LogWarning("{Message}", msg);
        }
        else
        {
            string msg = Loc.Instance["Log_CertAutoAcceptDisabled"];
            StatusText.Text = msg;
            _logger.LogInformation("{Message}", msg);
        }
    }

    // =========================================================================
    // ABA 1: AÇÕES SOBRE CERTIFICADOS DE SERVIDORES (TRUST / REJECT / IMPORT / EXPORT / DELETE)
    // =========================================================================

    private async void TrustSelectedServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServersGrid.SelectedItem is not CertificateInfo selected)
            return;

        try
        {
            await _securityManager.TrustRejectedCertificateAsync(selected.Thumbprint);
            await RefreshAllAsync();
            string msg = Loc.Instance.Format("Log_CertServerTrusted", selected.Subject, selected.Thumbprint);
            StatusText.Text = $"✔ {msg}";
            _logger.LogInformation("{Message}", msg);
        }
        catch (Exception ex)
        {
            string err = Loc.Instance.Format("Log_CertOperationError", ex.Message);
            StatusText.Text = err;
            _logger.LogError(ex, "{Message}", err);
        }
    }

    private async void RejectSelectedServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServersGrid.SelectedItem is not CertificateInfo selected)
            return;

        try
        {
            await _securityManager.RejectTrustedCertificateAsync(selected.Thumbprint);
            await RefreshAllAsync();
            string msg = Loc.Instance.Format("Log_CertServerRejected", selected.Subject, selected.Thumbprint);
            StatusText.Text = $"✖ {msg}";
            _logger.LogWarning("{Message}", msg);
        }
        catch (Exception ex)
        {
            string err = Loc.Instance.Format("Log_CertOperationError", ex.Message);
            StatusText.Text = err;
            _logger.LogError(ex, "{Message}", err);
        }
    }

    private async void ImportServerCert_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = Loc.Instance["CertDlg_Btn_ImportServer"],
            Filter = "Server Certificates (*.der;*.cer;*.crt;*.pem)|*.der;*.cer;*.crt;*.pem|All files (*.*)|*.*",
            Multiselect = true
        };
        if (ofd.ShowDialog(this) != true) return;

        try
        {
            string lastMsg = "";
            foreach (var file in ofd.FileNames)
            {
                var info = await _securityManager.ImportTrustedServerCertificateAsync(file);
                lastMsg = Loc.Instance.Format("Log_CertServerImported", info.Subject, info.Thumbprint);
                _logger.LogInformation("{Message}", lastMsg);
            }

            await RefreshAllAsync();
            StatusText.Text = lastMsg;
        }
        catch (Exception ex)
        {
            string err = Loc.Instance.Format("Log_CertOperationError", ex.Message);
            StatusText.Text = err;
            _logger.LogError(ex, "{Message}", err);
        }
    }

    private async void ExportSelectedServerCert_Click(object sender, RoutedEventArgs e)
    {
        if (ServersGrid.SelectedItem is not CertificateInfo selected)
            return;

        var sfd = new SaveFileDialog
        {
            Title = Loc.Instance["CertDlg_Btn_ExportServer"],
            FileName = $"Server-{selected.Thumbprint[..Math.Min(8, selected.Thumbprint.Length)]}.der",
            Filter = "DER Certificate (*.der;*.cer)|*.der;*.cer|All files (*.*)|*.*"
        };
        if (sfd.ShowDialog(this) != true) return;

        try
        {
            bool fromTrusted = selected.TrustStatus == CertificateTrustStatus.Trusted;
            await _securityManager.ExportServerCertificateDerAsync(selected.Thumbprint, fromTrusted, sfd.FileName);
            string msg = Loc.Instance.Format("Log_CertServerExported", selected.Subject, sfd.FileName);
            StatusText.Text = msg;
            _logger.LogInformation("{Message}", msg);
        }
        catch (Exception ex)
        {
            string err = Loc.Instance.Format("Log_CertOperationError", ex.Message);
            StatusText.Text = err;
            _logger.LogError(ex, "{Message}", err);
        }
    }

    private async void RemoveSelectedServerCert_Click(object sender, RoutedEventArgs e)
    {
        if (ServersGrid.SelectedItem is not CertificateInfo selected)
            return;

        var confirm = MessageBox.Show(
            this,
            $"{selected.Subject}\nThumbprint: {selected.Thumbprint}\nStatus: {selected.TrustStatusText}",
            Loc.Instance["CertDlg_Btn_DeleteServer"],
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            bool fromTrusted = selected.TrustStatus == CertificateTrustStatus.Trusted;
            await _securityManager.RemoveServerCertificateAsync(selected.Thumbprint, fromTrusted);
            await RefreshAllAsync();
            string msg = Loc.Instance.Format("Log_CertServerDeleted", selected.Subject, selected.Thumbprint);
            StatusText.Text = msg;
            _logger.LogInformation("{Message}", msg);
        }
        catch (Exception ex)
        {
            string err = Loc.Instance.Format("Log_CertOperationError", ex.Message);
            StatusText.Text = err;
            _logger.LogError(ex, "{Message}", err);
        }
    }

    // =========================================================================
    // ABA 2: DESCOBERTA DE CERTIFICADOS DE SERVIDORES (DISCOVERY)
    // =========================================================================

    private async void DiscoverServerCerts_Click(object sender, RoutedEventArgs e)
    {
        string url = DiscoveryUrlCombo.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            DiscoverBtn.IsEnabled = false;
            StatusText.Text = $"{url}...";

            var discovered = await _securityManager.DiscoverServerCertificatesAsync(url, timeoutMs: 10_000);
            _discoveredCerts = discovered.ToList();
            DiscoveredCertsGrid.ItemsSource = _discoveredCerts;

            if (_discoveredCerts.Count > 0)
            {
                DiscoveredCertsGrid.SelectedIndex = 0;
                string msg = Loc.Instance.Format("Log_CertDiscoverySuccess", url, _discoveredCerts.Count);
                StatusText.Text = msg;
                _logger.LogInformation("{Message}", msg);
            }
            else
            {
                string msg = Loc.Instance.Format("Log_CertDiscoveryNoneWarn", url);
                StatusText.Text = msg;
                _logger.LogWarning("{Message}", msg);
            }
        }
        catch (Exception ex)
        {
            string err = Loc.Instance.Format("Log_CertDiscoveryError", url, ex.Message);
            StatusText.Text = err;
            _logger.LogError(ex, "{Message}", err);
        }
        finally
        {
            DiscoverBtn.IsEnabled = true;
            UpdateDiscoveryButtonsState();
        }
    }

    private async void TrustDiscoveredCert_Click(object sender, RoutedEventArgs e)
    {
        if (DiscoveredCertsGrid.SelectedItem is not DiscoveredServerCertificate item)
            return;

        try
        {
            var info = await _securityManager.TrustServerCertificateAsync(item.RawData);
            await RefreshAllAsync();
            string msg = Loc.Instance.Format("Log_CertServerTrusted", info.Subject, info.Thumbprint);
            StatusText.Text = $"✔ {msg}";
            _logger.LogInformation("{Message}", msg);
        }
        catch (Exception ex)
        {
            string err = Loc.Instance.Format("Log_CertOperationError", ex.Message);
            StatusText.Text = err;
            _logger.LogError(ex, "{Message}", err);
        }
    }

    private async void RejectDiscoveredCert_Click(object sender, RoutedEventArgs e)
    {
        if (DiscoveredCertsGrid.SelectedItem is not DiscoveredServerCertificate item)
            return;

        try
        {
            var info = await _securityManager.RejectServerCertificateAsync(item.RawData);
            await RefreshAllAsync();
            string msg = Loc.Instance.Format("Log_CertServerRejected", info.Subject, info.Thumbprint);
            StatusText.Text = $"✖ {msg}";
            _logger.LogWarning("{Message}", msg);
        }
        catch (Exception ex)
        {
            string err = Loc.Instance.Format("Log_CertOperationError", ex.Message);
            StatusText.Text = err;
            _logger.LogError(ex, "{Message}", err);
        }
    }

    private async void ExportDiscoveredCert_Click(object sender, RoutedEventArgs e)
    {
        if (DiscoveredCertsGrid.SelectedItem is not DiscoveredServerCertificate item)
            return;

        var sfd = new SaveFileDialog
        {
            Title = Loc.Instance["CertDlg_Btn_ExportServer"],
            FileName = $"DiscoveredServer-{item.Certificate.Thumbprint[..Math.Min(8, item.Certificate.Thumbprint.Length)]}.der",
            Filter = "DER Certificate (*.der;*.cer)|*.der;*.cer"
        };
        if (sfd.ShowDialog(this) != true) return;

        try
        {
            await File.WriteAllBytesAsync(sfd.FileName, item.RawData);
            string msg = Loc.Instance.Format("Log_CertServerExported", item.Certificate.Subject, sfd.FileName);
            StatusText.Text = msg;
            _logger.LogInformation("{Message}", msg);
        }
        catch (Exception ex)
        {
            string err = Loc.Instance.Format("Log_CertOperationError", ex.Message);
            StatusText.Text = err;
            _logger.LogError(ex, "{Message}", err);
        }
    }

    // =========================================================================
    // ABA 3: MEU CERTIFICADO (CLIENTE OPC UA — GERAÇÃO COM VALIDADE, EXPORT, AVANÇADO)
    // =========================================================================

    private void PresetValidityYears_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tagStr } && int.TryParse(tagStr, out int years) && years > 0)
        {
            var startDate = GenNotBeforePicker.SelectedDate ?? DateTime.Today;
            GenNotBeforePicker.SelectedDate = startDate;
            GenNotAfterPicker.SelectedDate = startDate.AddYears(years);
            StatusText.Text = $"{startDate:dd/MM/yyyy} .. {startDate.AddYears(years):dd/MM/yyyy}";
        }
    }

    private async void GenerateClientCert_Click(object sender, RoutedEventArgs e)
    {
        string cn = string.IsNullOrWhiteSpace(GenCommonNameBox.Text)
            ? "DMapFactory OPC UA Client"
            : GenCommonNameBox.Text.Trim();
        string org = string.IsNullOrWhiteSpace(GenOrganizationBox.Text)
            ? "DMapFactory"
            : GenOrganizationBox.Text.Trim();

        DateTime startDate = (GenNotBeforePicker.SelectedDate ?? DateTime.Today).Date;
        DateTime endDate = (GenNotAfterPicker.SelectedDate ?? startDate.AddYears(2)).Date;

        if (endDate <= startDate)
            return;

        ushort keySize = 2048;
        if (GenKeySizeCombo.SelectedItem is ComboBoxItem { Tag: string ksStr } &&
            ushort.TryParse(ksStr, out ushort parsedKs))
        {
            keySize = parsedKs;
        }

        var confirm = MessageBox.Show(
            this,
            $"CN={cn}, O={org}\nRSA {keySize} bits\n{startDate:dd/MM/yyyy} .. {endDate:dd/MM/yyyy}",
            Loc.Instance["CertDlg_Btn_GenerateClient"],
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var options = new ClientCertificateGenerationOptions(
                CommonName: cn,
                Organization: org,
                KeySize: keySize,
                NotBeforeUtc: DateTime.SpecifyKind(startDate, DateTimeKind.Local).ToUniversalTime(),
                NotAfterUtc: DateTime.SpecifyKind(endDate.AddHours(23).AddMinutes(59).AddSeconds(59), DateTimeKind.Local).ToUniversalTime());

            var info = await _securityManager.GenerateApplicationCertificateAsync(options);
            await RefreshAllAsync();

            string msg = Loc.Instance.Format(
                "Log_CertClientGenerated",
                info.Subject,
                info.Thumbprint,
                info.KeySize,
                info.NotBeforeLocal.ToString("yyyy-MM-dd"),
                info.NotAfterLocal.ToString("yyyy-MM-dd"));
            StatusText.Text = $"✔ {msg}";
            _logger.LogInformation("{Message}", msg);
        }
        catch (Exception ex)
        {
            string err = Loc.Instance.Format("Log_CertOperationError", ex.Message);
            StatusText.Text = err;
            _logger.LogError(ex, "{Message}", err);
        }
    }

    private async void ExportClientDer_Click(object sender, RoutedEventArgs e)
    {
        var sfd = new SaveFileDialog
        {
            Title = Loc.Instance["CertDlg_Btn_ExportClientDer"],
            FileName = "DMapFactory-Client.der",
            Filter = "DER Certificate (*.der;*.cer)|*.der;*.cer|All files (*.*)|*.*"
        };
        if (sfd.ShowDialog(this) != true) return;

        try
        {
            await _securityManager.ExportApplicationCertificateDerAsync(sfd.FileName);
            string msg = Loc.Instance.Format("Log_CertClientExportedDer", sfd.FileName);
            StatusText.Text = msg;
            _logger.LogInformation("{Message}", msg);
        }
        catch (Exception ex)
        {
            string err = Loc.Instance.Format("Log_CertOperationError", ex.Message);
            StatusText.Text = err;
            _logger.LogError(ex, "{Message}", err);
        }
    }

    private async void ExportClientPfx_Click(object sender, RoutedEventArgs e)
    {
        var sfd = new SaveFileDialog
        {
            Title = Loc.Instance["CertDlg_Btn_ExportClientPfx"],
            FileName = "DMapFactory-Client.pfx",
            Filter = "PKCS#12 (*.pfx;*.p12)|*.pfx;*.p12"
        };
        if (sfd.ShowDialog(this) != true) return;

        try
        {
            await _securityManager.ExportApplicationCertificatePfxAsync(sfd.FileName, password: null);
            string msg = Loc.Instance.Format("Log_CertClientExportedPfx", sfd.FileName);
            StatusText.Text = msg;
            _logger.LogInformation("{Message}", msg);
        }
        catch (Exception ex)
        {
            string err = Loc.Instance.Format("Log_CertOperationError", ex.Message);
            StatusText.Text = err;
            _logger.LogError(ex, "{Message}", err);
        }
    }

    private async void ImportClientCert_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = Loc.Instance["CertDlg_Btn_ImportClient"],
            Filter = "Certificates (*.pfx;*.p12;*.der;*.cer;*.crt)|*.pfx;*.p12;*.der;*.cer;*.crt|All files (*.*)|*.*"
        };
        if (ofd.ShowDialog(this) != true) return;

        try
        {
            var info = await _securityManager.ImportApplicationCertificateAsync(ofd.FileName, password: null);
            await RefreshAllAsync();
            string msg = Loc.Instance.Format("Log_CertClientImported", ofd.FileName, info.Thumbprint);
            StatusText.Text = msg;
            _logger.LogInformation("{Message}", msg);
        }
        catch (Exception ex)
        {
            string err = Loc.Instance.Format("Log_CertOperationError", ex.Message);
            StatusText.Text = err;
            _logger.LogError(ex, "{Message}", err);
        }
    }

    private void OpenPkiFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_securityManager.PkiRoot);
            Process.Start(new ProcessStartInfo
            {
                FileName = _securityManager.PkiRoot,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            string err = Loc.Instance.Format("Log_CertOperationError", ex.Message);
            StatusText.Text = err;
            _logger.LogError(ex, "{Message}", err);
        }
    }
}
