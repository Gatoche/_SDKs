using Microsoft.Win32;

namespace Wps.Module.Hosting;

/// <summary>
/// Compteur d'usage des modules persisté en HKCU. Utilisé pour trier la sidebar par
/// fréquence d'utilisation (les plus ouverts en haut) — fallback alpha pour les modules
/// jamais ouverts. Exposé statique pour appel facile depuis le host.
///
/// Stockage : <c>HKCU\Software\wipiSoft\ModuloSlot\ModuleUsage\{moduleName}</c> → DWORD UseCount
/// </summary>
public static class WpsModuleUsageTracker
{
    private const string KeyRoot = @"Software\wipiSoft\ModuloSlot\ModuleUsage";

    /// <summary>Lit le compteur d'usage du module. 0 si jamais ouvert.</summary>
    public static int GetCount(string moduleName)
    {
        if (string.IsNullOrEmpty(moduleName)) return 0;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyRoot);
            if (key is null) return 0;
            var v = key.GetValue(moduleName);
            return v is int i ? i : 0;
        }
        catch { return 0; }
    }

    /// <summary>Incrémente le compteur d'usage du module. Crée la clé HKCU si absente.</summary>
    public static void Increment(string moduleName)
    {
        if (string.IsNullOrEmpty(moduleName)) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyRoot);
            if (key is null) return;
            int current = key.GetValue(moduleName) is int i ? i : 0;
            key.SetValue(moduleName, current + 1, RegistryValueKind.DWord);
        }
        catch { /* best-effort, pas critique */ }
    }
}
