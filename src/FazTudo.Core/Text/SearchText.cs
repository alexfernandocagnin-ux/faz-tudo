using System.Globalization;
using System.Text;

namespace FazTudo.Core.Text;

/// <summary>
/// Normalização para busca: ignora acentos e maiúsculas ("atencao" encontra "Atenção").
/// </summary>
public static class SearchText
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    public static string[] Terms(string? query) =>
        Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
