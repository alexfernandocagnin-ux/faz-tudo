# Conserta SIA · CPD — Doutor TI
# Examina este computador para os sistemas do DATASUS (SIA, BPA-Mag, APAC-Mag, SCNES).
# SÓ LÊ: não altera, não instala e não apaga nada. Gera um arquivo na Área de Trabalho para enviar ao site.

$ErrorActionPreference = 'SilentlyContinue'
$pastas = 'SIA','BPA','APAC','SCNES','FPO','RAAS'
$raiz = [IO.Path]::GetPathRoot([Environment]::GetFolderPath('Windows'))
$progFiles = @([Environment]::GetFolderPath('ProgramFiles'), [Environment]::GetFolderPath('ProgramFilesX86')) | Where-Object { $_ } | Select-Object -Unique

$naRaiz = @($pastas | Where-Object { Test-Path (Join-Path $raiz $_) })
$emProgramFiles = @(foreach ($pf in $progFiles) { foreach ($p in $pastas) { if (Test-Path (Join-Path $pf $p)) { "$(Split-Path $pf -Leaf)\$p" } } })

$vs = Join-Path $env:LOCALAPPDATA 'VirtualStore'
$virtualStoreExiste = Test-Path $vs
$virtualStoreDatasus = @()
if ($virtualStoreExiste) {
    $virtualStoreDatasus = @(Get-ChildItem $vs -Directory -Recurse -Depth 2 | Where-Object { $pastas -contains $_.Name } |
        ForEach-Object { $_.FullName.Substring($vs.Length).TrimStart('\') })
}

$porta3050 = $false
try {
    $c = New-Object Net.Sockets.TcpClient
    $porta3050 = $c.ConnectAsync('127.0.0.1', 3050).Wait(2000) -and $c.Connected
    $c.Close()
} catch { $porta3050 = $false }

$processos = @('fbserver','fb_inet_server','fbguard','firebird','ibserver' | Where-Object { Get-Process -Name $_ })

$win = [Environment]::GetFolderPath('Windows')
$bdeArquivos = @("${env:ProgramFiles(x86)}\Common Files\Borland Shared\BDE\IDAPI32.CFG",
                 "${env:ProgramFiles(x86)}\Borland\Common Files\BDE\IDAPI32.CFG")
$disco = Get-PSDrive -Name $raiz.Substring(0,1)

$resultado = [ordered]@{
    ferramenta = 'conserta-sia-doutor-ti'
    versao = 1
    gerado_em = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss')
    windows = (Get-CimInstance Win32_OperatingSystem).Caption
    raiz = $raiz
    pastas_na_raiz = $naRaiz
    pastas_em_program_files = $emProgramFiles
    virtualstore_existe = $virtualStoreExiste
    virtualstore_datasus = $virtualStoreDatasus
    porta_3050_aberta = $porta3050
    processos_firebird = $processos
    midas_syswow64 = Test-Path "$win\SysWOW64\midas.dll"
    midas_system32 = Test-Path "$win\System32\midas.dll"
    bde_encontrado = [bool]($bdeArquivos | Where-Object { Test-Path $_ })
    disco_livre_bytes = [int64]$disco.Free
}

$saida = Join-Path ([Environment]::GetFolderPath('Desktop')) 'doutor-ti-resultado.json'
$resultado | ConvertTo-Json | Set-Content -Path $saida -Encoding UTF8
Write-Host ''
Write-Host "Pronto! Arquivo gerado: $saida" -ForegroundColor Green
Write-Host 'Agora envie esse arquivo na página Doutor TI do site.'
