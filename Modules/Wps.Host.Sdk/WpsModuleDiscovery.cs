using System.IO;
using System.Windows.Media;

namespace Wps.Module.Hosting;

/// <summary>
/// Représente un module wipiSoft découvert dans le dossier de déploiement.
/// </summary>
public sealed class WpsDiscoveredModule
{
    /// <summary>Nom court du module — valeur authentique lue depuis le PE de l'exe
    /// (<c>AssemblyMetadata("WipiModuleName")</c> injecté par le SDK targets).
    /// Utilisé pour les CLI <c>--show NomModule</c> et la déduplication.</summary>
    public required string Name { get; init; }

    /// <summary>Chemin absolu vers l'exécutable du module.</summary>
    public required string ExePath { get; init; }

    /// <summary>Nom d'affichage UI (depuis AssemblyTitle PE → fallback Name).</summary>
    public required string DisplayName { get; init; }

    /// <summary>Icône native du module (depuis le PE de l'exe).</summary>
    public ImageSource? Icon { get; init; }

    /// <summary>Version du module (depuis FileVersionInfo PE).</summary>
    public required string Version { get; init; }

    /// <summary>Description Markdown (depuis Description.md embarqué dans l'assembly).</summary>
    public required string Description { get; init; }

    /// <summary>Type de l'app wipiSoft (Module ou ModuleService). Lu depuis le PE
    /// (<c>AssemblyMetadata("WipiModuleKind")</c>). Défaut <see cref="WpsModuleKind.Module"/>
    /// si l'attribut est absent (apps construites avant la convention 1.1).</summary>
    public WpsModuleKind Kind { get; init; } = WpsModuleKind.Module;

    /// <summary>Liste des LGO (Logiciel de Gestion d'Officine) compatibles avec l'app, lue
    /// depuis le PE (<c>AssemblyMetadata("WipiLGO")</c>, valeurs séparées par <c>;</c>).
    /// Tableau vide si non défini par l'app. Permet au host d'afficher un badge "Winpharma"
    /// ou de filtrer la liste selon l'environnement utilisateur.</summary>
    public IReadOnlyList<string> LGO { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Scan un dossier de déploiement pour découvrir les modules wipiSoft installés.
/// Convention :
///   <c>{deployRoot}\{anyFolderName}\{anyFolderName}.exe</c> (ou 1er .exe trouvé)
///
/// L'identité d'un module = <c>WipiModuleName</c> lu depuis le PE de l'exe (AssemblyMetadata).
/// Si l'exe n'expose pas ce metadata, le module est ignoré + log warning (= pas un module
/// wipiSoft conforme, ou exe corrompu).
///
/// Déduplication par <c>WipiModuleName</c> :
///   - Date modif EXE différente → garde le plus récent (l'ancien est skippé)
///   - EXE identique, dossier parent différent → garde le dossier parent le PLUS ANCIEN
///   - Tout identique → skip le nouveau (on garde le 1er trouvé)
///
/// Le déploiement automatique vers ce dossier est géré par le post-build du SDK
/// (<c>Wps.Module.Sdk.targets</c>). Default : <c>C:\_wipiSoft\Apps\Modules\</c>.
/// </summary>
public static class WpsModuleDiscovery
{
    /// <summary>Dossier de déploiement par défaut (override possible via paramètre).</summary>
    public const string DefaultDeployRoot = @"C:\_wipiSoft\Apps\Modules";

    private const string LogTag = "Wps.Host.Sdk";

    /// <summary>
    /// Scan le dossier de déploiement et retourne la liste des modules trouvés (dédupliqués
    /// par <see cref="WpsDiscoveredModule.Name"/>), triés par nom. Lit les métadonnées de
    /// chaque module via <see cref="wipisoft.WpsModuleMetadata.Read"/> (PE + Description.md).
    /// Modules sans .exe ou sans <c>WipiModuleName</c> dans le PE sont ignorés silencieusement
    /// (log warning).
    /// </summary>
    public static IReadOnlyList<WpsDiscoveredModule> Scan(string? deployRoot = null)
    {
        var root = deployRoot ?? DefaultDeployRoot;
        if (!Directory.Exists(root)) return Array.Empty<WpsDiscoveredModule>();

        // Map WipiModuleName → candidat sélectionné (selon règle de déduplication).
        var byName = new Dictionary<string, WpsDiscoveredModule>(StringComparer.OrdinalIgnoreCase);
        // Pour la déduplication on a besoin des dates EXE et dossier parent du candidat actuel.
        var dedupInfo = new Dictionary<string, (DateTime ExeMtime, DateTime ParentCtime)>(StringComparer.OrdinalIgnoreCase);

        foreach (var subdir in Directory.GetDirectories(root))
        {
            var folderName = Path.GetFileName(subdir);

            // Heuristique : 1er .exe du sous-dossier
            var exe = Directory.GetFiles(subdir, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (string.IsNullOrEmpty(exe))
            {
                WpsDebugSender.Log($"WpsModuleDiscovery: dossier '{folderName}' ignoré (pas d'exe)", LogLevel.Trace, LogTag);
                continue;
            }

            // Lit metadata via le helper partagé (PE + Description.md + AssemblyMetadata)
            var meta = wipisoft.WpsModuleMetadata.Read(exe);

            // Identité du module = WipiModuleName du PE. Sans ça, ce n'est PAS un module wipiSoft
            // conforme (pas buildé via Wps.Module.Sdk.targets) → log + skip (on ne charge pas).
            if (string.IsNullOrEmpty(meta.WipiModuleName))
            {
                WpsDebugSender.Log($"WpsModuleDiscovery: '{folderName}' ignoré (PE ne contient pas AssemblyMetadata WipiModuleName, pas un module wipiSoft conforme)",
                    LogLevel.Warning, LogTag);
                continue;
            }

            // Parse le WipiModuleKind du PE (vide pour apps anciennes pré-1.1 → Module par défaut).
            var kind = WpsModuleKind.Module;
            if (!string.IsNullOrEmpty(meta.WipiModuleKind)
                && Enum.TryParse<WpsModuleKind>(meta.WipiModuleKind, ignoreCase: true, out var parsedKind))
                kind = parsedKind;

            var candidate = new WpsDiscoveredModule
            {
                Name = meta.WipiModuleName,
                ExePath = exe,
                DisplayName = string.IsNullOrEmpty(meta.DisplayName) ? meta.WipiModuleName : meta.DisplayName,
                Icon = meta.Icon,
                Version = meta.Version ?? "",
                Description = meta.Description ?? "",
                Kind = kind,
                LGO = meta.WipiLGO,
            };

            DateTime exeMtime, parentCtime;
            try
            {
                exeMtime = File.GetLastWriteTimeUtc(exe);
                parentCtime = Directory.GetCreationTimeUtc(subdir);
            }
            catch
            {
                WpsDebugSender.Log($"WpsModuleDiscovery: '{folderName}' ignoré (lecture date système échouée)", LogLevel.Warning, LogTag);
                continue;
            }

            if (!byName.TryGetValue(meta.WipiModuleName, out var existing))
            {
                // Premier candidat pour ce WipiModuleName → on l'ajoute
                byName[meta.WipiModuleName] = candidate;
                dedupInfo[meta.WipiModuleName] = (exeMtime, parentCtime);
                continue;
            }

            // Doublon détecté : applique règle de déduplication
            var existingInfo = dedupInfo[meta.WipiModuleName];
            int cmpExe = exeMtime.CompareTo(existingInfo.ExeMtime);
            bool replace;
            string reason;

            if (cmpExe > 0)
            {
                // Le nouveau a un exe plus récent → remplace
                replace = true;
                reason = $"exe plus récent ({exeMtime:yyyy-MM-dd HH:mm:ss} > {existingInfo.ExeMtime:yyyy-MM-dd HH:mm:ss})";
            }
            else if (cmpExe < 0)
            {
                // L'existant a un exe plus récent → skip nouveau
                replace = false;
                reason = $"exe plus ancien ({exeMtime:yyyy-MM-dd HH:mm:ss} < {existingInfo.ExeMtime:yyyy-MM-dd HH:mm:ss}) — skip";
            }
            else
            {
                // Dates EXE identiques → on regarde le dossier parent (le plus ancien gagne)
                int cmpParent = parentCtime.CompareTo(existingInfo.ParentCtime);
                if (cmpParent < 0)
                {
                    // Nouveau dossier plus ancien → remplace
                    replace = true;
                    reason = $"exe identique, dossier parent plus ancien ({parentCtime:yyyy-MM-dd HH:mm:ss} < {existingInfo.ParentCtime:yyyy-MM-dd HH:mm:ss})";
                }
                else if (cmpParent > 0)
                {
                    replace = false;
                    reason = $"exe identique, dossier parent plus récent ({parentCtime:yyyy-MM-dd HH:mm:ss} > {existingInfo.ParentCtime:yyyy-MM-dd HH:mm:ss}) — skip";
                }
                else
                {
                    // Tout identique → skip le nouveau (on garde celui déjà dans la liste)
                    replace = false;
                    reason = "tout identique (exe + dossier parent) — skip";
                }
            }

            WpsDebugSender.Log(
                $"WpsModuleDiscovery: doublon '{meta.WipiModuleName}' " +
                $"({Path.GetFileName(Path.GetDirectoryName(existing.ExePath))} vs {folderName}) → {reason}",
                LogLevel.Warning, LogTag);

            if (replace)
            {
                byName[meta.WipiModuleName] = candidate;
                dedupInfo[meta.WipiModuleName] = (exeMtime, parentCtime);
            }
        }

        return byName.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
