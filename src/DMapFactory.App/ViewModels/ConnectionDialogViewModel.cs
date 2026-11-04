using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DMapFactory.Core.Models;
using DMapFactory.Core.Security;
using DMapFactory.Drivers.OpcUa;
using Opc.Ua;

namespace DMapFactory.App.ViewModels;

public sealed partial class ConnectionDialogViewModel : ObservableObject
{
    private readonly OpcUaSecurityManager _securityManager;

    public ConnectionDialogViewModel(OpcUaSecurityManager securityManager, ConnectionConfig? existing = null)
    {
        _securityManager = securityManager;

        if (existing is not null)
        {
            _id = existing.Id;
            _name = existing.Name;
            _endpointUrl = existing.EndpointUrl;
            _selectedPolicy = Policies.FirstOrDefault(p =>
                p.Uri == existing.SecurityPolicyUri || p.ShortName == existing.SecurityPolicyUri) ?? Policies[0];
            _selectedMode = existing.SecurityMode;
            _selectedAuth = existing.Authentication;
            _userName = existing.UserName ?? "";
            _password = SecretProtector.Unprotect(existing.ProtectedPassword) ?? "";
            _userCertificatePath = existing.UserCertificatePath ?? "";
        }
        else
        {
            _name = "PLC1";
            _endpointUrl = "opc.tcp://localhost:48400/DMapFactory/TestServer";
            _selectedPolicy = Policies[0];
            _selectedMode = MessageSecurity.None;
            _selectedAuth = UserAuthentication.Anonymous;
        }
    }

    private readonly int _id;

    public static IReadOnlyList<SecurityPolicyOption> Policies { get; } =
    [
        new("None", SecurityPolicies.None, false),
        new("Basic256Sha256", SecurityPolicies.Basic256Sha256, false),
        new("Aes128_Sha256_RsaOaep", SecurityPolicies.Aes128_Sha256_RsaOaep, false),
        new("Aes256_Sha256_RsaPss", SecurityPolicies.Aes256_Sha256_RsaPss, false),
        new("Basic256 ⚠", SecurityPolicies.Basic256, true),
        new("Basic128Rsa15 ⚠", SecurityPolicies.Basic128Rsa15, true),
    ];

    public static IReadOnlyList<MessageSecurity> SecurityModes { get; } =
        Enum.GetValues<MessageSecurity>();

    public static IReadOnlyList<UserAuthentication> AuthModes { get; } =
        Enum.GetValues<UserAuthentication>();

    public ObservableCollection<DiscoveredEndpoint> DiscoveredEndpoints { get; } = [];

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _endpointUrl;
    [ObservableProperty] private SecurityPolicyOption _selectedPolicy;
    [ObservableProperty] private MessageSecurity _selectedMode;
    [ObservableProperty] private UserAuthentication _selectedAuth;
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _userCertificatePath = "";
    [ObservableProperty] private DiscoveredEndpoint? _selectedDiscoveredEndpoint;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;

    public bool IsUserPasswordVisible => SelectedAuth == UserAuthentication.UserName;
    public bool IsCertificateVisible => SelectedAuth == UserAuthentication.Certificate;

    partial void OnSelectedAuthChanged(UserAuthentication value)
    {
        OnPropertyChanged(nameof(IsUserPasswordVisible));
        OnPropertyChanged(nameof(IsCertificateVisible));
    }

    partial void OnSelectedDiscoveredEndpointChanged(DiscoveredEndpoint? value)
    {
        if (value is null) return;
        EndpointUrl = value.EndpointUrl;
        SelectedPolicy = Policies.FirstOrDefault(p => p.Uri == value.SecurityPolicyUri) ?? Policies[0];
        SelectedMode = value.SecurityMode;
        if (value.SupportedAuthentications.Count > 0 && !value.SupportedAuthentications.Contains(SelectedAuth))
        {
            SelectedAuth = value.SupportedAuthentications[0];
        }
    }

    [RelayCommand]
    private async Task DiscoverAsync()
    {
        IsBusy = true;
        StatusText = "Consultando endpoints…";
        DiscoveredEndpoints.Clear();

        try
        {
            var list = await OpcUaSecurityManager.DiscoverEndpointsAsync(EndpointUrl);
            foreach (var ep in list)
            {
                DiscoveredEndpoints.Add(ep);
            }
            SelectedDiscoveredEndpoint = DiscoveredEndpoints.FirstOrDefault();
            StatusText = $"{list.Count} endpoint(s) encontrado(s).";
        }
        catch (Exception ex)
        {
            StatusText = $"Erro na descoberta: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsBusy = true;
        StatusText = "Testando conexão…";

        try
        {
            var cfg = BuildConfig(0);
            await using var driver = new OpcUaDriver(cfg, _securityManager);
            await driver.ConnectAsync();
            await driver.DisconnectAsync();
            StatusText = "✓ Conexão bem-sucedida!";
        }
        catch (Exception ex)
        {
            StatusText = $"✗ Falha: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public ConnectionConfig BuildConfig(int fallbackId) => new()
    {
        Id = _id > 0 ? _id : fallbackId,
        Name = string.IsNullOrWhiteSpace(Name) ? "OPCUA" : Name.Trim(),
        EndpointUrl = EndpointUrl.Trim(),
        SecurityPolicyUri = SelectedPolicy.Uri,
        SecurityMode = SelectedMode,
        Authentication = SelectedAuth,
        UserName = SelectedAuth == UserAuthentication.UserName ? UserName.Trim() : null,
        ProtectedPassword = SelectedAuth is UserAuthentication.UserName or UserAuthentication.Certificate
            ? SecretProtector.Protect(Password)
            : null,
        UserCertificatePath = SelectedAuth == UserAuthentication.Certificate ? UserCertificatePath.Trim() : null,
        Enabled = true
    };
}

public sealed record SecurityPolicyOption(string ShortName, string Uri, bool IsDeprecated);
