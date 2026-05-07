using System.Security.Cryptography;
using System.Text.Json;

namespace Wps.Module.Core;

/// <summary>
/// Vérifie l'intégrité d'un déploiement de module/moduleservice wipiSoft contre
/// son manifest (généré par <c>WpsGenerateManifest.ps1</c> en post-build).
///
/// <para>Utilisé côté host (ModuloSlot, futur wipiTools) avant de lancer un module :
/// si la vérification échoue, le host doit refuser de lancer et notifier
/// l'utilisateur en zone d'erreur. Évite les dialogues runtime cryptiques en cas
/// de déploiement incomplet (fichier manquant suite à un push interrompu, etc.).</para>
///
/// <para>Pourquoi un manifest plutôt qu'une simple vérification de présence de
/// fichier : on détecte aussi les fichiers <b>corrompus</b> (transfert tronqué,
/// antivirus qui modifie un DLL, etc.) — pas juste les fichiers <b>absents</b>.</para>
/// </summary>
public static class WpsDeployVerifier
{
    /// <summary>Résultat d'une vérification.</summary>
    public sealed class VerifyResult
    {
        /// <summary>True si tout est conforme.</summary>
        public bool IsValid { get; init; }

        /// <summary>Cause de l'échec (null si IsValid). Texte court pour UI :
        /// "Manifest absent", "Fichier manquant", "Fichier corrompu", etc.</summary>
        public string? FailureReason { get; init; }

        /// <summary>Nom du fichier coupable (relatif au DeployDir), si applicable.</summary>
        public string? FailingFile { get; init; }

        /// <summary>Message complet pour log/UI, prêt à afficher.
        /// Format : "<FailureReason> : <FailingFile>" ou juste "<FailureReason>".</summary>
        public string DisplayMessage =>
            FailingFile is null ? (FailureReason ?? "OK") : $"{FailureReason} : {FailingFile}";
    }

    /// <summary>DTO du manifest (correspond au JSON produit par WpsGenerateManifest.ps1).</summary>
    public sealed class DeployManifest
    {
        public string AppName { get; set; } = "";
        public string WipiModuleKind { get; set; } = "";
        public string WipiModuleVersion { get; set; } = "";
        public string BuildTimeUtc { get; set; } = "";
        public Dictionary<string, string> Files { get; set; } = new();
    }

    /// <summary>
    /// Vérifie le déploiement <paramref name="deployDir"/> pour l'app <paramref name="appName"/>.
    /// </summary>
    /// <param name="deployDir">Dossier de déploiement contenant l'exe et ses dépendances.</param>
    /// <param name="appName">Nom court de l'app (= WipiModuleName du csproj). Sert à
    /// localiser les fichiers <c>{appName}.deploy.manifest.json</c> et
    /// <c>{appName}.deploy.guid</c>.</param>
    public static VerifyResult Verify(string deployDir, string appName)
    {
        var manifestPath = Path.Combine(deployDir, $"{appName}.deploy.manifest.json");
        var guidPath = Path.Combine(deployDir, $"{appName}.deploy.guid");

        // 1) Manifest présent ?
        if (!File.Exists(manifestPath))
            return new()
            {
                IsValid = false,
                FailureReason = "Manifest absent",
                FailingFile = $"{appName}.deploy.manifest.json"
            };

        // 2) Master guid présent ?
        if (!File.Exists(guidPath))
            return new()
            {
                IsValid = false,
                FailureReason = "Master guid absent",
                FailingFile = $"{appName}.deploy.guid"
            };

        // 3) Le master guid scelle bien le manifest courant ?
        byte[] manifestBytes;
        try { manifestBytes = File.ReadAllBytes(manifestPath); }
        catch (Exception ex)
        {
            return new() { IsValid = false, FailureReason = $"Manifest illisible ({ex.Message})" };
        }

        var expectedMasterGuid = File.ReadAllText(guidPath).Trim().ToLowerInvariant();
        var actualMasterGuid = ComputeMasterGuid(manifestBytes);
        if (actualMasterGuid != expectedMasterGuid)
            return new()
            {
                IsValid = false,
                FailureReason = "Manifest corrompu (master guid mismatch)"
            };

        // 4) Désérialise le manifest
        DeployManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<DeployManifest>(manifestBytes,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            return new() { IsValid = false, FailureReason = $"Manifest invalide ({ex.Message})" };
        }
        if (manifest is null || manifest.Files.Count == 0)
            return new() { IsValid = false, FailureReason = "Manifest vide" };

        // 5) Chaque fichier listé doit exister et avoir le bon hash
        foreach (var (relPath, expectedHash) in manifest.Files)
        {
            var fullPath = Path.Combine(deployDir, relPath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(fullPath))
                return new()
                {
                    IsValid = false,
                    FailureReason = "Fichier manquant",
                    FailingFile = relPath
                };

            string actualHash;
            try
            {
                using var fs = File.OpenRead(fullPath);
                actualHash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            }
            catch (Exception ex)
            {
                return new()
                {
                    IsValid = false,
                    FailureReason = $"Fichier illisible ({ex.Message})",
                    FailingFile = relPath
                };
            }

            if (actualHash != expectedHash.ToLowerInvariant())
                return new()
                {
                    IsValid = false,
                    FailureReason = "Fichier corrompu",
                    FailingFile = relPath
                };
        }

        return new() { IsValid = true };
    }

    /// <summary>
    /// Calcule le master guid du manifest : SHA-256 des bytes du fichier manifest,
    /// tronqué aux 32 premiers caractères hex (= 128 bits, format GUID-like).
    /// Doit rester aligné avec la logique du PS1 <c>WpsGenerateManifest.ps1</c>.
    /// </summary>
    private static string ComputeMasterGuid(byte[] manifestBytes)
    {
        var hash = SHA256.HashData(manifestBytes);
        return Convert.ToHexString(hash).Substring(0, 32).ToLowerInvariant();
    }
}
