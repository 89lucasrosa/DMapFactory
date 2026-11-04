using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DMapFactory.App.Services;
using DMapFactory.Drivers.OpcUa;
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
                    $"{appCert.NotBeforeLocal:dd/MM/yyyy HH:mm}  até  {appCert.NotAfterLocal:dd/MM/yyyy HH:mm}  —  {appCert.ValidityStatusText}";
                ClientKeyInfoText.Text = appCert.KeySize > 0
                    ? $"RSA {appCert.KeySize} bits ({appCert.SignatureAlgorithm})"
                    : appCert.SignatureAlgorithm;
            }
            else
            {
                ClientSubjectBox.Text = "(Nenhum certificado encontrado)";
                ClientUriBox.Text = "—";
                ClientThumbprintBox.Text = "—";
                ClientValidityText.Text = "—";
                ClientKeyInfoText.Text = "—";
            }

            _trustedCerts = await _securityManager.GetTrustedServerCertificatesAsync();
            _rejectedCerts = await _securityManager.GetRejectedServerCertificatesAsync();
            _allServerCerts = await _securityManager.GetAllServerCertificatesAsync();

            FilterAllRadio.Content = $"Todos ({_allServerCerts.Count})";
            FilterTrustedRadio.Content = $"Confiáveis ({_trustedCerts.Count})";
            FilterRejectedRadio.Content = $"Rejeitados ({_rejectedCerts.Count})";

            ApplyServerFilter();
            SyncDiscoveredStatusesWithStores();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao carregar certificados: {ex.Message}";
            _logger.LogError(ex, "Erro ao atualizar gerenciador de certificados");
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
        StatusText.Text = "Lista de certificados atualizada.";
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
            SelectedServerSubjectBox.Text = $"{selected.Subject} (Emissor: {selected.Issuer})";
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
            SelectedServerSubjectBox.Text = "Selecione um certificado na lista acima";
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

        StatusText.Text = autoAccept
            ? "Aceitação automática de certificados de servidores ativada."
            : "Aceitação automática desativada (apenas servidores na lista Confiáveis serão aceitos sem confirmação).";
    }

    // =========================================================================
    // ABA 1: AÇÕES SOBRE CERTIFICADOS DE SERVIDORES (TRUST / REJECT / IMPORT / EXPORT / DELETE)
    // =========================================================================

    private async void TrustSelectedServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServersGrid.SelectedItem is not CertificateInfo selected)
        {
            StatusText.Text = "Selecione um certificado na lista para confiar nele.";
            return;
        }

        try
        {
            bool ok = await _securityManager.TrustRejectedCertificateAsync(selected.Thumbprint);
            await RefreshAllAsync();
            StatusText.Text = ok
                ? $"✔ Certificado '{selected.Subject}' movido para Servidores Confiáveis (Trusted)."
                : "O certificado selecionado já está na lista de confiáveis ou não pôde ser movido.";
            _logger.LogInformation("Certificado de servidor '{Subject}' ({Thumb}) marcado como Confiável (Trusted).", selected.Subject, selected.Thumbprint);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao confiar no certificado: {ex.Message}";
            _logger.LogError(ex, "Erro ao mover certificado para Confiáveis");
        }
    }

    private async void RejectSelectedServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServersGrid.SelectedItem is not CertificateInfo selected)
        {
            StatusText.Text = "Selecione um certificado na lista para rejeitá-lo.";
            return;
        }

        try
        {
            bool ok = await _securityManager.RejectTrustedCertificateAsync(selected.Thumbprint);
            await RefreshAllAsync();
            StatusText.Text = ok
                ? $"✖ Certificado '{selected.Subject}' movido para Servidores Rejeitados (Rejected)."
                : "O certificado selecionado já está na lista de rejeitados ou não pôde ser movido.";
            _logger.LogInformation("Certificado de servidor '{Subject}' ({Thumb}) marcado como Rejeitado (Rejected).", selected.Subject, selected.Thumbprint);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao rejeitar certificado: {ex.Message}";
            _logger.LogError(ex, "Erro ao mover certificado para Rejeitados");
        }
    }

    private async void ImportServerCert_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = "Importar Certificado de Servidor OPC UA (.der, .cer, .crt)",
            Filter = "Certificados de Servidor (*.der;*.cer;*.crt;*.pem)|*.der;*.cer;*.crt;*.pem|Todos os arquivos (*.*)|*.*",
            Multiselect = true
        };
        if (ofd.ShowDialog(this) != true) return;

        try
        {
            int count = 0;
            foreach (var file in ofd.FileNames)
            {
                var info = await _securityManager.ImportTrustedServerCertificateAsync(file);
                count++;
                _logger.LogInformation("Certificado do servidor '{Subject}' ({Thumb}) importado para Confiáveis.", info.Subject, info.Thumbprint);
            }

            await RefreshAllAsync();
            StatusText.Text = $"{count} certificado(s) de servidor importado(s) para a lista de Servidores Confiáveis (Trusted).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Falha ao importar certificado do servidor: {ex.Message}";
            _logger.LogError(ex, "Falha ao importar certificado do servidor");
        }
    }

    private async void ExportSelectedServerCert_Click(object sender, RoutedEventArgs e)
    {
        if (ServersGrid.SelectedItem is not CertificateInfo selected)
        {
            StatusText.Text = "Selecione um certificado de servidor para exportar.";
            return;
        }

        var sfd = new SaveFileDialog
        {
            Title = "Exportar Certificado de Servidor (.der)",
            FileName = $"Server-{selected.Thumbprint[..Math.Min(8, selected.Thumbprint.Length)]}.der",
            Filter = "Certificado DER (*.der;*.cer)|*.der;*.cer|Todos os arquivos (*.*)|*.*"
        };
        if (sfd.ShowDialog(this) != true) return;

        try
        {
            bool fromTrusted = selected.TrustStatus == CertificateTrustStatus.Trusted;
            await _securityManager.ExportServerCertificateDerAsync(selected.Thumbprint, fromTrusted, sfd.FileName);
            StatusText.Text = $"Certificado de servidor exportado para: {sfd.FileName}";
            _logger.LogInformation("Certificado de servidor '{Subject}' exportado para {Path}", selected.Subject, sfd.FileName);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Falha ao exportar certificado de servidor: {ex.Message}";
            _logger.LogError(ex, "Falha ao exportar certificado de servidor");
        }
    }

    private async void RemoveSelectedServerCert_Click(object sender, RoutedEventArgs e)
    {
        if (ServersGrid.SelectedItem is not CertificateInfo selected)
        {
            StatusText.Text = "Selecione um certificado de servidor na tabela para excluí-lo.";
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Deseja excluir permanentemente o certificado de servidor abaixo?\n\n" +
            $"Sujeito: {selected.Subject}\n" +
            $"Thumbprint: {selected.Thumbprint}\n" +
            $"Status atual: {selected.TrustStatusText}",
            "Excluir Certificado de Servidor",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            bool fromTrusted = selected.TrustStatus == CertificateTrustStatus.Trusted;
            await _securityManager.RemoveServerCertificateAsync(selected.Thumbprint, fromTrusted);
            await RefreshAllAsync();
            StatusText.Text = $"Certificado '{selected.Subject}' excluído.";
            _logger.LogInformation("Certificado de servidor '{Subject}' ({Thumb}) excluído.", selected.Subject, selected.Thumbprint);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao excluir certificado: {ex.Message}";
            _logger.LogError(ex, "Erro ao excluir certificado de servidor");
        }
    }

    // =========================================================================
    // ABA 2: DESCOBERTA DE CERTIFICADOS DE SERVIDORES (DISCOVERY)
    // =========================================================================

    private async void DiscoverServerCerts_Click(object sender, RoutedEventArgs e)
    {
        string url = DiscoveryUrlCombo.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusText.Text = "Informe a URL do servidor OPC UA (ex.: opc.tcp://localhost:4840).";
            return;
        }

        try
        {
            DiscoverBtn.IsEnabled = false;
            StatusText.Text = $"Consultando endpoints e certificados em {url}...";

            var discovered = await _securityManager.DiscoverServerCertificatesAsync(url, timeoutMs: 10_000);
            _discoveredCerts = discovered.ToList();
            DiscoveredCertsGrid.ItemsSource = _discoveredCerts;

            if (_discoveredCerts.Count > 0)
            {
                DiscoveredCertsGrid.SelectedIndex = 0;
                StatusText.Text = $"{_discoveredCerts.Count} certificado(s) de servidor encontrado(s) em {url}. Selecione e clique em Confiar ou Rejeitar.";
                _logger.LogInformation("Descoberta de certificados em {Url}: {Count} certificado(s) obtido(s).", url, _discoveredCerts.Count);
            }
            else
            {
                StatusText.Text = $"O servidor em {url} respondeu, mas não apresentou nenhum certificado X.509 nos endpoints retornados.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Falha ao descobrir certificados em '{url}': {ex.Message}";
            _logger.LogWarning(ex, "Falha na descoberta de certificados em {Url}", url);
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
        {
            StatusText.Text = "Selecione um certificado descoberto na tabela acima.";
            return;
        }

        try
        {
            var info = await _securityManager.TrustServerCertificateAsync(item.RawData);
            await RefreshAllAsync();
            StatusText.Text = $"✔ Certificado descoberto '{info.Subject}' adicionado à lista de Servidores Confiáveis (Trusted).";
            _logger.LogInformation("Certificado descoberto '{Subject}' ({Thumb}) salvo em Confiáveis.", info.Subject, info.Thumbprint);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao confiar no certificado descoberto: {ex.Message}";
            _logger.LogError(ex, "Erro ao confiar no certificado descoberto");
        }
    }

    private async void RejectDiscoveredCert_Click(object sender, RoutedEventArgs e)
    {
        if (DiscoveredCertsGrid.SelectedItem is not DiscoveredServerCertificate item)
        {
            StatusText.Text = "Selecione um certificado descoberto na tabela acima.";
            return;
        }

        try
        {
            var info = await _securityManager.RejectServerCertificateAsync(item.RawData);
            await RefreshAllAsync();
            StatusText.Text = $"✖ Certificado descoberto '{info.Subject}' movido para a lista de Servidores Rejeitados (Rejected).";
            _logger.LogInformation("Certificado descoberto '{Subject}' ({Thumb}) salvo em Rejeitados.", info.Subject, info.Thumbprint);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao rejeitar certificado descoberto: {ex.Message}";
            _logger.LogError(ex, "Erro ao rejeitar certificado descoberto");
        }
    }

    private async void ExportDiscoveredCert_Click(object sender, RoutedEventArgs e)
    {
        if (DiscoveredCertsGrid.SelectedItem is not DiscoveredServerCertificate item)
        {
            StatusText.Text = "Selecione um certificado descoberto para exportar.";
            return;
        }

        var sfd = new SaveFileDialog
        {
            Title = "Exportar Certificado Descoberto (.der)",
            FileName = $"DiscoveredServer-{item.Certificate.Thumbprint[..Math.Min(8, item.Certificate.Thumbprint.Length)]}.der",
            Filter = "Certificado DER (*.der;*.cer)|*.der;*.cer"
        };
        if (sfd.ShowDialog(this) != true) return;

        try
        {
            await File.WriteAllBytesAsync(sfd.FileName, item.RawData);
            StatusText.Text = $"Certificado descoberto exportado para: {sfd.FileName}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao exportar certificado descoberto: {ex.Message}";
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
            StatusText.Text = $"Validade ajustada para {years} ano(s): de {startDate:dd/MM/yyyy} até {startDate.AddYears(years):dd/MM/yyyy}.";
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
        {
            MessageBox.Show(
                this,
                "A data de expiração deve ser posterior à data de início da validade.",
                "Data de Validade Inválida",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        ushort keySize = 2048;
        if (GenKeySizeCombo.SelectedItem is ComboBoxItem { Tag: string ksStr } &&
            ushort.TryParse(ksStr, out ushort parsedKs))
        {
            keySize = parsedKs;
        }

        var confirm = MessageBox.Show(
            this,
            $"Deseja gerar e ativar um novo certificado de cliente OPC UA com os parâmetros abaixo?\n\n" +
            $"• Nome Comum (CN): {cn}\n" +
            $"• Organização (O): {org}\n" +
            $"• Tamanho da Chave: RSA {keySize} bits\n" +
            $"• Validade: {startDate:dd/MM/yyyy} até {endDate:dd/MM/yyyy}\n\n" +
            "Observação: se você já havia importado o certificado anterior manualmente em algum servidor OPC UA com segurança Sign/SignAndEncrypt, será necessário exportar o novo certificado (.der) e importá-lo no servidor.",
            "Gerar Novo Certificado de Cliente OPC UA",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            StatusText.Text = $"Gerando novo certificado de cliente RSA {keySize} bits (válido até {endDate:dd/MM/yyyy})...";
            var options = new ClientCertificateGenerationOptions(
                CommonName: cn,
                Organization: org,
                KeySize: keySize,
                NotBeforeUtc: DateTime.SpecifyKind(startDate, DateTimeKind.Local).ToUniversalTime(),
                NotAfterUtc: DateTime.SpecifyKind(endDate.AddHours(23).AddMinutes(59).AddSeconds(59), DateTimeKind.Local).ToUniversalTime());

            var info = await _securityManager.GenerateApplicationCertificateAsync(options);
            await RefreshAllAsync();

            StatusText.Text =
                $"✔ Novo certificado de cliente gerado! Thumbprint: {info.Thumbprint} | Validade: {info.NotBeforeLocal:dd/MM/yyyy} a {info.NotAfterLocal:dd/MM/yyyy}";
            _logger.LogInformation(
                "Novo certificado de cliente OPC UA gerado: Subject='{Subject}', Thumbprint={Thumb}, Validade={Start:yyyy-MM-dd}..{End:yyyy-MM-dd}, KeySize={KeySize}",
                info.Subject, info.Thumbprint, info.NotBeforeLocal, info.NotAfterLocal, info.KeySize);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Falha ao gerar novo certificado: {ex.Message}";
            _logger.LogError(ex, "Falha ao gerar certificado do cliente");
        }
    }

    private async void ExportClientDer_Click(object sender, RoutedEventArgs e)
    {
        var sfd = new SaveFileDialog
        {
            Title = "Exportar Certificado Público do Cliente (.der / .cer)",
            FileName = "DMapFactory-Client.der",
            Filter = "Certificado DER (*.der;*.cer)|*.der;*.cer|Todos os arquivos (*.*)|*.*"
        };
        if (sfd.ShowDialog(this) != true) return;

        try
        {
            await _securityManager.ExportApplicationCertificateDerAsync(sfd.FileName);
            StatusText.Text = $"Certificado público do cliente exportado com sucesso para: {sfd.FileName}";
            _logger.LogInformation("Certificado público do cliente exportado para {Path}", sfd.FileName);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Falha ao exportar certificado: {ex.Message}";
            _logger.LogError(ex, "Falha ao exportar certificado .der");
        }
    }

    private async void ExportClientPfx_Click(object sender, RoutedEventArgs e)
    {
        var sfd = new SaveFileDialog
        {
            Title = "Exportar Certificado do Cliente com Chave Privada (.pfx)",
            FileName = "DMapFactory-Client.pfx",
            Filter = "Arquivo PKCS#12 (*.pfx;*.p12)|*.pfx;*.p12"
        };
        if (sfd.ShowDialog(this) != true) return;

        try
        {
            await _securityManager.ExportApplicationCertificatePfxAsync(sfd.FileName, password: null);
            StatusText.Text = $"Certificado completo (.pfx) exportado para: {sfd.FileName}";
            _logger.LogInformation("Certificado .pfx do cliente exportado para {Path}", sfd.FileName);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Falha ao exportar .pfx: {ex.Message}";
            _logger.LogError(ex, "Falha ao exportar certificado .pfx");
        }
    }

    private async void ImportClientCert_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = "Importar Certificado do Cliente (.pfx, .p12, .der, .cer)",
            Filter = "Certificados (*.pfx;*.p12;*.der;*.cer;*.crt)|*.pfx;*.p12;*.der;*.cer;*.crt|Todos os arquivos (*.*)|*.*"
        };
        if (ofd.ShowDialog(this) != true) return;

        try
        {
            var info = await _securityManager.ImportApplicationCertificateAsync(ofd.FileName, password: null);
            await RefreshAllAsync();
            StatusText.Text = $"Certificado do cliente importado: {info.Subject} (Thumbprint: {info.Thumbprint})";
            _logger.LogInformation("Certificado do cliente importado de {Path} ({Thumbprint})", ofd.FileName, info.Thumbprint);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Falha ao importar certificado do cliente: {ex.Message}";
            _logger.LogError(ex, "Falha ao importar certificado do cliente");
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
            StatusText.Text = $"Não foi possível abrir a pasta PKI: {ex.Message}";
        }
    }
}
