using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using DMapFactory.Localization;

namespace DMapFactory.App.Services;

/// <summary>
/// Constantes de identificação de tópicos para Ajuda Sensível ao Contexto (F1).
/// </summary>
public static class HelpTopicIds
{
    public const string Overview = "IDH_OVERVIEW";
    public const string OpcConnection = "IDH_OPC_CONNECTION";
    public const string TagBrowser = "IDH_TAG_BROWSER";
    public const string Certificates = "IDH_CERTIFICATES";
    public const string Spreadsheet = "IDH_SPREADSHEET";
    public const string Chart = "IDH_CHART";
    public const string PenPanel = "IDH_PEN_PANEL";
    public const string Marker = "IDH_MARKER";
    public const string Shortcuts = "IDH_SHORTCUTS";
    public const string LogPanel = "IDH_LOG_PANEL";
}

/// <summary>
/// Propriedade anexada para associar controles e painéis XAML a tópicos de ajuda específicos.
/// </summary>
public static class HelpProvider
{
    public static readonly DependencyProperty TopicIdProperty = DependencyProperty.RegisterAttached(
        "TopicId",
        typeof(string),
        typeof(HelpProvider),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public static string? GetTopicId(DependencyObject element) =>
        (string?)element.GetValue(TopicIdProperty);

    public static void SetTopicId(DependencyObject element, string? value) =>
        element.SetValue(TopicIdProperty, value);

    /// <summary>
    /// Percorre a árvore visual para encontrar o tópico de ajuda mais específico associado ao elemento ou ancestrais.
    /// </summary>
    public static string? FindTopicId(DependencyObject? element)
    {
        while (element != null)
        {
            var topic = GetTopicId(element);
            if (!string.IsNullOrWhiteSpace(topic))
                return topic;

            element = (element is Visual || element is System.Windows.Media.Media3D.Visual3D)
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return null;
    }
}

/// <summary>
/// Serviço de gerenciamento e exibição da documentação de ajuda contextual (HTML5 Offline).
/// </summary>
public static class HelpService
{
    /// <summary>
    /// Abre a documentação de ajuda no navegador padrão na página/seção contextual correspondente.
    /// </summary>
    /// <param name="topicId">Identificador do tópico (ver <see cref="HelpTopicIds"/>) ou nulo para a Visão Geral.</param>
    public static void OpenHelp(string? topicId = null)
    {
        topicId ??= HelpTopicIds.Overview;

        var culture = Loc.Instance.Culture.Name;
        var langFolder = culture.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "pt-BR";

        var (fileName, anchor) = ResolveTopicFile(topicId, langFolder);

        var helpBaseDir = FindHelpDirectory();
        if (helpBaseDir == null)
        {
            MessageBox.Show(
                "A pasta de documentação de ajuda não foi encontrada.",
                "Ajuda - DMapFactory",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var langDir = Path.Combine(helpBaseDir, langFolder);
        if (!Directory.Exists(langDir))
            langDir = Path.Combine(helpBaseDir, "pt-BR"); // fallback

        var targetFile = Path.Combine(langDir, fileName);
        if (!File.Exists(targetFile))
            targetFile = Path.Combine(langDir, "index.html");

        try
        {
            var fileUri = new Uri(targetFile).AbsoluteUri;
            if (!string.IsNullOrEmpty(anchor))
                fileUri += "#" + anchor;

            Process.Start(new ProcessStartInfo
            {
                FileName = fileUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Não foi possível abrir o navegador padrão para exibir a ajuda:\n{ex.Message}",
                "Ajuda - DMapFactory",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static (string fileName, string? anchor) ResolveTopicFile(string topicId, string lang)
    {
        var isEn = lang.Equals("en-US", StringComparison.OrdinalIgnoreCase);

        return topicId switch
        {
            HelpTopicIds.Overview => ("index.html", null),
            HelpTopicIds.OpcConnection => (isEn ? "opc-connection.html" : "conexao-opc.html", null),
            HelpTopicIds.TagBrowser => (isEn ? "opc-connection.html" : "conexao-opc.html", isEn ? "tag-browser" : "navegador-tags"),
            HelpTopicIds.Certificates => (isEn ? "certificates.html" : "certificados.html", null),
            HelpTopicIds.Spreadsheet => (isEn ? "spreadsheet.html" : "planilha.html", null),
            HelpTopicIds.Chart => (isEn ? "chart.html" : "grafico.html", null),
            HelpTopicIds.PenPanel => (isEn ? "chart.html" : "grafico.html", isEn ? "pen-panel" : "painel-penas"),
            HelpTopicIds.Marker => (isEn ? "marker.html" : "marcador.html", null),
            HelpTopicIds.Shortcuts => (isEn ? "shortcuts.html" : "atalhos.html", null),
            HelpTopicIds.LogPanel => (isEn ? "shortcuts.html" : "atalhos.html", isEn ? "diagnostics" : "diagnosticos"),
            _ => ("index.html", null)
        };
    }

    private static string? FindHelpDirectory()
    {
        // 1. Diretório do executável compilado (bin\...\Help)
        var binHelp = Path.Combine(AppContext.BaseDirectory, "Help");
        if (Directory.Exists(binHelp))
            return binHelp;

        // 2. Fallback de desenvolvimento (procurando a partir da árvore de diretórios do projeto)
        var current = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && !string.IsNullOrEmpty(current); i++)
        {
            var candidate = Path.Combine(current, "src", "DMapFactory.App", "Help");
            if (Directory.Exists(candidate))
                return candidate;

            candidate = Path.Combine(current, "Help");
            if (Directory.Exists(candidate))
                return candidate;

            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        return null;
    }
}
