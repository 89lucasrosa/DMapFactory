using System.Globalization;
using DMapFactory.Core.Models;
using DMapFactory.Localization;

namespace DMapFactory.Core.Tests;

public sealed class FoundationTests
{
    [Theory]
    [InlineData(0x0000_0000u, QualityClass.Good)]
    [InlineData(0x4000_0000u, QualityClass.Uncertain)]
    [InlineData(0x8000_0000u, QualityClass.Bad)]
    [InlineData(0x8034_0000u, QualityClass.Bad)] // BadNodeIdUnknown
    public void Sample_Quality_DerivedFromStatusCodeTopBits(uint statusCode, QualityClass expected)
    {
        var sample = new Sample(1, DateTime.UtcNow, DateTime.UtcNow, TimestampOrigin.Source, 42.0, statusCode);
        Assert.Equal(expected, sample.Quality);
    }

    [Theory]
    [InlineData("pt-BR", "_Arquivo", "Ficar on-line")]
    [InlineData("en-US", "_File", "Go online")]
    [InlineData("es", "_Archivo", "Conectar en línea")]
    public void Localization_SwitchesBetweenAllThreeSupportedLanguages(
        string cultureCode, string expectedFileMenu, string expectedOnlineButton)
    {
        Loc.Instance.Culture = CultureInfo.GetCultureInfo(cultureCode);

        Assert.Equal(expectedFileMenu, Loc.Instance["Menu_File"]);
        Assert.Equal(expectedOnlineButton, Loc.Instance["Toolbar_GoOnline"]);
        Assert.DoesNotContain("[", Loc.Instance["App_Title"]);
    }

    [Fact]
    public void SecretProtector_RoundTripsPasswordWithoutPlaintextExposure()
    {
        const string secret = "P@ssw0rd!Industrial#2026";
        string? protectedText = DMapFactory.Core.Security.SecretProtector.Protect(secret);

        Assert.NotNull(protectedText);
        Assert.DoesNotContain(secret, protectedText);
        Assert.Equal(secret, DMapFactory.Core.Security.SecretProtector.Unprotect(protectedText));
    }
}


