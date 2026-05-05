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

### 2. `Description.md` embarqué

Crée un `Description.md` à la racine du projet (titre court, méthodes exposées, persistance HKCU,
etc.) puis :

```xml
<ItemGroup>
  <EmbeddedResource Include="Description.md" />
</ItemGroup>
```

Lu côté host par `WpsModuleMetadata` → affiché dans la pageslot du module.

### 3. App.xaml.cs

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

### 4. MainWindow.xaml.cs — signaler READY au bon moment

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

### 5. (Optionnel) Hooks lifecycle via `IWpsModule`

Pour réagir aux événements du host (CLOSE demandé, RESIZE), implémente `IWpsModule` :

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
    public void OnShutdownRequested() { Application.Current.Shutdown(); }  // CLOSE reçu
    public void OnResizeRequested(double dipW, double dipH, double dpi) { /* facultatif */ }
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
