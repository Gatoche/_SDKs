// Wps.AppLauncher — launcher natif (NativeAOT) devant l'AppHost réel d'une app
// wipiSoft standalone. Compilé UNE seule fois dans le SDK, puis copié et renommé
// par le post-build de chaque app (ex: OuiPDF.exe).
//
// Logique :
//   1. Lit son propre nom de fichier → déduit <AppName> (ex: "OuiPDF").
//   2. WpsDeployVerifier.Verify(deployDir, appName) → si KO : MessageBox + exit 1.
//   3. Tente Mutex.OpenExisting("wipiSoft.<AppName>.SingleInstance") :
//        - existe        → instance déjà lancée → envoie args via le pipe canonique
//                          ("wipiSoft.<AppName>.Pipe") et exit 0 (pas de fork inutile).
//        - n'existe pas  → spawn "<AppName>.app.exe" avec les args reçus, exit 0.
//
// Conventions canoniques (alignées avec le SDK) :
//   - AppHost réel       : <AppName>.app.exe
//   - Mutex single-inst. : wipiSoft.<AppName>.SingleInstance
//   - Pipe IPC           : wipiSoft.<AppName>.Pipe
//   - Protocole pipe     : 1 ligne par argument, le serveur lit jusqu'à EOF du stream.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using Wps.Module.Core;
using wipisoft;

namespace Wps.AppLauncher;

internal static class Program
{
    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [STAThread]
    static int Main(string[] args)
    {
        string appName = "Launcher"; // fallback pour le bloc catch
        try
        {
            var ownExePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Environment.ProcessPath null");
            var deployDir = Path.GetDirectoryName(ownExePath)
                ?? throw new InvalidOperationException("Impossible de déterminer deployDir");
            appName = Path.GetFileNameWithoutExtension(ownExePath);

            // AUMID canonique wipiSoft.<AppName> : meme valeur cote launcher et cote AppHost
            // → Windows regroupe les deux exe sous une seule icone taskbar (sinon la fenetre
            // de OuiPDF.app.exe creerait une icone distincte de OuiPDF.exe epinglee).
            WpsAppUserModelId.SetCurrentProcess(appName);

            // 1) Vérification de l'intégrité du déploiement (manifest + master guid)
            var verify = WpsDeployVerifier.Verify(deployDir, appName);
            if (!verify.IsValid)
            {
                TryLog($"Launcher: deploy invalid → {verify.DisplayMessage}", LogLevel.Error, appName);
                MessageBoxW(IntPtr.Zero,
                    $"Impossible de lancer {appName}.\n\n" +
                    $"Déploiement invalide :\n{verify.DisplayMessage}\n\n" +
                    "Merci de réinstaller l'application.",
                    $"{appName} — Déploiement corrompu",
                    MB_OK | MB_ICONERROR);
                return 1;
            }

            // 2) Single-instance : mutex canonique wipiSoft.<AppName>.SingleInstance
            var mutexName = $"wipiSoft.{appName}.SingleInstance";
            if (TryOpenExistingMutex(mutexName))
            {
                // Une instance tourne déjà → délègue les args via le pipe canonique.
                if (TrySendArgsViaPipe(appName, args))
                {
                    TryLog($"Launcher: instance existante → args délégués via pipe ({args.Length})",
                        LogLevel.Trace, appName);
                    return 0;
                }
                // L'envoi pipe a échoué (timing, pipe pas encore prêt, etc.) → tombe en mode spawn.
                TryLog("Launcher: pipe inaccessible, fallback spawn", LogLevel.Warning, appName);
            }

            // 3) Spawn de l'AppHost réel
            var realExe = Path.Combine(deployDir, $"{appName}.app.exe");
            if (!File.Exists(realExe))
            {
                TryLog($"Launcher: AppHost introuvable → {realExe}", LogLevel.Error, appName);
                MessageBoxW(IntPtr.Zero,
                    $"Impossible de lancer {appName}.\n\n" +
                    $"Fichier introuvable : {appName}.app.exe\n\n" +
                    "Merci de réinstaller l'application.",
                    $"{appName} — Déploiement incomplet",
                    MB_OK | MB_ICONERROR);
                return 2;
            }

            var psi = new ProcessStartInfo(realExe)
            {
                UseShellExecute = false,
                WorkingDirectory = deployDir,
            };
            // Token de provenance : permet à l'AppHost (via WpsAppGuard) de detecter
            // qu'il a bien ete lance par ce launcher et pas directement double-clique.
            psi.Environment[WpsAppGuard.LauncherTokenEnvVar] = WpsAppGuard.LauncherTokenValue;
            // Transmet les args tels quels (file associations, drag-drop, CLI).
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
            return 0;
        }
        catch (Exception ex)
        {
            TryLog($"Launcher: exception non gérée → {ex}", LogLevel.Error, appName);
            MessageBoxW(IntPtr.Zero,
                $"Erreur inattendue au démarrage :\n{ex.Message}",
                $"{appName} — Erreur Launcher",
                MB_OK | MB_ICONERROR);
            return 99;
        }
    }

    /// <summary>True si le mutex existe déjà (= une autre instance le détient).</summary>
    private static bool TryOpenExistingMutex(string mutexName)
    {
        try
        {
            using var m = Mutex.OpenExisting(mutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch
        {
            // UnauthorizedAccessException / etc. → on considère "pas joignable" et fallback spawn.
            return false;
        }
    }

    /// <summary>
    /// Notifie l'instance existante via le pipe canonique.
    /// Protocole : 1 ligne par argument, le serveur lit jusqu'à EOF du stream.
    ///
    /// <para>Une connexion vide (args vide) a un sens metier : c'est le signal
    /// "double-clic supplementaire sur &lt;App&gt;.exe sans fichier" → l'instance
    /// existante doit faire un BringToForeground. On ouvre donc TOUJOURS le pipe,
    /// meme avec args vide.</para>
    /// </summary>
    private static bool TrySendArgsViaPipe(string appName, string[] args)
    {
        var pipeName = $"wipiSoft.{appName}.Pipe";
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName,
                PipeDirection.Out, PipeOptions.Asynchronous);
            pipe.Connect(2000);
            using var w = new StreamWriter(pipe) { AutoFlush = true };
            foreach (var a in args)
            {
                if (string.IsNullOrEmpty(a)) continue;
                w.WriteLine(a);
            }
            // Si args est vide : le writer.Dispose() ferme le stream sans rien ecrire.
            // Le serveur lit 0 ligne → BringToForeground sans openPdfFile.
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryLog(string msg, LogLevel level, string appName)
    {
        try { WpsDebugSender.Log(msg, level, appName); } catch { /* fire-and-forget */ }
    }
}
