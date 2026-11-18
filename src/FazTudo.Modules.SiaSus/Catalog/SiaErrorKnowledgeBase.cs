using System.Text.Json;
using System.Text.Json.Serialization;
using FazTudo.Core.Models;

namespace FazTudo.Modules.SiaSus.Catalog;

/// <summary>Quem resolve e o que fazer, para cada área (BPA, APAC, CNES, FPO, SIA).</summary>
public sealed record FixAreaInfo(string Label, string Who, string Action);

/// <summary>
/// Base de erros do SIA/SUS. O conteúdo fica em shared/sia-knowledge.json (embutido na DLL),
/// o mesmo arquivo que o app web em Python lê — edite lá.
/// </summary>
public static class SiaErrorKnowledgeBase
{
    public const string ModuleKey = "sia-sus";
    private const string ResourceName = "FazTudo.SiaKnowledge.json";

    private sealed class KnowledgeFile
    {
        public int Version { get; init; }
        public Dictionary<string, FixAreaInfo> Areas { get; init; } = [];
        public Dictionary<string, string> SiaCodes { get; init; } = [];
        public List<DiagnosticErrorEntry> Entries { get; init; } = [];
    }

    private static readonly Lazy<KnowledgeFile> File = new(Load);

    public static IReadOnlyList<DiagnosticErrorEntry> GetAll() => File.Value.Entries;

    /// <summary>Áreas de resolução: "BPA", "APAC", "CNES", "FPO", "SIA".</summary>
    public static IReadOnlyDictionary<string, FixAreaInfo> Areas => File.Value.Areas;

    /// <summary>Código do relatório de consistência do SIA (ex.: "FET2") → Id do erro na base.</summary>
    public static IReadOnlyDictionary<string, string> SiaCodes => File.Value.SiaCodes;

    private static KnowledgeFile Load()
    {
        using var stream = typeof(SiaErrorKnowledgeBase).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Recurso {ResourceName} não encontrado na DLL do módulo.");
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            Converters = { new JsonStringEnumConverter() }
        };
        var file = JsonSerializer.Deserialize<KnowledgeFile>(stream, options)
            ?? throw new InvalidDataException("sia-knowledge.json vazio.");
        return file;
    }
}
