using System.Diagnostics;
using System.Management;

namespace Wps.Module.Hosting;

/// <summary>
/// Lance un process détaché du Host via WMI <c>Win32_Process.Create</c>. Le ParentPID du
/// process lancé devient <c>WmiPrvSE.exe</c> (et non Host.exe) → plus de relation
/// parent-enfant dans le Task Manager, les modules apparaissent comme process standalone.
///
/// Fallback sur <see cref="Process.Start"/> classique si WMI échoue (services arrêtés, droits
/// insuffisants). Dans ce cas Host redevient le parent mais l'app fonctionne.
///
/// Ce comportement détaché évite que la fermeture du Host n'entraîne automatiquement la
/// fermeture des modules (cas Windows job-object hiérarchique). Le Host garde quand même
/// la référence <see cref="Process"/> retournée pour kill explicite à la fermeture.
/// </summary>
internal static class WpsDetachedProcessLauncher
{
    /// <summary>
    /// Lance <paramref name="exePath"/> avec les <paramref name="arguments"/> donnés. L'appel
    /// est synchrone et prend ~150ms (latence WMI) — à wrapper dans <c>Task.Run</c> côté
    /// appelant pour ne pas bloquer l'UI thread.
    /// </summary>
    public static Process Launch(string exePath, string arguments, string workingDirectory)
    {
        try
        {
            using var processClass = new ManagementClass(@"\\.\root\cimv2:Win32_Process");
            using var inParams = processClass.GetMethodParameters("Create");
            inParams["CommandLine"] = $"\"{exePath}\" {arguments}";
            inParams["CurrentDirectory"] = workingDirectory;

            using var outParams = processClass.InvokeMethod("Create", inParams, null);
            uint retVal = (uint)outParams["ReturnValue"];
            if (retVal != 0)
                return StartClassic(exePath, arguments, workingDirectory);

            uint pid = (uint)outParams["ProcessId"];
            return Process.GetProcessById((int)pid);
        }
        catch
        {
            return StartClassic(exePath, arguments, workingDirectory);
        }
    }

    private static Process StartClassic(string exePath, string arguments, string workingDirectory)
    {
        var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory,
            },
        };
        p.Start();
        return p;
    }
}
