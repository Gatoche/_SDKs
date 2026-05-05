using Microsoft.Win32;

namespace Wps.Module.Hosting;

/// <summary>
/// Persistance HKCU du flag "Démarrer au lancement" (Daemon) pour les ModuleService.
/// Utilisable par n'importe quel host wipiSoft (ModuloSlot, futur wipiTools, etc.) — le
/// nom du host est injecté à chaque appel pour préfixer la clé HKCU.
///
/// <para>Convention : <c>HKCU\Software\wipiSoft\&lt;HostName&gt;\Services\&lt;ServiceName&gt;\Daemon</c>
/// (DWORD 0/1).</para>
///
/// <para>Politique par défaut : un service jamais configuré (clé absente) est considéré
/// <b>Daemon=true</b> — lancé au démarrage du host. L'utilisateur peut désactiver via le
/// toggle dans la pageslot du service.</para>
/// </summary>
public static class WpsServiceDaemonConfig
{
    private const string KeyRootBase = @"Software\wipiSoft";

    private static string KeyPath(string hostName, string serviceName) =>
        $@"{KeyRootBase}\{hostName}\Services\{serviceName}";

    /// <summary>True si le service doit être lancé au démarrage du host. Défaut <c>true</c>
    /// si la clé n'existe pas encore (service jamais ouvert dans le panneau Services).</summary>
    public static bool IsDaemonEnabled(string hostName, string serviceName)
    {
        if (string.IsNullOrEmpty(hostName) || string.IsNullOrEmpty(serviceName)) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath(hostName, serviceName));
            if (key is null) return true;  // jamais configuré → défaut ON
            var v = key.GetValue("Daemon");
            if (v is int i) return i != 0;
            return true;
        }
        catch { return true; }
    }

    /// <summary>Persiste le flag Daemon. Crée la clé si absente.</summary>
    public static void SetDaemonEnabled(string hostName, string serviceName, bool enabled)
    {
        if (string.IsNullOrEmpty(hostName) || string.IsNullOrEmpty(serviceName)) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath(hostName, serviceName));
            key?.SetValue("Daemon", enabled ? 1 : 0, RegistryValueKind.DWord);
        }
        catch { /* HKCU readonly extrême : on ignore, le défaut s'appliquera */ }
    }
}
