namespace FazTudo.Modules.SiaSus.Services;

/// <summary>
/// Validadores oficiais de documentos e códigos do ecossistema DATASUS / SIA-SUS.
/// </summary>
public static class SusValidators
{
    /// <summary>
    /// Valida o Cartão Nacional de Saúde (CNS - 15 dígitos) pela rotina oficial Módulo 11 do DATASUS
    /// (faixas definitivas iniciadas em 1 ou 2 e faixas iniciadas em 7, 8 ou 9).
    /// </summary>
    public static bool IsValidCns(string? rawCns)
    {
        if (string.IsNullOrWhiteSpace(rawCns))
        {
            return false;
        }

        var digits = new string(rawCns.Where(char.IsDigit).ToArray());
        if (digits.Length != 15 || digits.Distinct().Count() == 1)
        {
            return false;
        }

        var first = digits[0];
        if (first is '1' or '2')
        {
            var pis = digits[..11];
            var sum = 0;
            for (var i = 0; i < 11; i++)
            {
                sum += (pis[i] - '0') * (15 - i);
            }

            var remainder = sum % 11;
            var dv = 11 - remainder;
            if (dv == 11)
            {
                dv = 0;
            }

            string expected;
            if (dv == 10)
            {
                sum += 2;
                remainder = sum % 11;
                dv = 11 - remainder;
                expected = $"{pis}001{dv}";
            }
            else
            {
                expected = $"{pis}000{dv}";
            }

            return digits == expected;
        }

        if (first is '7' or '8' or '9')
        {
            var sum = 0;
            for (var i = 0; i < 15; i++)
            {
                sum += (digits[i] - '0') * (15 - i);
            }

            return sum % 11 == 0;
        }

        return false;
    }

    public static bool IsValidCpf(string? rawCpf)
    {
        if (string.IsNullOrWhiteSpace(rawCpf))
        {
            return false;
        }

        var digits = new string(rawCpf.Where(char.IsDigit).ToArray());
        if (digits.Length != 11 || digits.Distinct().Count() == 1)
        {
            return false;
        }

        var sum1 = 0;
        for (var i = 0; i < 9; i++)
        {
            sum1 += (digits[i] - '0') * (10 - i);
        }

        var rem1 = (sum1 * 10) % 11;
        if (rem1 == 10) rem1 = 0;
        if (rem1 != digits[9] - '0') return false;

        var sum2 = 0;
        for (var i = 0; i < 10; i++)
        {
            sum2 += (digits[i] - '0') * (11 - i);
        }

        var rem2 = (sum2 * 10) % 11;
        if (rem2 == 10) rem2 = 0;
        return rem2 == digits[10] - '0';
    }

    public static string NormalizeProcedureCode(string? rawCode)
    {
        if (string.IsNullOrWhiteSpace(rawCode))
        {
            return string.Empty;
        }

        return new string(rawCode.Where(char.IsDigit).ToArray());
    }
}
