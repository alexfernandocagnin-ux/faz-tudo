using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FazTudo.Core.Text;
using FazTudo.Modules.SiaSus.Models;
using FazTudo.Modules.SiaSus.Services;

namespace FazTudo.Modules.SiaSus.Catalog;

/// <summary>
/// Repositório de regras SIGTAP. Vem com uma pequena base de EXEMPLO (valores e CBOs ilustrativos,
/// não use para faturar) e importa a pasta oficial mensal (.txt) do SIGTAP/DATASUS, que passa a valer sozinha.
/// </summary>
public sealed class SigtapRepository
{
    private Dictionary<string, SigtapProcedureRule> _procedures = new(StringComparer.OrdinalIgnoreCase);

    public string LoadedSourceLabel { get; private set; } = "Base de exemplo embarcada (importe a tabela oficial do SIGTAP)";

    /// <summary>true depois de importar a tabela oficial; até lá só existe a base de exemplo.</summary>
    public bool IsOfficialTableLoaded { get; private set; }

    public SigtapRepository()
    {
        foreach (var seed in GetBuiltInSeed())
        {
            _procedures[seed.Code] = seed;
        }
    }

    public IReadOnlyCollection<SigtapProcedureRule> AllProcedures => _procedures.Values;

    /// <summary>
    /// Busca pelo código de 10 dígitos. Aceita também os 9 dígitos sem o verificador,
    /// como aparecem nos relatórios de APAC do SIA (ex.: "030402014").
    /// </summary>
    public SigtapProcedureRule? FindByCode(string? rawCode)
    {
        var normalized = SusValidators.NormalizeProcedureCode(rawCode);
        if (_procedures.TryGetValue(normalized, out var rule))
        {
            return rule;
        }

        if (normalized.Length == 9)
        {
            return _procedures.Values.FirstOrDefault(p => p.Code.StartsWith(normalized, StringComparison.Ordinal));
        }

        return null;
    }

    public IReadOnlyList<SigtapProcedureRule> Search(string? query, string? instrumentFilter = null)
    {
        IEnumerable<SigtapProcedureRule> source = _procedures.Values.OrderBy(p => p.Code);

        if (!string.IsNullOrWhiteSpace(instrumentFilter) && instrumentFilter != "Todos")
        {
            source = source.Where(p => p.AllowedInstruments.Any(i => i.Equals(instrumentFilter, StringComparison.OrdinalIgnoreCase)));
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return source.Take(250).ToList();
        }

        var normalizedDigits = SusValidators.NormalizeProcedureCode(query);
        var isCodeQuery = normalizedDigits.Length >= 4 && query.All(c => char.IsDigit(c) || c is '.' or '-' or ' ');
        var terms = SearchText.Terms(query);

        return source
            .Where(p => isCodeQuery
                ? p.Code.Contains(normalizedDigits, StringComparison.Ordinal)
                : terms.All(t =>
                    SearchText.Normalize(p.Name).Contains(t) ||
                    SearchText.Normalize(p.Financing).Contains(t) ||
                    SearchText.Normalize(p.RequiredServiceClassification).Contains(t) ||
                    p.AllowedCbos.Any(c => c.Contains(t, StringComparison.Ordinal))))
            .Take(250)
            .ToList();
    }

    // Layouts padrão do SIGTAP (usados só se o "<tabela>_layout.txt" não vier na pasta).
    private static readonly (string Name, int Length)[] DefaultProcedimentoLayout =
    [
        ("CO_PROCEDIMENTO", 10), ("NO_PROCEDIMENTO", 250), ("TP_COMPLEXIDADE", 1), ("TP_SEXO", 1),
        ("QT_MAXIMA_EXECUCAO", 4), ("QT_DIAS_PERMANENCIA", 4), ("QT_PONTOS", 4),
        ("VL_IDADE_MINIMA", 4), ("VL_IDADE_MAXIMA", 4),
        ("VL_SH", 12), ("VL_SA", 12), ("VL_SP", 12),
        ("CO_FINANCIAMENTO", 2), ("CO_RUBRICA", 6), ("QT_TEMPO_PERMANENCIA", 4), ("DT_COMPETENCIA", 6)
    ];

    private static readonly (string Name, int Length)[] DefaultOcupacaoLayout =
        [("CO_PROCEDIMENTO", 10), ("CO_OCUPACAO", 6), ("DT_COMPETENCIA", 6)];

    private static readonly (string Name, int Length)[] DefaultRegistroLayout =
        [("CO_PROCEDIMENTO", 10), ("CO_REGISTRO", 2), ("DT_COMPETENCIA", 6)];

    private static readonly (string Name, int Length)[] DefaultCidLayout =
        [("CO_PROCEDIMENTO", 10), ("CO_CID", 4), ("ST_PRINCIPAL", 1), ("DT_COMPETENCIA", 6)];

    /// <summary>
    /// Importa o SIGTAP a partir do zip baixado do DATASUS (TabelaUnificada_AAAAMM_*.zip) ou da pasta já descompactada.
    /// </summary>
    public int ImportOfficialSigtap(string zipOrFolderPath)
    {
        if (Directory.Exists(zipOrFolderPath))
        {
            return ImportOfficialSigtapFolder(zipOrFolderPath);
        }

        if (!File.Exists(zipOrFolderPath))
        {
            throw new FileNotFoundException("Arquivo ou pasta não encontrado.", zipOrFolderPath);
        }

        if (!zipOrFolderPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Escolha o arquivo .zip da Tabela Unificada do SIGTAP ou a pasta descompactada.");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FazTudo-sigtap-" + Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(zipOrFolderPath, tempDir);
            var procFile = Directory.EnumerateFiles(tempDir, "tb_procedimento.txt", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidDataException("O zip não contém 'tb_procedimento.txt'. Confira se é a Tabela Unificada do SIGTAP.");
            return ImportOfficialSigtapFolder(Path.GetDirectoryName(procFile)!);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
                // é temporário; o Windows limpa depois
            }
        }
    }

    /// <summary>
    /// Importa uma pasta descompactada oficial do SIGTAP/DATASUS (tb_procedimento.txt, rl_procedimento_ocupacao.txt, rl_procedimento_registro.txt).
    /// Layout posicional ISO-8859-1 (Latin1); as posições são lidas dos arquivos "*_layout.txt" do próprio zip quando presentes.
    /// </summary>
    public int ImportOfficialSigtapFolder(string folderPath)
    {
        var procFile = Path.Combine(folderPath, "tb_procedimento.txt");
        if (!File.Exists(procFile))
        {
            throw new FileNotFoundException("O arquivo 'tb_procedimento.txt' não foi encontrado na pasta selecionada.", procFile);
        }

        var latin1 = Encoding.Latin1;

        var cboMap = ReadRelation(folderPath, "rl_procedimento_ocupacao", DefaultOcupacaoLayout, "CO_OCUPACAO", latin1, code => code);
        var regMap = ReadRelation(folderPath, "rl_procedimento_registro", DefaultRegistroLayout, "CO_REGISTRO", latin1, MapRegistro);
        // Só CIDs que podem ser principal (ST_PRINCIPAL = S); se a coluna não existir, aceita todos.
        var cidMap = ReadRelation(folderPath, "rl_procedimento_cid", DefaultCidLayout, "CO_CID", latin1, NormalizeCid,
            (layout, line) => layout.Read(line, "ST_PRINCIPAL") is "" or "S");

        var layout = SigtapLayout.Load(folderPath, "tb_procedimento", DefaultProcedimentoLayout, latin1);
        var imported = new Dictionary<string, SigtapProcedureRule>(StringComparer.OrdinalIgnoreCase);
        var competenciaDetected = string.Empty;

        foreach (var line in File.ReadLines(procFile, latin1))
        {
            var code = layout.Read(line, "CO_PROCEDIMENTO");
            if (code.Length != 10 || !code.All(char.IsDigit))
            {
                continue; // linha vazia/corrompida
            }

            var name = layout.Read(line, "NO_PROCEDIMENTO");
            var sex = layout.Read(line, "TP_SEXO").ToUpperInvariant();
            int.TryParse(layout.Read(line, "VL_IDADE_MINIMA"), out var minAgeMonths);
            var hasMaxAge = int.TryParse(layout.Read(line, "VL_IDADE_MAXIMA"), out var maxAgeMonths);
            long.TryParse(layout.Read(line, "VL_SA"), out var vlSaCents);
            int.TryParse(layout.Read(line, "QT_MAXIMA_EXECUCAO"), out var maxQuantity);
            var financingCode = layout.Read(line, "CO_FINANCIAMENTO");

            if (string.IsNullOrEmpty(competenciaDetected))
            {
                var comp = layout.Read(line, "DT_COMPETENCIA");
                if (comp.Length == 6 && comp.All(char.IsDigit))
                {
                    competenciaDetected = comp;
                }
            }

            var instruments = regMap.TryGetValue(code, out var regs) && regs.Count > 0
                ? regs.OrderBy(x => x).ToList()
                : ["SIA"];

            var cbos = cboMap.TryGetValue(code, out var occs) && occs.Count > 0
                ? occs.OrderBy(x => x).ToList()
                : ["*"];

            List<string> cids = cidMap.TryGetValue(code, out var cidSet) ? cidSet.OrderBy(x => x).ToList() : [];

            imported[code] = new SigtapProcedureRule
            {
                Code = code,
                Name = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.ToLowerInvariant()),
                SexRestriction = sex is "M" or "F" ? sex : "I", // I = Indiferente, N = Não se aplica
                MinAgeMonths = Math.Max(0, minAgeMonths),
                MaxAgeMonths = !hasMaxAge || maxAgeMonths <= 0 || maxAgeMonths >= 9999 ? 9999 : maxAgeMonths,
                AllowedInstruments = instruments,
                AllowedCbos = cbos,
                RequiresCid = instruments.Contains("APAC") || cids.Count > 0,
                AllowedCids = cids,
                MaxQuantity = Math.Max(0, maxQuantity),
                UnitValue = vlSaCents / 100m,
                Financing = MapFinanciamento(financingCode)
            };
        }

        if (imported.Count == 0)
        {
            throw new InvalidDataException("Nenhum procedimento válido foi lido de 'tb_procedimento.txt'. Confira se a pasta é a do SIGTAP descompactado.");
        }

        // Substitui a base inteira: a tabela oficial não se mistura com a base de exemplo.
        _procedures = imported;
        IsOfficialTableLoaded = true;
        LoadedSourceLabel = string.IsNullOrEmpty(competenciaDetected)
            ? $"SIGTAP oficial ({imported.Count:N0} procedimentos)"
            : $"SIGTAP oficial competência {competenciaDetected[4..]}/{competenciaDetected[..4]} ({imported.Count:N0} procedimentos)";

        return imported.Count;
    }

    private static Dictionary<string, HashSet<string>> ReadRelation(
        string folderPath,
        string tableName,
        IReadOnlyList<(string Name, int Length)> defaultLayout,
        string valueColumn,
        Encoding encoding,
        Func<string, string> mapValue,
        Func<SigtapLayout, string, bool>? include = null)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var file = Path.Combine(folderPath, $"{tableName}.txt");
        if (!File.Exists(file))
        {
            return map;
        }

        var layout = SigtapLayout.Load(folderPath, tableName, defaultLayout, encoding);
        foreach (var line in File.ReadLines(file, encoding))
        {
            var procCode = layout.Read(line, "CO_PROCEDIMENTO");
            var value = layout.Read(line, valueColumn);
            if (procCode.Length != 10 || value.Length == 0 || (include is not null && !include(layout, line)))
            {
                continue;
            }

            if (!map.TryGetValue(procCode, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                map[procCode] = set;
            }

            set.Add(mapValue(value));
        }

        return map;
    }

    /// <summary>"C50.9" / "c509 " → "C509".</summary>
    public static string NormalizeCid(string? cid) =>
        new string((cid ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    // ---------------- Cache em disco ----------------

    private sealed record CacheFile(int Version, string Label, List<SigtapProcedureRule> Procedures);

    private const int CacheVersion = 2;

    /// <summary>Grava a tabela oficial carregada para ser reaberta na próxima execução sem reimportar.</summary>
    public void SaveCache(string path)
    {
        if (!IsOfficialTableLoaded)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, new CacheFile(CacheVersion, LoadedSourceLabel, _procedures.Values.ToList()));
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Carrega a tabela salva por SaveCache. Arquivo ausente, de outra versão ou corrompido → false (fica a base atual).</summary>
    public bool TryLoadCache(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var cache = JsonSerializer.Deserialize<CacheFile>(stream);
            if (cache is null || cache.Version != CacheVersion || cache.Procedures is not { Count: > 0 })
            {
                return false;
            }

            _procedures = cache.Procedures.ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase);
            LoadedSourceLabel = cache.Label;
            IsOfficialTableLoaded = true;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private static string MapRegistro(string regCode) => regCode switch
    {
        "01" => "BPA-C",
        "02" => "BPA-I",
        "03" or "04" or "05" => "AIH",
        "06" or "07" => "APAC",
        "08" or "09" => "RAAS",
        _ => $"REG-{regCode}"
    };

    private static string MapFinanciamento(string code) => code switch
    {
        "01" => "PAB / Atenção Básica",
        "02" => "Assistência Farmacêutica",
        "04" => "FAEC",
        "05" => "Incentivo - MAC",
        "06" => "MAC",
        "07" => "Vigilância em Saúde",
        "" => "SUS",
        _ => $"FIN-{code}"
    };

    private static IReadOnlyList<SigtapProcedureRule> GetBuiltInSeed() =>
    [
        new SigtapProcedureRule
        {
            Code = "0203010086",
            Name = "Exame Citopatológico Cérvico-Vaginal/Microflora - Rastreamento",
            SexRestriction = "F",
            MinAgeMonths = 300, // 25 anos
            MaxAgeMonths = 779, // 64 anos e 11 meses
            AllowedInstruments = ["BPA-I"],
            AllowedCbos = ["221105", "225305", "225335"],
            RequiresCid = false,
            UnitValue = 0.00m,
            Financing = "MAC"
        },
        new SigtapProcedureRule
        {
            Code = "0203010019",
            Name = "Exame Citopatológico Cérvico-Vaginal/Microflora",
            SexRestriction = "F",
            MinAgeMonths = 0,
            MaxAgeMonths = 9999,
            AllowedInstruments = ["BPA-I"],
            AllowedCbos = ["221105", "225305", "225335"],
            RequiresCid = false,
            UnitValue = 0.00m,
            Financing = "MAC"
        },
        new SigtapProcedureRule
        {
            Code = "0301010064",
            Name = "Consulta Médica / Enfermagem na Atenção Primária",
            SexRestriction = "I",
            MinAgeMonths = 0,
            MaxAgeMonths = 9999,
            AllowedInstruments = ["BPA-C", "BPA-I"],
            AllowedCbos = ["225125", "225142", "225130", "225124", "223505", "223565"],
            RequiresCid = false,
            UnitValue = 0.00m,
            Financing = "PAB / APS"
        },
        new SigtapProcedureRule
        {
            Code = "0301010072",
            Name = "Consulta Médica em Atenção Especializada",
            SexRestriction = "I",
            MinAgeMonths = 0,
            MaxAgeMonths = 9999,
            AllowedInstruments = ["BPA-I"],
            AllowedCbos = ["225125", "225120", "225124", "225135", "225155", "225250", "225265", "225270", "225275"],
            RequiresCid = false,
            UnitValue = 10.00m,
            Financing = "MAC"
        },
        new SigtapProcedureRule
        {
            Code = "0301010048",
            Name = "Consulta de Profissionais de Nível Superior na Atenção Especializada (Exceto Médico)",
            SexRestriction = "I",
            MinAgeMonths = 0,
            MaxAgeMonths = 9999,
            AllowedInstruments = ["BPA-I", "BPA-C"],
            AllowedCbos = ["223505", "223605", "223710", "251510", "223810", "251605"],
            RequiresCid = false,
            UnitValue = 6.30m,
            Financing = "MAC"
        },
        new SigtapProcedureRule
        {
            Code = "0301010110",
            Name = "Consulta Pré-Natal",
            SexRestriction = "F",
            MinAgeMonths = 108, // 9 anos
            MaxAgeMonths = 720, // 60 anos
            AllowedInstruments = ["BPA-I"],
            AllowedCbos = ["225125", "225142", "225250", "223505", "223565", "223545"],
            RequiresCid = false,
            UnitValue = 0.00m,
            Financing = "PAB / Rede Alyne"
        },
        new SigtapProcedureRule
        {
            Code = "0201020033",
            Name = "Coleta de Material para Exame Citopatológico de Colo de Útero",
            SexRestriction = "F",
            MinAgeMonths = 120, // 10 anos
            MaxAgeMonths = 1320,
            AllowedInstruments = ["BPA-I"],
            AllowedCbos = ["225125", "225142", "225250", "223505", "223565"],
            RequiresCid = false,
            UnitValue = 6.64m,
            Financing = "FAEC / MAC"
        },
        new SigtapProcedureRule
        {
            Code = "0204030188",
            Name = "Mamografia Bilateral para Rastreamento",
            SexRestriction = "F",
            MinAgeMonths = 600, // 50 anos (regra rastreamento/monitoramento)
            MaxAgeMonths = 839, // 69 anos e 11 meses
            AllowedInstruments = ["BPA-I"],
            AllowedCbos = ["225320"],
            RequiresCid = false,
            RequiredServiceClassification = "121/001 (Diagnóstico por Imagem)",
            UnitValue = 45.00m,
            Financing = "FAEC"
        },
        new SigtapProcedureRule
        {
            Code = "0205020143",
            Name = "Ultrassonografia Obstétrica",
            SexRestriction = "F",
            MinAgeMonths = 108,
            MaxAgeMonths = 720,
            AllowedInstruments = ["BPA-I"],
            AllowedCbos = ["225320", "225250"],
            RequiresCid = false,
            RequiredServiceClassification = "121/005 (Ultrassonografia)",
            UnitValue = 24.20m,
            Financing = "MAC"
        },
        new SigtapProcedureRule
        {
            Code = "0211020036",
            Name = "Eletrocardiograma (ECG)",
            SexRestriction = "I",
            MinAgeMonths = 0,
            MaxAgeMonths = 9999,
            AllowedInstruments = ["BPA-C", "BPA-I"],
            AllowedCbos = ["225120", "225125", "223505", "322205"],
            RequiresCid = false,
            UnitValue = 5.15m,
            Financing = "MAC"
        },
        new SigtapProcedureRule
        {
            Code = "0301100039",
            Name = "Aferição de Pressão Arterial",
            SexRestriction = "I",
            MinAgeMonths = 0,
            MaxAgeMonths = 9999,
            AllowedInstruments = ["BPA-C", "BPA-I"],
            AllowedCbos = ["223505", "223565", "322205", "322230", "322245", "225125", "225142"],
            RequiresCid = false,
            UnitValue = 0.00m,
            Financing = "PAB / APS"
        },
        new SigtapProcedureRule
        {
            Code = "0302050027",
            Name = "Atendimento Fisioterapêutico nas Alterações Motoras",
            SexRestriction = "I",
            MinAgeMonths = 0,
            MaxAgeMonths = 9999,
            AllowedInstruments = ["BPA-I"],
            AllowedCbos = ["223605"],
            RequiresCid = true,
            RequiredServiceClassification = "135/001 (Reabilitação Física)",
            UnitValue = 4.67m,
            Financing = "MAC"
        },
        new SigtapProcedureRule
        {
            Code = "0206010079",
            Name = "Tomografia Computadorizada de Crânio",
            SexRestriction = "I",
            MinAgeMonths = 0,
            MaxAgeMonths = 9999,
            AllowedInstruments = ["BPA-I", "APAC"],
            AllowedCbos = ["225320"],
            RequiresCid = true,
            RequiredServiceClassification = "121/002 (Tomografia)",
            UnitValue = 97.44m,
            Financing = "MAC"
        },
        new SigtapProcedureRule
        {
            Code = "0305010107",
            Name = "Hemodiálise Crônica (Máximo 3 Sessões por Semana)",
            SexRestriction = "I",
            MinAgeMonths = 0,
            MaxAgeMonths = 9999,
            AllowedInstruments = ["APAC"],
            AllowedCbos = ["225109"],
            RequiresCid = true,
            RequiredServiceClassification = "130/001 (Atenção a Doença Renal Crônica - TRS)",
            UnitValue = 240.97m,
            Financing = "FAEC"
        },
        new SigtapProcedureRule
        {
            Code = "0304020133",
            Name = "Quimioterapia do Carcinoma de Mama Avançado (1ª Linha)",
            SexRestriction = "I",
            MinAgeMonths = 228, // 19 anos
            MaxAgeMonths = 9999,
            AllowedInstruments = ["APAC"],
            AllowedCbos = ["225121"],
            RequiresCid = true,
            RequiredServiceClassification = "132/003 (Oncologia Clínica - UNACON/CACON)",
            UnitValue = 1700.00m,
            Financing = "MAC / Alta Complexidade"
        },
        new SigtapProcedureRule
        {
            Code = "0301080208",
            Name = "Atendimento Individual de Paciente em CAPS",
            SexRestriction = "I",
            MinAgeMonths = 0,
            MaxAgeMonths = 9999,
            AllowedInstruments = ["RAAS"],
            AllowedCbos = ["225133", "251510", "223505", "223905", "251605"],
            RequiresCid = true,
            RequiredServiceClassification = "115/002 (Atenção Psicossocial - CAPS)",
            UnitValue = 0.00m,
            Financing = "MAC / RAPS"
        }
    ];
}
