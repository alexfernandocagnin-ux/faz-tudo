using System.Text;
using System.Text.RegularExpressions;

namespace FazTudo.Modules.SiaSus.Reports;

/// <summary>Um procedimento lançado na APAC, como aparece no espelho.</summary>
public sealed record MirrorItem(string Seq, string ProcedureCode, string Cbo, int Quantity, string ThirdPartyCnes, string Situation)
{
    public bool HasError => Situation.Length > 0 && !Situation.Contains("SEM ERRO", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Uma APAC do relatório "Situação de Produção de APAC" (R0330T — o espelho da APAC).
/// Os dados do paciente que o relatório traz NÃO são lidos nem guardados.
/// </summary>
public sealed class ApacMirror
{
    public required string Cnes { get; init; }
    public required string CnesName { get; init; }
    /// <summary>13 dígitos, sem traço (igual ao R0350).</summary>
    public required string Number { get; init; }
    public required string Validity { get; init; }
    public required string ApacType { get; init; }
    /// <summary>9 dígitos, como no relatório.</summary>
    public string Principal { get; set; } = string.Empty;
    public string PrincipalName { get; set; } = string.Empty;
    public string Processing { get; set; } = string.Empty;
    public string Production { get; set; } = string.Empty;
    public string ExitReason { get; set; } = string.Empty;
    public List<MirrorItem> Items { get; } = [];

    private static readonly Regex Escape = new(@"\x1B[^A-Z@]*[A-Z@]", RegexOptions.Compiled);
    private static readonly Regex Estab = new(@"^Estab\.Exec\.\s*:\s*(\d{7})\s*-?\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex Apac = new(@"^APAC:\s*([\d-]+)\s+Validade:\s*(.*?)\s+Tipo:\s*(\S+)", RegexOptions.Compiled);
    private static readonly Regex Principal_ = new(@"^Proced\.Princ:\s*(\d{9})\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex Processing_ = new(@"^Processamento:\s*(\S+)\s+Producao:\s*(\S+)", RegexOptions.Compiled);
    private static readonly Regex Item = new(@"^\s*(\d{2})\s+(\d{9})-(\d)\s+(\d{6})?\s+(\d+)\s+(.*?)\s*$", RegexOptions.Compiled);
    private static readonly Regex SevenDigits = new(@"^\d{7}$", RegexOptions.Compiled);

    public static bool LooksLikeApacMirror(string text) =>
        text.Contains("SITUACAO DE PRODUCAO DE APAC", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<ApacMirror> Load(string path) => Parse(File.ReadAllText(path, Encoding.Latin1));

    public static IReadOnlyList<ApacMirror> Parse(string text)
    {
        var lines = Escape.Replace(text, string.Empty).Replace("\f", "\n").Replace("\r", string.Empty).Split('\n');
        var result = new List<ApacMirror>();
        var cnes = string.Empty;
        var cnesName = string.Empty;
        ApacMirror? current = null;
        var inItems = false;

        foreach (var line in lines)
        {
            var s = line.Trim();
            var m = Estab.Match(s);
            if (m.Success)
            {
                cnes = m.Groups[1].Value;
                cnesName = m.Groups[2].Value.Trim();
                continue;
            }

            m = Apac.Match(s);
            if (m.Success)
            {
                current = new ApacMirror
                {
                    Cnes = cnes,
                    CnesName = cnesName,
                    Number = m.Groups[1].Value.Replace("-", string.Empty),
                    Validity = m.Groups[2].Value.Trim(),
                    ApacType = m.Groups[3].Value
                };
                result.Add(current);
                inItems = false;
                continue;
            }

            if (current is null)
            {
                continue;
            }

            if ((m = Principal_.Match(s)).Success)
            {
                current.Principal = m.Groups[1].Value;
                current.PrincipalName = m.Groups[2].Value.Trim(' ', '-');
            }
            else if ((m = Processing_.Match(s)).Success)
            {
                current.Processing = m.Groups[1].Value;
                current.Production = m.Groups[2].Value;
            }
            else if (s.StartsWith("Mot.Saida", StringComparison.Ordinal))
            {
                var value = s.Contains(':') ? s[(s.IndexOf(':') + 1)..] : string.Empty;
                var dt = value.IndexOf("Dt.", StringComparison.Ordinal);
                current.ExitReason = (dt >= 0 ? value[..dt] : value).Trim();
            }
            else if (s.StartsWith("SQ PROC", StringComparison.Ordinal))
            {
                inItems = true;
            }
            else if (inItems)
            {
                m = Item.Match(line);
                if (m.Success)
                {
                    var rest = m.Groups[6].Value;
                    var third = string.Empty;
                    var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0 && SevenDigits.IsMatch(parts[0]))
                    {
                        third = parts[0];
                        rest = parts.Length > 1 ? parts[1] : string.Empty;
                    }

                    current.Items.Add(new MirrorItem(m.Groups[1].Value, m.Groups[2].Value + m.Groups[3].Value, m.Groups[4].Value,
                        int.Parse(m.Groups[5].Value), third, rest.Trim()));
                }
                else if (s.StartsWith("---", StringComparison.Ordinal))
                {
                    inItems = false;
                }
            }
        }

        return result;
    }
}
