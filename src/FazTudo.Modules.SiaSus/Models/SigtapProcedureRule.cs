namespace FazTudo.Modules.SiaSus.Models;

/// <summary>Procedimento secundário previsto para um principal (rl_procedimento_compativel).</summary>
/// <param name="Type">TP_COMPATIBILIDADE; "5" = Obrigatória (APAC principal x APAC secundário).</param>
public sealed record SigtapCompatible(string Code, int Quantity, string Type)
{
    public const string Obligatory = "5";
    public bool IsObligatory => Type == Obligatory;
}

public sealed class SigtapProcedureRule
{
    /// <summary>Secundários previstos no SIGTAP (os de tipo 5 são obrigatórios na APAC).</summary>
    public IReadOnlyList<SigtapCompatible> Compatibles { get; init; } = [];

    /// <summary>Códigos de tb_regra_condicionada vinculados ao procedimento.</summary>
    public IReadOnlyList<string> ConditionedRules { get; init; } = [];

    /// <summary>Códigos de tb_detalhe (ex.: "APAC com validade fixa de 2 competências").</summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    public IEnumerable<SigtapCompatible> ObligatorySecondaries => Compatibles.Where(c => c.IsObligatory);

    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string SexRestriction { get; init; } // I = Indiferente, F = Feminino, M = Masculino
    public required int MinAgeMonths { get; init; }
    public required int MaxAgeMonths { get; init; } // 9999 = Sem limite superior
    public required IReadOnlyList<string> AllowedInstruments { get; init; } // BPA-C, BPA-I, APAC, RAAS
    public required IReadOnlyList<string> AllowedCbos { get; init; } // "*" = Qualquer CBO habilitado, ou lista exata
    public required bool RequiresCid { get; init; }
    public string RequiredServiceClassification { get; init; } = string.Empty;

    /// <summary>CIDs-10 (4 caracteres, sem ponto) aceitos como principal. Vazio = sem restrição de CID na tabela.</summary>
    public IReadOnlyList<string> AllowedCids { get; init; } = [];

    /// <summary>QT_MAXIMA_EXECUCAO do SIGTAP (0 = não informado).</summary>
    public int MaxQuantity { get; init; }
    public required decimal UnitValue { get; init; }
    public required string Financing { get; init; } // MAC, FAEC, PAB

    public string FormattedCode => Code.Length == 10
        ? $"{Code[..2]}.{Code[2..4]}.{Code[4..6]}.{Code[6..9]}-{Code[9]}"
        : Code;

    public string InstrumentsDisplay => string.Join(", ", AllowedInstruments);

    public string SexDisplay => SexRestriction switch
    {
        "F" => "Feminino",
        "M" => "Masculino",
        _ => "Ambos"
    };

    public string AgeRangeDisplay
    {
        get
        {
            var min = MinAgeMonths <= 0 ? "0m" : FormatMonths(MinAgeMonths);
            var max = MaxAgeMonths >= 1560 ? "130a" : FormatMonths(MaxAgeMonths);
            return $"{min} até {max}";
        }
    }

    public string CboSummary => AllowedCbos.Contains("*")
        ? "Amplo (Multiprofissional)"
        : string.Join(", ", AllowedCbos.Take(5)) + (AllowedCbos.Count > 5 ? $" (+{AllowedCbos.Count - 5})" : string.Empty);

    private static string FormatMonths(int months)
    {
        if (months < 12)
        {
            return $"{months}m";
        }

        var years = months / 12;
        var rem = months % 12;
        return rem == 0 ? $"{years}a" : $"{years}a {rem}m";
    }
}
