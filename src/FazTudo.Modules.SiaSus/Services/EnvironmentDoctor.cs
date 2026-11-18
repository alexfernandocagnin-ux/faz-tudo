using System.Diagnostics;
using System.Net.Sockets;
using FazTudo.Core.Models;

namespace FazTudo.Modules.SiaSus.Services;

/// <summary>
/// Doutor TI DATASUS: checagens SOMENTE DE LEITURA do ambiente Windows onde rodam SIA, BPA-Mag, APAC-Mag e SCNES.
/// Nada é alterado; cada item diz o que foi encontrado e o que fazer.
/// </summary>
public sealed class EnvironmentDoctor
{
    private static readonly string[] DatasusFolders = ["SIA", "BPA", "APAC", "SCNES", "FPO", "RAAS"];
    private static readonly string[] FirebirdProcesses = ["fbserver", "fb_inet_server", "fbguard", "firebird", "ibserver"];

    public string SystemDrive { get; init; } = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) is { Length: > 0 } root ? root : @"C:\";

    public Task<IReadOnlyList<EnvironmentCheckItem>> RunAllAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<EnvironmentCheckItem>>(async () =>
        {
            var items = new List<EnvironmentCheckItem>();
            items.Add(CheckInstallFolders());
            items.AddRange(CheckVirtualStore());
            items.Add(await CheckFirebirdPortAsync(cancellationToken));
            items.Add(CheckFirebirdProcesses());
            items.Add(CheckMidas());
            items.Add(CheckBde());
            items.Add(CheckDiskSpace());
            return items;
        }, cancellationToken);

    private EnvironmentCheckItem CheckInstallFolders()
    {
        var found = DatasusFolders.Where(f => Directory.Exists(Path.Combine(SystemDrive, f))).ToList();
        var programFiles = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            }
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct()
            .SelectMany(pf => DatasusFolders.Select(f => Path.Combine(pf, f)))
            .Where(Directory.Exists)
            .ToList();

        if (programFiles.Count > 0)
        {
            return new EnvironmentCheckItem
            {
                CheckId = "folders",
                Category = "Instalação",
                Title = "Sistemas DATASUS dentro de 'Arquivos de Programas'",
                Status = CheckHealthStatus.Critical,
                DetectedState = "Encontrado: " + string.Join(", ", programFiles),
                Recommendation = "Pastas em Arquivos de Programas sofrem redirecionamento do Windows (VirtualStore). Reinstale na raiz (ex.: C:\\SIA, C:\\BPA).",
                LinkedKnowledgeErrorId = "sia-ti-virtualstore"
            };
        }

        return new EnvironmentCheckItem
        {
            CheckId = "folders",
            Category = "Instalação",
            Title = "Pastas dos sistemas DATASUS na raiz do disco",
            Status = found.Count > 0 ? CheckHealthStatus.Healthy : CheckHealthStatus.Info,
            DetectedState = found.Count > 0
                ? "Encontrado: " + string.Join(", ", found.Select(f => Path.Combine(SystemDrive, f)))
                : $"Nenhuma pasta {string.Join("/", DatasusFolders)} em {SystemDrive}",
            Recommendation = found.Count > 0
                ? "Instalação no local recomendado."
                : "Se os sistemas estão instalados em outro disco, ignore este item."
        };
    }

    private static IEnumerable<EnvironmentCheckItem> CheckVirtualStore()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var virtualStore = Path.Combine(localAppData, "VirtualStore");
        if (!Directory.Exists(virtualStore))
        {
            yield return new EnvironmentCheckItem
            {
                CheckId = "virtualstore",
                Category = "Windows / UAC",
                Title = "Pasta VirtualStore",
                Status = CheckHealthStatus.Healthy,
                DetectedState = "Não existe VirtualStore para este usuário.",
                Recommendation = "Nenhuma ação necessária."
            };
            yield break;
        }

        var hijacked = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(virtualStore, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true }))
            {
                if (DatasusFolders.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase))
                {
                    hijacked.Add(dir);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // segue com o que foi possível ler
        }

        yield return new EnvironmentCheckItem
        {
            CheckId = "virtualstore",
            Category = "Windows / UAC",
            Title = "Arquivos DATASUS desviados para a VirtualStore",
            Status = hijacked.Count > 0 ? CheckHealthStatus.Critical : CheckHealthStatus.Healthy,
            DetectedState = hijacked.Count > 0
                ? "Cópias paralelas em: " + string.Join("; ", hijacked)
                : $"VirtualStore existe ({virtualStore}), mas sem pastas dos sistemas DATASUS.",
            Recommendation = hijacked.Count > 0
                ? "O BDSIA gravou na VirtualStore e o sistema lê a base antiga. Faça backup dessas pastas, remova-as e rode o BDSIA como Administrador."
                : "Nenhuma ação necessária.",
            LinkedKnowledgeErrorId = hijacked.Count > 0 ? "sia-ti-virtualstore" : null
        };
    }

    private static async Task<EnvironmentCheckItem> CheckFirebirdPortAsync(CancellationToken cancellationToken)
    {
        bool open;
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync("127.0.0.1", 3050, timeout.Token);
            open = true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            open = false;
        }

        return new EnvironmentCheckItem
        {
            CheckId = "firebird-port",
            Category = "Banco Firebird",
            Title = "Porta 3050 do Firebird",
            Status = open ? CheckHealthStatus.Healthy : CheckHealthStatus.Attention,
            DetectedState = open ? "Porta 3050 aceitando conexões em localhost." : "Nada respondendo na porta 3050 em localhost.",
            Recommendation = open
                ? "Se mesmo assim der 'Unavailable database', verifique se não é outra versão do Firebird (3.0+) ocupando a porta."
                : "Se esta máquina roda SIA/SCNES local, inicie o serviço 'Firebird Server' em services.msc. Em estação cliente de rede, ignore.",
            LinkedKnowledgeErrorId = open ? null : "sia-ti-firebird"
        };
    }

    private static EnvironmentCheckItem CheckFirebirdProcesses()
    {
        var running = new List<string>();
        foreach (var name in FirebirdProcesses)
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                if (processes.Length > 0)
                {
                    running.Add($"{name}.exe");
                }

                foreach (var p in processes)
                {
                    p.Dispose();
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        var conflict = running.Contains("firebird.exe") && (running.Contains("fbserver.exe") || running.Contains("fb_inet_server.exe"));
        return new EnvironmentCheckItem
        {
            CheckId = "firebird-process",
            Category = "Banco Firebird",
            Title = "Processos do Firebird",
            Status = conflict ? CheckHealthStatus.Critical : running.Count > 0 ? CheckHealthStatus.Healthy : CheckHealthStatus.Info,
            DetectedState = running.Count > 0 ? "Em execução: " + string.Join(", ", running) : "Nenhum processo do Firebird em execução.",
            Recommendation = conflict
                ? "Firebird 1.5/2.x (fbserver) e 3.0+ (firebird.exe) rodando juntos: configure portas diferentes para não derrubar o legado DATASUS."
                : running.Count > 0 ? "Nenhuma ação necessária." : "Normal em estações que não hospedam a base local.",
            LinkedKnowledgeErrorId = conflict ? "sia-ti-firebird" : null
        };
    }

    private EnvironmentCheckItem CheckMidas()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sysWow = Path.Combine(windows, "SysWOW64", "midas.dll");
        var system32 = Path.Combine(windows, "System32", "midas.dll");
        var inSysWow = File.Exists(sysWow);
        var inSystem32 = File.Exists(system32);

        return new EnvironmentCheckItem
        {
            CheckId = "midas",
            Category = "Bibliotecas Delphi",
            Title = "midas.dll (32 bits)",
            Status = inSysWow ? CheckHealthStatus.Healthy : CheckHealthStatus.Attention,
            DetectedState = inSysWow
                ? $"Presente em {sysWow}."
                : inSystem32 ? $"Só existe em {system32} (pasta de 64 bits)." : "midas.dll não encontrada no Windows.",
            Recommendation = inSysWow
                ? "Se ainda der 'Error loading midas.dll', registre-a com SysWOW64\\regsvr32.exe como Administrador."
                : "Copie midas.dll da pasta do sistema DATASUS para SysWOW64 e registre com C:\\Windows\\SysWOW64\\regsvr32.exe.",
            LinkedKnowledgeErrorId = inSysWow ? null : "sia-ti-bde"
        };
    }

    private static EnvironmentCheckItem CheckBde()
    {
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = new[]
        {
            Path.Combine(x86, "Common Files", "Borland Shared", "BDE", "IDAPI32.CFG"),
            Path.Combine(x86, "Borland", "Common Files", "BDE", "IDAPI32.CFG")
        };
        var found = candidates.FirstOrDefault(File.Exists);

        return new EnvironmentCheckItem
        {
            CheckId = "bde",
            Category = "Bibliotecas Delphi",
            Title = "BDE (Borland Database Engine)",
            Status = found is not null ? CheckHealthStatus.Healthy : CheckHealthStatus.Info,
            DetectedState = found is not null ? $"Configuração encontrada: {found}" : "BDE não encontrado.",
            Recommendation = found is not null
                ? "Se aparecer erro de memória compartilhada, ajuste SHAREDMEMLOCATION no BDE Administrator."
                : "Só é necessário para BPA-Mag/APAC-Mag/FPO antigos. Se eles abrem normalmente, ignore."
        };
    }

    private EnvironmentCheckItem CheckDiskSpace()
    {
        DriveInfo drive;
        try
        {
            drive = new DriveInfo(SystemDrive);
            _ = drive.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new EnvironmentCheckItem
            {
                CheckId = "disk",
                Category = "Disco",
                Title = "Espaço livre",
                Status = CheckHealthStatus.Info,
                DetectedState = $"Não foi possível ler o disco {SystemDrive}.",
                Recommendation = "—"
            };
        }

        const long fourGb = 4L * 1024 * 1024 * 1024;
        const long margin = 256L * 1024 * 1024;
        var free = drive.AvailableFreeSpace;
        var remainder = free % fourGb;
        // O BDE antigo lê o espaço livre em 32 bits: quando o resto da divisão por 4 GB é quase zero, ele "vê" disco cheio.
        var bdeTrap = remainder < margin;

        return new EnvironmentCheckItem
        {
            CheckId = "disk",
            Category = "Disco",
            Title = "Espaço livre e armadilha do BDE",
            Status = free < 2L * 1024 * 1024 * 1024 ? CheckHealthStatus.Critical : bdeTrap ? CheckHealthStatus.Attention : CheckHealthStatus.Healthy,
            DetectedState = $"{free / (1024d * 1024 * 1024):N1} GB livres em {drive.Name}.",
            Recommendation = free < 2L * 1024 * 1024 * 1024
                ? "Libere espaço: consistência e backup do SIA precisam de folga em disco."
                : bdeTrap
                    ? "O espaço livre está perto de um múltiplo de 4 GB, faixa em que o BDE acusa 'Insufficient disk space'. Crie ou apague um arquivo de ~500 MB."
                    : "Nenhuma ação necessária.",
            LinkedKnowledgeErrorId = bdeTrap ? "sia-ti-bde" : null
        };
    }
}
