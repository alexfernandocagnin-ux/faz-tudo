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

    /// <summary>Competência da tabela carregada (AAAAMM); vazio na base de exemplo.</summary>
    public string Competencia { get; private set; } = string.Empty;

    public string CompetenciaDisplay => Competencia.Length == 6 ? $"{Competencia[4..]}/{Competencia[..4]}" : Competencia;

    /// <summary>tb_regra_condicionada: código → (nome, descrição).</summary>
    public IReadOnlyDictionary<string, RuleText> RuleNames { get; private set; } = new Dictionary<string, RuleText>();

    /// <summary>tb_detalhe: código → nome (ex.: "APAC com validade fixa de 2 competências.").</summary>
    public IReadOnlyDictionary<string, string> DetailNames { get; private set; } = new Dictionary<string, string>();

    public sealed record RuleText(string Name, string Description);

    public IReadOnlyList<string> DetailTexts(SigtapProcedureRule rule) =>
        rule.Details.Where(DetailNames.ContainsKey).Select(d => DetailNames[d]).ToList();

    public IReadOnlyList<RuleText> RuleTexts(SigtapProcedureRule rule) =>
        rule.ConditionedRules.Where(RuleNames.ContainsKey).Select(r => RuleNames[r]).ToList();

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

    private static readonly (string Name, int Length)[] DefaultCompativelLayout =
    [
        ("CO_PROCEDIMENTO_PRINCIPAL", 10), ("CO_REGISTRO_PRINCIPAL", 2), ("CO_PROCEDIMENTO_COMPATIVEL", 10),
        ("CO_REGISTRO_COMPATIVEL", 2), ("TP_COMPATIBILIDADE", 1), ("QT_PERMITIDA", 4), ("DT_COMPETENCIA", 6)
    ];

    private static readonly (string Name, int Length)[] DefaultRegraCondLayout = [("CO_PROCEDIMENTO", 10), ("CO_REGRA_CONDICIONADA", 4)];
    private static readonly (string Name, int Length)[] DefaultTbRegraLayout =
        [("CO_REGRA_CONDICIONADA", 4), ("NO_REGRA_CONDICIONADA", 150), ("DS_REGRA_CONDICIONADA", 4000)];
    private static readonly (string Name, int Length)[] DefaultDetalheLayout = [("CO_PROCEDIMENTO", 10), ("CO_DETALHE", 3), ("DT_COMPETENCIA", 6)];
    private static readonly (string Name, int Length)[] DefaultTbDetalheLayout = [("CO_DETALHE", 3), ("NO_DETALHE", 100), ("DT_COMPETENCIA", 6)];

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

        var ruleMap = ReadRelation(folderPath, "rl_procedimento_regra_cond", DefaultRegraCondLayout, "CO_REGRA_CONDICIONADA", latin1, c => c);
        var detailMap = ReadRelation(folderPath, "rl_procedimento_detalhe", DefaultDetalheLayout, "CO_DETALHE", latin1, c => c);
        var compatMap = ReadCompatibles(folderPath, latin1);

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
                Name = TitlePt(name),
                SexRestriction = sex is "M" or "F" ? sex : "I", // I = Indiferente, N = Não se aplica
                MinAgeMonths = Math.Max(0, minAgeMonths),
                MaxAgeMonths = !hasMaxAge || maxAgeMonths <= 0 || maxAgeMonths >= 9999 ? 9999 : maxAgeMonths,
                AllowedInstruments = instruments,
                AllowedCbos = cbos,
                RequiresCid = instruments.Contains("APAC") || cids.Count > 0,
                AllowedCids = cids,
                MaxQuantity = Math.Max(0, maxQuantity),
                UnitValue = vlSaCents / 100m,
                Financing = MapFinanciamento(financingCode),
                Compatibles = compatMap.TryGetValue(code, out var compat) ? compat : [],
                ConditionedRules = ruleMap.TryGetValue(code, out var rules) ? rules.OrderBy(x => x).ToList() : [],
                Details = detailMap.TryGetValue(code, out var details) ? details.OrderBy(x => x).ToList() : []
            };
        }

        if (imported.Count == 0)
        {
            throw new InvalidDataException("Nenhum procedimento válido foi lido de 'tb_procedimento.txt'. Confira se a pasta é a do SIGTAP descompactado.");
        }

        // Substitui a base inteira: a tabela oficial não se mistura com a base de exemplo.
        _procedures = imported;
        IsOfficialTableLoaded = true;
        Competencia = competenciaDetected;
        RuleNames = ReadNames(folderPath, "tb_regra_condicionada", DefaultTbRegraLayout, latin1,
            (l, line) => (l.Read(line, "CO_REGRA_CONDICIONADA"),
                          new RuleText(TitlePt(l.Read(line, "NO_REGRA_CONDICIONADA")).Replace("Secudários", "Secundários"),
                                       l.Read(line, "DS_REGRA_CONDICIONADA"))));
        DetailNames = ReadNames(folderPath, "tb_detalhe", DefaultTbDetalheLayout, latin1,
            (l, line) => (l.Read(line, "CO_DETALHE"), l.Read(line, "NO_DETALHE")));
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

    private static Dictionary<string, List<SigtapCompatible>> ReadCompatibles(string folderPath, Encoding encoding)
    {
        var map = new Dictionary<string, List<SigtapCompatible>>(StringComparer.OrdinalIgnoreCase);
        var file = Path.Combine(folderPath, "rl_procedimento_compativel.txt");
        if (!File.Exists(file))
        {
            return map;
        }

        var layout = SigtapLayout.Load(folderPath, "rl_procedimento_compativel", DefaultCompativelLayout, encoding);
        foreach (var line in File.ReadLines(file, encoding))
        {
            var principal = layout.Read(line, "CO_PROCEDIMENTO_PRINCIPAL");
            var secondary = layout.Read(line, "CO_PROCEDIMENTO_COMPATIVEL");
            if (principal.Length != 10 || secondary.Length != 10)
            {
                continue;
            }

            int.TryParse(layout.Read(line, "QT_PERMITIDA"), out var qty);
            var entry = new SigtapCompatible(secondary, qty, layout.Read(line, "TP_COMPATIBILIDADE"));
            if (!map.TryGetValue(principal, out var list))
            {
                list = [];
                map[principal] = list;
            }

            if (!list.Contains(entry))
            {
                list.Add(entry);
            }
        }

        return map;
    }

    private static Dictionary<string, T> ReadNames<T>(string folderPath, string table, IReadOnlyList<(string Name, int Length)> defaultLayout,
        Encoding encoding, Func<SigtapLayout, string, (string Key, T Value)> read)
    {
        var map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        var file = Path.Combine(folderPath, $"{table}.txt");
        if (!File.Exists(file))
        {
            return map;
        }

        var layout = SigtapLayout.Load(folderPath, table, defaultLayout, encoding);
        foreach (var line in File.ReadLines(file, encoding))
        {
            var (key, value) = read(layout, line);
            if (key.Length > 0)
            {
                map[key] = value;
            }
        }

        return map;
    }

    private static readonly HashSet<string> LowerWords = new(StringComparer.Ordinal)
    {
        "de", "da", "do", "das", "dos", "e", "em", "na", "no", "nas", "nos", "a", "o", "as", "os", "com", "por", "para", "p/",
        "ou", "ao", "aos", "à", "às", "x"
    };

    private static readonly HashSet<string> UpperWords = new(StringComparer.Ordinal)
    {
        "oci", "apac", "sus", "cid", "hiv", "aids", "cdk", "ii", "iii", "iv", "vi", "vii", "viii", "ix", "uti", "utin", "caps",
        "rx", "pet-ct", "dna", "rna", "pcr", "ivas", "dst", "ist", "tb"
    };

    /// <summary>"CONSULTA MEDICA EM ATENCAO ESPECIALIZADA" → "Consulta Medica em Atencao Especializada" (siglas em maiúsculas).</summary>
    public static string TitlePt(string text)
    {
        var words = text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select((w, i) =>
        {
            var core = w.Trim('(', ')', '-', ',', '.', ';', ':', '/');
            if (UpperWords.Contains(core))
            {
                return w.ToUpperInvariant();
            }

            return i > 0 && LowerWords.Contains(w) ? w : char.ToUpperInvariant(w[0]) + w[1..];
        }));
    }

    /// <summary>"C50.9" / "c509 " → "C509".</summary>
    public static string NormalizeCid(string? cid) =>
        new string((cid ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    // ---------------- Cache em disco ----------------

    private sealed record CacheFile(int Version, string Label, string Competencia, List<SigtapProcedureRule> Procedures,
        Dictionary<string, RuleText> RuleNames, Dictionary<string, string> DetailNames);

    private const int CacheVersion = 3;

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
            JsonSerializer.Serialize(stream, new CacheFile(CacheVersion, LoadedSourceLabel, Competencia, _procedures.Values.ToList(),
                RuleNames.ToDictionary(), DetailNames.ToDictionary()));
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Carrega a tabela padrão do projeto (shared/sigtap/sigtap-AAAAMM.json.gz), a mesma que o site usa.
    /// O arquivo é gerado pelo web/tools/preparar_sigtap.py (JSON em snake_case, compactado com gzip).
    /// </summary>
    public bool TryLoadSharedBundle(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var root = System.Text.Json.Nodes.JsonNode.Parse(gzip)!.AsObject();

            static List<string> Strings(System.Text.Json.Nodes.JsonNode? node) =>
                node?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];

            var procedures = new Dictionary<string, SigtapProcedureRule>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in root["procedures"]!.AsArray())
            {
                var p = item!.AsObject();
                var code = p["code"]!.GetValue<string>();
                procedures[code] = new SigtapProcedureRule
                {
                    Code = code,
                    Name = p["name"]!.GetValue<string>(),
                    SexRestriction = p["sex_restriction"]!.GetValue<string>(),
                    MinAgeMonths = p["min_age_months"]!.GetValue<int>(),
                    MaxAgeMonths = p["max_age_months"]!.GetValue<int>(),
                    AllowedInstruments = Strings(p["allowed_instruments"]),
                    AllowedCbos = Strings(p["allowed_cbos"]),
                    RequiresCid = p["requires_cid"]!.GetValue<bool>(),
                    UnitValue = decimal.Parse(p["unit_value"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                    Financing = p["financing"]!.GetValue<string>(),
                    RequiredServiceClassification = p["required_service_classification"]?.GetValue<string>() ?? string.Empty,
                    AllowedCids = Strings(p["allowed_cids"]),
                    MaxQuantity = p["max_quantity"]?.GetValue<int>() ?? 0,
                    Compatibles = p["compatibles"]?.AsArray()
                        .Select(c => new SigtapCompatible(c![0]!.GetValue<string>(), c[1]!.GetValue<int>(), c[2]!.GetValue<string>()))
                        .ToList() ?? [],
                    ConditionedRules = Strings(p["conditioned_rules"]),
                    Details = Strings(p["details"])
                };
            }

            if (procedures.Count == 0)
            {
                return false;
            }

            _procedures = procedures;
            LoadedSourceLabel = root["label"]?.GetValue<string>() ?? "SIGTAP oficial";
            Competencia = root["competencia"]?.GetValue<string>() ?? string.Empty;
            RuleNames = root["rule_names"]?.AsObject().ToDictionary(
                kv => kv.Key, kv => new RuleText(kv.Value![0]!.GetValue<string>(), kv.Value[1]!.GetValue<string>())) ?? [];
            DetailNames = root["detail_names"]?.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()) ?? [];
            IsOfficialTableLoaded = true;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or InvalidOperationException
                                       or FormatException or NullReferenceException or KeyNotFoundException)
        {
            return false;
        }
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
            Competencia = cache.Competencia ?? string.Empty;
            RuleNames = cache.RuleNames ?? [];
            DetailNames = cache.DetailNames ?? [];
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
        "10" => "e-SUS APS",
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
