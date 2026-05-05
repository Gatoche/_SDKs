namespace Wps.Module.Core;

/// <summary>
/// Helpers de validation de la version du contrat module ↔ host. Format attendu : <c>"major.minor"</c>
/// (ex: "1.0", "1.1"). Politique semver lax du contrat wipiSoft :
/// <list type="bullet">
///   <item><b>Major identique</b> : breaking change → un mismatch major rejette la connexion</item>
///   <item><b>Mineur additif</b> : le mineur du peer (module ou ModuleService) doit être ≤ mineur
///         du host. Le host plus récent accepte les peers plus anciens (rétrocompat ascendante)
///         mais pas l'inverse.</item>
/// </list>
/// </summary>
public static class WpsContractVersion
{
    /// <summary>Retourne <c>true</c> si la version annoncée par le peer est compatible avec la
    /// version du host. Format attendu "major.minor" pour les deux ; tout autre format → faux.</summary>
    public static bool IsCompatible(string peerVersion, string hostVersion)
    {
        if (!TryParseSemver(peerVersion, out int pMaj, out int pMin)) return false;
        if (!TryParseSemver(hostVersion, out int hMaj, out int hMin)) return false;
        return pMaj == hMaj && pMin <= hMin;
    }

    /// <summary>Parse <c>"major.minor"</c>. Renvoie <c>true</c> si parse OK, sinon <c>false</c>
    /// avec <paramref name="major"/>/<paramref name="minor"/> à 0.</summary>
    public static bool TryParseSemver(string v, out int major, out int minor)
    {
        major = 0; minor = 0;
        if (string.IsNullOrEmpty(v)) return false;
        var parts = v.Split('.');
        if (parts.Length < 2) return false;
        return int.TryParse(parts[0], out major) && int.TryParse(parts[1], out minor);
    }
}
