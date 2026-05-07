# WpsGenerateManifest.ps1
#
# Génère le manifest d'intégrité de déploiement pour un module wipiSoft :
#   - <AppName>.deploy.manifest.json : liste des fichiers du déploiement
#     avec leur SHA-256, plus métadonnées (kind, version, build time)
#   - <AppName>.deploy.guid : SHA-256 du manifest tronqué à 32 hex chars
#     (= 128 bits, format GUID-like). Sert de scellé rapide à comparer.
#
# Appelé en post-build par Wps.Module.Sdk.Core.targets après WpsDeployApp.
# Idempotent : si exécuté deux fois sur le même contenu, produit les mêmes fichiers.
#
# Le manifest et le guid eux-mêmes sont exclus du calcul (sinon dépendance
# circulaire). Les .pdb sont aussi exclus : ils ne sont pas requis au runtime
# et leur présence/absence ne doit pas invalider un déploiement.

param(
    [Parameter(Mandatory=$true)] [string]$DeployDir,
    [Parameter(Mandatory=$true)] [string]$AppName,
    [Parameter(Mandatory=$true)] [string]$Kind,
    [Parameter(Mandatory=$true)] [string]$Version
)

if (-not (Test-Path $DeployDir)) {
    Write-Host "WpsGenerateManifest: DeployDir introuvable, skip ($DeployDir)"
    exit 0
}

$manifestPath = Join-Path $DeployDir "$AppName.deploy.manifest.json"
$guidPath     = Join-Path $DeployDir "$AppName.deploy.guid"

# Liste tous les fichiers du déploiement, sauf les exclusions
$excludePatterns = @(
    "*.pdb",
    "$AppName.deploy.manifest.json",
    "$AppName.deploy.guid"
)

$files = Get-ChildItem -Path $DeployDir -File -Recurse | Where-Object {
    $name = $_.Name
    foreach ($p in $excludePatterns) {
        if ($name -like $p) { return $false }
    }
    return $true
} | Sort-Object FullName

# Calcule SHA-256 de chaque fichier, chemin relatif au DeployDir
$filesMap = [ordered]@{}
foreach ($f in $files) {
    $relPath = $f.FullName.Substring($DeployDir.Length).TrimStart('\','/').Replace('\','/')
    $hash = (Get-FileHash -Path $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $filesMap[$relPath] = $hash
}

# Construit le manifest
$manifest = [ordered]@{
    appName          = $AppName
    wipiModuleKind   = $Kind
    wipiModuleVersion = $Version
    buildTimeUtc     = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    files            = $filesMap
}

# Sérialise en JSON stable (pretty + ordered)
$manifestJson = $manifest | ConvertTo-Json -Depth 5

# Écrit en UTF-8 sans BOM (compatibilité runtime .NET pour relecture)
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, $utf8NoBom)

# Calcule le master guid = SHA-256 du manifest, tronqué à 32 hex chars (128 bits)
$manifestBytes = [System.Text.Encoding]::UTF8.GetBytes($manifestJson)
$sha256 = [System.Security.Cryptography.SHA256]::Create()
$hashBytes = $sha256.ComputeHash($manifestBytes)
$masterGuid = ([System.BitConverter]::ToString($hashBytes) -replace '-','').Substring(0, 32).ToLowerInvariant()
$sha256.Dispose()

[System.IO.File]::WriteAllText($guidPath, $masterGuid, $utf8NoBom)

Write-Host "WpsGenerateManifest: $AppName ($Kind v$Version) → $($filesMap.Count) fichiers, guid=$masterGuid"
