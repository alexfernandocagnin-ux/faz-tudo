using System.Globalization;
using System.Text;

namespace FazTudo.Modules.SiaSus.Bpa;

public enum BpaRecordType
{
    Consolidated, // linha "02" — BPA-C
    Individual    // linha "03" — BPA-I
}

public sealed class BpaRecord
{
    public required int LineNumber { get; init; }
    public required BpaRecordType Type { get; init; }
    public required string Cnes { get; init; }
    public required string Competencia { get; init; }
    public required string Cbo { get; init; }
    public required string Folha { get; init; }
    public required string Sequencia { get; init; }
    public required string ProcedureCode { get; init; }
    public required int? AgeYears { get; init; }
    public required int Quantity { get; init; }

    // Só BPA-I
    public string ProfessionalCns { get; init; } = string.Empty;
    public DateOnly? AttendanceDate { get; init; }
    public string PatientCns { get; init; } = string.Empty;
    public string Sex { get; init; } = string.Empty;
    public string Cid { get; init; } = string.Empty;
    public string PatientName { get; init; } = string.Empty;
    public DateOnly? BirthDate { get; init; }
    public string RaceColor { get; init; } = string.Empty;
    public string Ethnicity { get; init; } = string.Empty;

    public string InstrumentLabel => Type == BpaRecordType.Individual ? "BPA-I" : "BPA-C";
}

public sealed class BpaFile
{
    public required string SourceName { get; init; }
    public required bool HasHeader { get; init; }
    public required string HeaderCompetencia { get; init; }
    public required IReadOnlyList<BpaRecord> Records { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>
    /// Lê a remessa magnética do BPA-Mag (layout posicional, Latin1).
    /// Cabeçalho "01#BPA#AAAAMM...", linhas "02" (BPA-C) e "03" (BPA-I).
    /// </summary>
    public static BpaFile Load(string path) => Parse(File.ReadLines(path, Encoding.Latin1), Path.GetFileName(path));

    public static BpaFile Parse(IEnumerable<string> lines, string sourceName)
    {
        var records = new List<BpaRecord>();
        var warnings = new List<string>();
        var hasHeader = false;
        var headerCompetencia = string.Empty;
        var lineNumber = 0;
        var unknownLines = 0;

        foreach (var raw in lines)
        {
            lineNumber++;
            var line = raw.TrimEnd('\r', '\n');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("01#BPA#", StringComparison.Ordinal))
            {
                hasHeader = true;
                headerCompetencia = Cut(line, 7, 6);
                continue;
            }

            if (line.StartsWith("02", StringComparison.Ordinal) && line.Length >= 45)
            {
                records.Add(new BpaRecord
                {
                    LineNumber = lineNumber,
                    Type = BpaRecordType.Consolidated,
                    Cnes = Cut(line, 2, 7),
                    Competencia = Cut(line, 9, 6),
                    Cbo = Cut(line, 15, 6),
                    Folha = Cut(line, 21, 3),
                    Sequencia = Cut(line, 24, 2),
                    ProcedureCode = Cut(line, 26, 10),
                    AgeYears = ParseInt(Cut(line, 36, 3)),
                    Quantity = ParseInt(Cut(line, 39, 6)) ?? 0
                });
                continue;
            }

            if (line.StartsWith("03", StringComparison.Ordinal) && line.Length >= 94)
            {
                records.Add(new BpaRecord
                {
                    LineNumber = lineNumber,
                    Type = BpaRecordType.Individual,
                    Cnes = Cut(line, 2, 7),
                    Competencia = Cut(line, 9, 6),
                    ProfessionalCns = Cut(line, 15, 15),
                    Cbo = Cut(line, 30, 6),
                    AttendanceDate = ParseDate(Cut(line, 36, 8)),
                    Folha = Cut(line, 44, 3),
                    Sequencia = Cut(line, 47, 2),
                    ProcedureCode = Cut(line, 49, 10),
                    PatientCns = Cut(line, 59, 15),
                    Sex = Cut(line, 74, 1).ToUpperInvariant(),
                    Cid = Cut(line, 81, 4),
                    AgeYears = ParseInt(Cut(line, 85, 3)),
                    Quantity = ParseInt(Cut(line, 88, 6)) ?? 0,
                    PatientName = Cut(line, 112, 30),
                    BirthDate = ParseDate(Cut(line, 142, 8)),
                    RaceColor = Cut(line, 150, 2),
                    Ethnicity = Cut(line, 152, 4)
                });
                continue;
            }

            unknownLines++;
        }

        if (!hasHeader)
        {
            warnings.Add("Arquivo sem cabeçalho \"01#BPA#\": pode não ser uma remessa do BPA-Mag.");
        }

        if (unknownLines > 0)
        {
            warnings.Add($"{unknownLines:N0} linha(s) não reconhecida(s) como BPA-C (02) ou BPA-I (03) foram ignoradas.");
        }

        return new BpaFile
        {
            SourceName = sourceName,
            HasHeader = hasHeader,
            HeaderCompetencia = headerCompetencia,
            Records = records,
            Warnings = warnings
        };
    }

    private static string Cut(string line, int start, int length)
    {
        if (start >= line.Length)
        {
            return string.Empty;
        }

        return line.Substring(start, Math.Min(length, line.Length - start)).Trim();
    }

    private static int? ParseInt(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static DateOnly? ParseDate(string value) =>
        DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
