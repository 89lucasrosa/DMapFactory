using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace DMapFactory.Localization;

/// <summary>
/// Serviço de localização com troca de idioma em tempo de execução (RF-UI-01).
/// Uso em XAML: <c>Text="{Binding [Menu_File], Source={x:Static loc:Loc.Instance}}"</c>.
/// Ao trocar <see cref="Culture"/>, todas as ligações com o indexador são atualizadas.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources =
        new("DMapFactory.Localization.Strings", typeof(Loc).Assembly);

    public static Loc Instance { get; } = new();

    /// <summary>Idiomas suportados (código, nome nativo).</summary>
    public static IReadOnlyList<LanguageOption> SupportedLanguages { get; } =
    [
        new("pt-BR", "Português (Brasil)"),
        new("en-US", "English (US)"),
        new("es", "Español"),
    ];

    private CultureInfo _culture = CultureInfo.GetCultureInfo("pt-BR");

    private Loc() { }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CultureInfo Culture
    {
        get => _culture;
        set
        {
            if (Equals(_culture, value)) return;
            _culture = value;
            CultureInfo.CurrentUICulture = value;
            CultureInfo.DefaultThreadCurrentUICulture = value;
            // "Item[]" notifica todas as ligações que usam o indexador.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        }
    }

    /// <summary>Retorna o texto traduzido; se a chave não existir, retorna "[chave]" para facilitar a revisão.</summary>
    public string this[string key] => Resources.GetString(key, _culture) ?? $"[{key}]";

    /// <summary>Texto traduzido com formatação (string.Format).</summary>
    public string Format(string key, params object?[] args) =>
        string.Format(_culture, this[key], args);
}

public sealed record LanguageOption(string Code, string NativeName);
