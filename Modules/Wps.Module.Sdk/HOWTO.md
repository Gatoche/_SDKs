# HOWTO — Transformer une app WPF en Module wipiSoft

Un **Module** est une app WPF avec UI principale qui s'intègre dans un host orchestrateur
(ModuloSlot, futur wipiTools) en tant qu'**onglet/slot** : la fenêtre du module est embarquée
dans le host via parking HWND cross-process. Le module reste une app standalone — il ne
sait pas qu'il est dans un host (sauf via le SDK qui détecte `--wps-session`).

> Pour une app **headless invocable** (sans UI principale), utilise plutôt le SDK
> `Wps.ModuleService.Sdk` et son [HOWTO dédié](../Wps.ModuleService.Sdk/HOWTO.md).

## Pré-requis

- App **WPF .NET 8** (`net8.0-windows`, `UseWPF=true`, `OutputType=WinExe`)
- Une `MainWindow` qui devient prête à un moment précis (`Loaded`, `WebView2.EnsureCoreWebView2Async`,
  `CefSharp.FrameLoadEnd`, etc.) — c'est ce moment qu'on signale au host.

## Checklist

### 1. csproj

Ajouter dans `<PropertyGroup>` :

```xml
<!-- Convention wipiSoft : déclare l'app comme Module embarquable. -->
<WipiModuleKind>Module</WipiModuleKind>
<WipiModuleName>NomCourt</WipiModuleName>      <!-- sans espace, ni \ / : * ? " < > | -->
<WipiModuleVersion>1.0</WipiModuleVersion>
<!-- LGO compatibles (facultatif) — séparateur ; pour plusieurs valeurs. -->
<LGO>Winpharma</LGO>
<!-- Recommandé pour la détection wpsServices : -->
<Company>wipiSoft</Company>
```

Ajouter la référence SDK + l'import des targets unifiés :

```xml
<ItemGroup>
  <ProjectReference Include="..\..\..\_SDKs\Modules\Wps.Module.Sdk\Wps.Module.Sdk.csproj" />
</ItemGroup>

<!-- Auto-deploy post-build vers C:\_wipiSoft\Apps\Modules\<WipiModuleName>\
     + injection AssemblyMetadata WipiModuleKind/Name/Version/LGO. -->
<Import Project="..\..\..\_SDKs\Modules\Wps.Module.Sdk\Wps.Module.Sdk.targets" />
```

### 2. Stockage des données — convention wipiSoft

Toute donnée hors-registre du module (cache, fichiers générés, settings JSON,
WebView2 user data, extractions...) doit aller dans :

```
<RépertoireWipiSoft>\Data\<WipiModuleName>\        (par défaut C:\_wipiSoft\Data\<Name>\)
```

Source-link `WpsPaths.cs` dans le csproj :

```xml
<Compile Include="..\..\..\_libs\WpsPaths.cs" Link="Helpers\WpsPaths.cs" />
```

Et utilise-le partout où tu écris :

```csharp
var dataDir   = wipisoft.WpsPaths.GetAppDataDir("MonModule");
var cacheDir  = wipisoft.WpsPaths.GetAppDataSubDir("MonModule", "cache");
var wv2Folder = wipisoft.WpsPaths.GetAppDataSubDir("MonModule", "WebView2");
```

> **Pourquoi pas `%LOCALAPPDATA%` ?** Par-utilisateur, invisible aux outils host
> (wipiManager), impossible à inventorier/sauvegarder en bloc. La convention
> wipiSoft regroupe tout sous une seule racine machine. Cf. en-tête de
> `_libs/WpsPaths.cs`.
>
> Pour les vrais secrets utilisateur, continuer à utiliser HKCU
> (`WpsHKCU` dans `_libs/`).

### 3. `Description.md` embarqué

Crée un `Description.md` à la racine du projet (titre court, méthodes exposées, persistance HKCU,
etc.) puis :

```xml
<ItemGroup>
  <EmbeddedResource Include="Description.md" />
</ItemGroup>
```

Lu côté host par `WpsModuleMetadata` → affiché dans la pageslot du module.

### 4. App.xaml.cs

```csharp
using Wps.Module;

protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);
    var window = new MainWindow();

    // Auto-détecte standalone vs embedded selon args (--wps-session présent ou non).
    // En standalone : no-op total. En embedded : pipes IPC + handshake HELLO/WELCOME.
    WpsModule.Bootstrap(this, window, e.Args);

    window.Show();
}
```

### 5. MainWindow.xaml.cs — signaler READY au bon moment

Le READY indique au host **"je suis prêt à être affiché"** — c'est à ce moment que le host
parke la fenêtre dans son slot. Le moment dépend de chaque module :

```csharp
public MainWindow()
{
    InitializeComponent();

    // WPF basique : Ready dès le Loaded
    Loaded += async (_, _) => await WpsModule.NotifyReadyAsync();

    // WebView2 : Ready après EnsureCoreWebView2Async
    // Loaded += async (_, _) =>
    // {
    //     await Wv2.EnsureCoreWebView2Async();
    //     await WpsModule.NotifyReadyAsync();
    // };

    // CefSharp : Ready dans FrameLoadEnd avec e.Frame.IsMain
    // Cef.FrameLoadEnd += async (_, args) =>
    // {
    //     if (args.Frame.IsMain) await WpsModule.NotifyReadyAsync();
    // };
}
```

⚠️ Sans appel à `NotifyReadyAsync()`, le host timeoutera après 30s et killera le process.
Un warning est loggé dans wipiLOG après 5s sans ready pour aider à diagnostiquer un oubli.

### 6. (Optionnel) Hooks lifecycle via `IWpsModule`

Pour réagir aux événements du host (CLOSE demandé, RESIZE, négociation v1.3, déconnexion),
implémente `IWpsModule`. Tous les hooks ont une implémentation par défaut (DIM C# 8+) — tu
n'override que ce dont tu as besoin :

```csharp
public partial class MainWindow : Window, IWpsModule
{
    public MainWindow()
    {
        InitializeComponent();
        WpsModule.Register(this);   // avant Bootstrap
        Loaded += async (_, _) => await WpsModule.NotifyReadyAsync();
    }

    public void OnHostConnected() { /* handshake terminé */ }
    public void OnShutdownRequested() { Application.Current.Shutdown(); }  // CLOSE reçu, phase finale
    public void OnResizeRequested(double dipW, double dipH, double dpi) { /* facultatif */ }
}
```

### 7. (v1.3, recommandé) Shutdown négocié via `OnCanCloseRequestedAsync`

Depuis le contrat **v1.3**, le host envoie d'abord un **CAN_CLOSE** au module avant le CLOSE
final. Le module peut **négocier** sa fermeture en répondant :

| Décision | Sémantique |
|---|---|
| `CanCloseDecision.Ok` | Libre, je m'engage à fermer (default DIM = Ok = comportement v1.2) |
| `CanCloseDecision.Busy(reason, estimatedMs)` | Occupé, ~Xms restant (le host attend des `BUSY_PROGRESS`) |
| `CanCloseDecision.NeedUser(reason)` | Besoin d'un dialog utilisateur (host bascule l'onglet + focus) |
| `CanCloseDecision.Rejected(reason)` | L'utilisateur a refusé — le host annule sa fermeture |

**Cas le plus simple** : cleanup synchrone rapide avant de répondre Ok.

```csharp
public ValueTask<CanCloseDecision> OnCanCloseRequestedAsync(CanCloseContext ctx)
{
    // Ex MiniBoard : ABM_REMOVE est synchrone et rapide → on l'exécute, puis Ok
    AppBarManager.Unregister();
    return new ValueTask<CanCloseDecision>(CanCloseDecision.Ok);
}
```

**Cas Busy long** (cleanup async > 100ms) :

```csharp
public ValueTask<CanCloseDecision> OnCanCloseRequestedAsync(CanCloseContext ctx)
{
    _ = SaveAndResolveAsync();  // fire-and-forget
    return new ValueTask<CanCloseDecision>(CanCloseDecision.Busy("Sauvegarde...", 3000));
}
private async Task SaveAndResolveAsync()
{
    // Pendant la sauvegarde, on envoie BUSY_PROGRESS toutes les ~3s pour ne pas timeouter
    for (int i = 0; i < 10; i++)
    {
        await Task.Delay(300);
        await WpsModule.ReportBusyProgress(new BusyProgress(i * 10, $"Étape {i}/10..."));
    }
    await _store.SaveAsync();
    await WpsModule.ResolveCanClose(CanCloseDecision.Ok);  // débloque l'attente côté host
}
```

**Cas dialog utilisateur** (genre "Voulez-vous sauvegarder ?") :

```csharp
public ValueTask<CanCloseDecision> OnCanCloseRequestedAsync(CanCloseContext ctx)
{
    if (HasUnsavedChanges)
    {
        _ = ShowSaveDialogAsync();
        return new ValueTask<CanCloseDecision>(CanCloseDecision.NeedUser("Document non sauvegardé"));
    }
    return new ValueTask<CanCloseDecision>(CanCloseDecision.Ok);
}
private async Task ShowSaveDialogAsync()
{
    var result = MessageBox.Show("Sauvegarder avant de fermer ?", ..., MessageBoxButton.YesNoCancel);
    var decision = result switch
    {
        MessageBoxResult.Yes    => /* save then */ CanCloseDecision.Ok,
        MessageBoxResult.No     => CanCloseDecision.Ok,
        MessageBoxResult.Cancel => CanCloseDecision.Rejected("L'utilisateur a annulé"),
        _ => CanCloseDecision.Ok,
    };
    await WpsModule.ResolveCanClose(decision);
}
```

**Mode urgent (shutdown OS)** : si `ctx.IsUrgent = true`, le SDK clamp automatiquement
`NeedUser` et `Rejected` en `Busy(2000ms)` — pas de dialog ni de veto pendant un shutdown
système (Windows tuera le process passé son timeout indépendamment). L'app peut consulter
`ctx.IsUrgent` pour adapter son cleanup (skip dialog, sauvegarde minimale).

### 8. (v1.3, recommandé) Hook `OnHostDisconnected`

Filet quand le host meurt (crash) ou est figé (UI thread bloqué > 30s sans PING). Le SDK
appelle ce hook puis fait `Application.Current.Shutdown()` automatiquement — l'app peut
faire un cleanup synchrone d'urgence dans le hook avant le shutdown auto.

```csharp
public void OnHostDisconnected(HostDisconnectReason reason)
{
    // reason = PipeClosed (host crashed/killed) ou HeartbeatSilent (host frozen > 30s)
    // Cleanup d'urgence (ex: AppBar idempotent)
    AppBarManager.Unregister();
    // Pas besoin d'appeler Application.Shutdown — le SDK le fait après le hook
}
```

## Test

### Standalone

```
cd MonApp\bin\x64\Debug\net8.0-windows
.\MonApp.exe
```

L'app démarre normalement, `WpsModule.Bootstrap` est no-op, comportement inchangé.

### Embedded (via host)

Lance ton host (ex: ModuloSlot.Host). Au scan `WpsModuleDiscovery.Scan()`, il trouve ton
module dans `C:\_wipiSoft\Apps\Modules\<WipiModuleName>\` et l'affiche dans la sidebar.
Cliquer dessus → le host launch ton .exe avec `--wps-session XXX` → handshake → embed.

## Pièges courants

- **Pas de `Description.md` embedded** → l'utilisateur ne voit qu'une description vide dans
  la pageslot. Toujours en mettre un, même court.
- **`<WipiModuleName>` avec un espace** → erreur de build (validation des targets).
- **Oubli de `WpsModule.NotifyReadyAsync()`** → timeout 30s + kill par le host.
- **`MainWindow` qui appelle `Application.Shutdown()` à la fermeture** → le module se
  ferme dès que l'user ferme sa fenêtre, alors que le host la garde "vivante" en parking
  HWND. Ne shutdown qu'à `OnShutdownRequested` (CLOSE du host).
- **(v1.3) Cleanup applicatif dans `OnShutdownRequested` au lieu de `OnCanCloseRequestedAsync`**
  → si le cleanup est rapide (< 50ms), c'est OK. Mais s'il est long (> 100ms : flush SMB,
  ABM_REMOVE pas instantané sur certaines configs), le faire **proactivement** dans
  `OnCanCloseRequestedAsync` AVANT de répondre Ok. C'est ce qui résout le bug historique
  MiniBoard (ABM_REMOVE dans Window_Closing arrivant trop tard côté Explorer après le Kill
  du host). Pattern : exécuter le cleanup, retourner Ok, recevoir CLOSE, faire juste
  `Application.Shutdown()`.
- **(v1.3) Retour `Busy(estimatedMs)` sans envoyer de `BUSY_PROGRESS`** → le host considère
  le module figé après ~8s de silence et passe au Kill. Si tu retournes Busy, envoie un
  `WpsModule.ReportBusyProgress(...)` toutes les ~3s pour rester dans la fenêtre du
  watchdog (= 2× la période recommandée 3s + 2s de marge swap = 8s).
- **(v1.3) Oubli de `WpsModule.ResolveCanClose(...)` après un Busy/NeedUser** → le host
  reste en attente jusqu'au timeout, puis Kill. Toujours appeler `ResolveCanClose(Ok)` ou
  `ResolveCanClose(Rejected(...))` quand le travail Busy est fini ou le dialog tranché.
