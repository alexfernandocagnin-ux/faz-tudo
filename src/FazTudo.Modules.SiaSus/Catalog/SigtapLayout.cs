using System.Text;

namespace FazTudo.Modules.SiaSus.Catalog;

/// <summary>
/// Layout posicional de um arquivo do SIGTAP (tb_*.txt / rl_*.txt).
/// O zip oficial traz, para cada tabela, um "&lt;tabela&gt;_layout.txt" no formato
/// "Coluna,Tamanho,Inicio,Fim,Tipo" (posições 1-based). Quando ele existe, é a fonte da verdade;
/// senão usa-se o layout padrão conhecido.
/// </summary>
internal sealed class SigtapLayout
{
    private readonly Dictionary<string, (int Start, int Length)> _columns;

    private SigtapLayout(Dictionary<string, (int Start, int Length)> columns, bool fromFile)
    {
        _columns = columns;
        LoadedFromFile = fromFile;
    }

    public bool LoadedFromFile { get; }

    public static SigtapLayout Load(string folderPath, string tableName, IReadOnlyList<(string Name, int Length)> defaultLayout, Encoding encoding)
    {
        var layoutFile = Path.Combine(folderPath, $"{tableName}_layout.txt");
        if (File.Exists(layoutFile))
        {
            var parsed = TryParseLayoutFile(layoutFile, encoding);
            if (parsed is not null)
            {
                return new SigtapLayout(parsed, fromFile: true);
            }
        }

        var columns = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        foreach (var (name, length) in defaultLayout)
        {
            columns[name] = (offset, length);
            offset += length;
        }

        return new SigtapLayout(columns, fromFile: false);
    }

    public bool Has(string column) => _columns.ContainsKey(column);

    /// <summary>Lê a coluna da linha de forma segura (linha curta ou coluna ausente → string vazia).</summary>
    public string Read(string line, string column)
    {
        if (!_columns.TryGetValue(column, out var col) || col.Start >= line.Length)
        {
            return string.Empty;
        }

        var length = Math.Min(col.Length, line.Length - col.Start);
        return line.Substring(col.Start, length).Trim();
    }

    private static Dictionary<string, (int, int)>? TryParseLayoutFile(string path, Encoding encoding)
    {
        var columns = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(path, encoding))
        {
            var parts = rawLine.Split([',', ';', '\t'], StringSplitOptions.TrimEntries);
            if (parts.Length < 3
                || !int.TryParse(parts[1], out var length)
                || !int.TryParse(parts[2], out var start)
                || length <= 0 || start <= 0)
            {
                continue; // cabeçalho ou linha inválida
            }

            columns[parts[0]] = (start - 1, length);
        }

        return columns.Count > 0 ? columns : null;
    }
}
