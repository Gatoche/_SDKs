# HOWTO — Transformer une app en ModuleService wipiSoft

Un **ModuleService** est une app **headless** (pas d'UI principale) qui expose des méthodes
invocables par un host (ModuloSlot, futur wipiTools) via IPC. Cas d'usage typique :
TracePML (surveille `pml.log` et émet des toasts), DymoPlus (impression d'étiquettes via
service Dymo), tout daemon de fond avec une fenêtre de paramétrage occasionnelle.

> Pour une app **avec UI principale** embedded dans un slot, utilise plutôt le SDK
> `Wps.Module.Sdk` et son [HOWTO dédié](../Wps.Module.Sdk/HOWTO.md).

## Pré-requis

- App **.NET 8** (`net8.0-windows`, `UseWPF=true` recommandé pour la fenêtre de paramétrage)
- **`OutputType=WinExe`** (pas `Exe`) — strictement silencieux, pas de fenêtre console
- Une logique métier qui peut tourner en arrière-plan (FileSystemWatcher, timer, listener…)
- (Optionnel) Une `MainWindow` qui sert de **fenêtre de paramétrage** ouverte à la demande

## Checklist

### 1. csproj

Ajouter dans `<PropertyGroup>` :

```xml
<OutputType>WinExe</OutputType>            <!-- pas Exe : aucune console au démarrage -->
<TargetFramework>net8.0-windows</TargetFramework>
<UseWPF>true</UseWPF>                      <!-- pour la settings window optionnelle -->

<!-- Convention wipiSoft : déclare l'app comme service headless invocable. -->
<WipiModuleKind>ModuleService</WipiModuleKind>
<WipiModuleName>NomCourt</WipiModuleName>
<WipiModuleVersion>1.0</WipiModuleVersion>
<!-- LGO compatibles (facultatif) — séparateur ; pour plusieurs valeurs. -->
<LGO>Winpharma</LGO>
<!-- Recommandé pour la détection wpsServices : -->
<Company>wipiSoft</Company>
```

Ajouter la référence SDK + l'import des targets unifiés :

```xml
<ItemGroup>
  <ProjectReference Include="..\..\..\_SDKs\Modules\Wps.ModuleService.Sdk\Wps.ModuleService.Sdk.csproj" />
</ItemGroup>

<!-- Auto-deploy post-build vers C:\_wipiSoft\Apps\Modules\<WipiModuleName>\
     + injection AssemblyMetadata. -->
<Import Project="..\..\..\_SDKs\Modules\Wps.ModuleService.Sdk\Wps.ModuleService.Sdk.targets" />
```

### 2. Stockage des données — convention wipiSoft

Toute donnée hors-registre du service (cache, fichiers générés, settings JSON,
extractions...) doit aller dans :

```
<RépertoireWipiSoft>\Data\<WipiModuleName>\        (par défaut C:\_wipiSoft\Data\<Name>\)
```

Source-link `WpsPaths.cs` dans le csproj :

```xml
<Compile Include="..\..\..\_libs\WpsPaths.cs" Link="Helpers\WpsPaths.cs" />
```

Et utilise-le partout où tu écris (le service tourne en background, donc même les
petits fichiers de cache/état doivent passer par là, pas dans `BaseDirectory`) :

```csharp
var dataDir  = wipisoft.WpsPaths.GetAppDataDir("MonService");
var stateDb  = Path.Combine(dataDir, "state.json");
var cacheDir = wipisoft.WpsPaths.GetAppDataSubDir("MonService", "cache");
```

> **Pourquoi pas `%LOCALAPPDATA%` ?** Par-utilisateur, invisible aux outils host.
> La convention wipiSoft regroupe tout sous une seule racine machine. Cf.
> en-tête de `_libs/WpsPaths.cs`. Pour les vrais secrets utilisateur, continuer
> à utiliser HKCU (`WpsHKCU`).

### 3. `Description.md` embarqué

Crée un `Description.md` à la racine du projet (titre, méthodes exposées avec signatures,
persistance HKCU). Puis :

```xml
<ItemGroup>
  <EmbeddedResource Include="Description.md" />
</ItemGroup>
```

### 4. App.xaml.cs — pattern dual-mode (embedded vs standalone)

Le service doit fonctionner **dans les deux modes** : sans `--wps-session` (mode debug
direct) et avec (mode pilotée par le host). Pattern de référence (extrait de TracePML) :

```csharp
using Wps.ModuleService;

public partial class App : Application
{
    private static Mutex? _mutex;
    private bool _mutexOwned;
    private MainWindow? _mainWindow;
    // ... services métier (parser, monitor, viewModel...)

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // OnStartup ne peut pas être async → fire-and-forget vers InitAsync.
        _ = InitAsync(e.Args);
    }

    private async Task InitAsync(string[] args)
    {
        // Auto-détecte le mode selon args (--wps-session présent ou non).
        // En standalone : no-op silencieux. En embedded : ouvre les pipes IPC + handshake.
        await WpsModuleService.BootstrapAsync(args);

        // Mutex singleton UNIQUEMENT en standalone (en embedded, le host gère l'unicité
        // via session ID → chaque launch = nouveau pipe).
        if (!WpsModuleService.IsEmbedded)
        {
            _mutex = new Mutex(true, "MonApp_SingleInstance", out _mutexOwned);
            if (!_mutexOwned) { Shutdown(); return; }
        }

        // ====== Init services métier (commun aux 2 modes) ======
        // var monitor = new MyMonitor();
        // var viewModel = new MyViewModel(...);
        // monitor.Start();

        // ====== MainWindow (créée mais pas affichée par défaut) ======
        // En embedded : exposée comme settings window.
        // En standalone : affichée selon une logique propre (DebugMode flag, systray...).
        _mainWindow = new MainWindow { /* DataContext = viewModel */ };

        // Empêche la fermeture du process : la window se cache, le service continue.
        _mainWindow.Closing += (s, ev) =>
        {
            if (s is Window w) { ev.Cancel = true; w.Hide(); }
        };

        if (WpsModuleService.IsEmbedded)
        {
            await ConfigureEmbeddedAsync();
        }
        else
        {
            ConfigureStandalone();   // ex: systray, fenêtre debug, etc.
        }
    }

    private async Task ConfigureEmbeddedAsync()
    {
        // Settings window : à chaque SHOW_SETTINGS du host, on retourne l'instance unique.
        // Le SDK marshalle automatiquement sur le UI thread + BringToForegroundAndClick
        // (force foreground via AttachThreadInput + clic synth pour stopper l'alerte taskbar).
        WpsModuleService.RegisterSettingsWindow(() => _mainWindow!);

        // Méthodes Invoke exposées au host (paramètres / résultat sérialisés en JSON).
        WpsModuleService.RegisterInvokeHandler<EmptyParams, MyStatusResult>(
            "GetStatus",
            async _ =>
            {
                await Task.CompletedTask;
                return new MyStatusResult { /* ... */ };
            });

        await WpsModuleService.NotifyReadyAsync();

        // Fire-and-forget : RunAsync bloque jusqu'à ce que le host envoie CLOSE ou que le
        // pipe soit coupé. Quand RunAsync complete, on shutdown l'app WPF proprement.
        _ = Task.Run(async () =>
        {
            await WpsModuleService.RunAsync();
            Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
        });
    }

    private void ConfigureStandalone()
    {
        // Comportement debug local : systray, fenêtre principale visible si DebugMode...
        // (Selon les besoins de l'app — totalement libre.)
    }
}

// ====== DTOs Invoke (sérialisés via System.Text.Json côté SDK) ======
public sealed class EmptyParams { }
public sealed class MyStatusResult { /* ... */ }
```

### 5. Méthodes Invoke à exposer

Dans `ConfigureEmbeddedAsync`, déclare les méthodes que ton service expose :

```csharp
WpsModuleService.RegisterInvokeHandler<TParams, TResult>(
    "MethodName",
    async (paramsTyped) =>
    {
        // ... logique métier ...
        return new TResult { /* ... */ };
    });
```

- `TParams` et `TResult` doivent être des **classes** sérialisables JSON
  (`System.Text.Json` côté SDK).
- En cas d'exception dans le handler, le SDK répond `INVOKE_RESULT|...|ERROR|message` au host.
- Le SDK exécute le handler depuis le ReadLoop IPC (ThreadPool) — si tu accèdes à des
  contrôles WPF, marshall via `Application.Current.Dispatcher`.

### 6. Mode Daemon (HKCU)

Pas de code à ajouter côté service : le host gère le flag Daemon via
`WpsServiceDaemonConfig` (clé `HKCU\Software\wipiSoft\<HostName>\Services\<Name>\Daemon`).

- Défaut : Daemon=true (lancé au démarrage du host)
- L'utilisateur peut désactiver via le toggle dans la pageslot du service
- Si Daemon=false : le service n'est lancé que sur clic explicite "Redémarrer" dans la pageslot

### 7. (v1.3, recommandé) Shutdown négocié via `IWpsModule`

Depuis le contrat **v1.3**, le SDK ModuleService supporte la négociation d'arrêt côté
service comme côté Module classique. Le service peut :

1. Implémenter `Wps.Module.IWpsModule` sur sa classe `App` (ou une classe dédiée)
2. Appeler `WpsModuleService.Register(this)` au début de `ConfigureEmbeddedAsync`

Hooks principaux :

- **`OnCanCloseRequestedAsync(ctx)`** : phase 1 du shutdown. Faire le cleanup synchrone
  rapide (ABM_REMOVE, flush BDD courte, etc.) **avant de répondre Ok**. Pour un cleanup
  long, retourner `Busy(estimatedMs)` + `ReportBusyProgress` périodique + `ResolveCanClose(Ok)`
  à la fin.
- **`OnShutdownRequested()`** : phase finale après notre Ok. Le cleanup applicatif est
  déjà fait dans le hook précédent ; ici juste `Application.Shutdown()` (ou laisser tomber
  si pas d'Application WPF — le SDK exit le process).
- **`OnHostDisconnected(reason)`** : pipe coupé ou silence PING > 30s. Filet pour les cas
  où le host crashe sans envoyer CAN_CLOSE. Cleanup d'urgence + `Application.Shutdown()`
  (le SDK ne le fait PAS automatiquement côté ModuleService — c'est différent du Module
  classique car le ModuleService peut être console pure sans Application WPF ; il faut
  appeler `Shutdown` explicitement si pertinent).

Exemple concret (extrait simplifié de MiniBoard) :

```csharp
public partial class App : Application, IWpsModule
{
    public ValueTask<CanCloseDecision> OnCanCloseRequestedAsync(CanCloseContext ctx)
    {
        // Cleanup synchrone rapide AVANT de répondre Ok au host. Évite que Kill fallback
        // shoote le cleanup applicatif si jamais grace expire (~7s default).
        AppBarManager.Unregister();
        return new ValueTask<CanCloseDecision>(CanCloseDecision.Ok);
    }

    public void OnShutdownRequested()
    {
        // CLOSE reçu après notre Ok → Application.Shutdown.
        Application.Current?.Shutdown();
    }

    public void OnHostDisconnected(HostDisconnectReason reason)
    {
        // Filet : host crashed/frozen, on n'aura pas eu de CAN_CLOSE négocié.
        AppBarManager.Unregister();  // idempotent
        Application.Current?.Shutdown();
    }

    private async Task ConfigureEmbeddedAsync()
    {
        WpsModuleService.Register(this);  // ← branche les hooks ci-dessus

        WpsModuleService.RegisterSettingsWindow(() => _mainWindow!);
        WpsModuleService.RegisterInvokeHandler<...>("...");

        await WpsModuleService.NotifyReadyAsync();
        _ = Task.Run(async () =>
        {
            await WpsModuleService.RunAsync();
            Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
        });
    }
}
```

APIs publiques v1.3 supplémentaires :

- `WpsModuleService.ReportBusyProgress(BusyProgress)` pour les Busy longs
- `WpsModuleService.NotifySelfClosing(reason)` quand le service se ferme à son initiative
  (bouton Quitter dans la settings window, etc.) — permet au host de griser proprement
- `WpsModuleService.ResolveCanClose(decision)` pour résoudre un Busy/NeedUser asynchrone

## Test

### Standalone

```
cd MonApp\bin\x64\Debug\net8.0-windows
.\MonApp.exe
```

L'app démarre silencieusement (WinExe). En standalone, ton `ConfigureStandalone()` peut
afficher un systray ou une fenêtre debug. **Pas de Ctrl+C disponible** (WinExe → pas de
console attachée). Pour terminer en debug : Task Manager.

### Embedded (via host)

Lance ton host (ex: ModuloSlot.Host). Le panneau Services montre ton service avec son icône
et description. Au démarrage, si Daemon=true (défaut), il est auto-lancé. Cliquer sur sa
pageslot → tu vois le statut (Running/Stopped), boutons Paramètres/Redémarrer/Arrêter.

## Pièges courants

- **`OutputType=Exe` au lieu de `WinExe`** → fenêtre console noire visible au démarrage en
  embedded (pollue l'UX du host). Toujours `WinExe`.
- **Application.Current.Shutdown() à la fermeture de la window** → le service meurt dès
  que l'user ferme la settings window. Toujours intercepter `Closing` → `e.Cancel = true; w.Hide()`.
- **Mutex singleton actif en embedded** → empêche le host de relancer le service après un
  Stop. Toujours conditionnel `if (!WpsModuleService.IsEmbedded)`.
- **Méthodes Invoke qui touchent des contrôles WPF sans marshaller** → cross-thread
  exception silencieuse. Marshall via `Application.Current.Dispatcher.Invoke(...)` avant
  d'accéder à des `TextBlock`/`ListBox`/etc.
- **Pas de `RunAsync` fire-and-forget** → le service ne reçoit jamais le CLOSE du host
  proprement et reste orphelin à la fermeture du host (rattrapé par
  `SweepOrphanModuleServiceProcesses` au prochain démarrage, mais c'est sale).
- **Manque `NotifyReadyAsync`** → le host considère le service "non prêt", impossible
  d'invoquer ses méthodes (le SDK rejette `InvokeAsync` tant que `IsReady=false` côté client).
- **(v1.3) Cleanup applicatif dans `OnShutdownRequested` au lieu de `OnCanCloseRequestedAsync`**
  → si le cleanup est rapide (< 50ms), c'est OK. Mais s'il est long, le faire proactivement
  dans `OnCanCloseRequestedAsync` AVANT de répondre Ok. C'est ce qui résout le bug
  historique MiniBoard (ABM_REMOVE arrivait après le Kill côté Explorer).
- **(v1.3) Oubli de `WpsModuleService.Register(this)` dans `ConfigureEmbeddedAsync`** → les
  hooks `IWpsModule` ne sont pas appelés, le SDK utilise les DIMs (Ok par défaut). Le service
  fonctionne mais sans cleanup négocié — on retombe sur le pattern legacy avec event
  `ShutdownRequested` levé par le SDK et débloquant `RunAsync`.
- **(v1.3) Retour `Busy(estimatedMs)` sans envoyer de `BUSY_PROGRESS`** → le host considère
  le service figé après ~8s de silence et passe au Kill. Toujours envoyer
  `WpsModuleService.ReportBusyProgress(...)` toutes les ~3s pendant un Busy.
- **(v1.3) Oubli de `WpsModuleService.ResolveCanClose(...)` après un Busy/NeedUser** → host
  reste en attente jusqu'au timeout, puis Kill. Toujours résoudre par Ok ou Rejected.
