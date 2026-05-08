# HOWTO — Transformer une app .NET en app standalone wipiSoft

Une **app standalone wipiSoft** est une app classique (WPF, WinForms, console) lancée
directement par l'utilisateur (raccourci, double-clic, association de fichier), à
l'opposé des Modules/ModuleServices pilotés par un host. Exemples : OuiPDF, MailTools.

Ce SDK ajoute à l'app :

1. Un **launcher natif (NativeAOT)** `<AppName>.exe` devant le vrai AppHost
   `<AppName>.app.exe`.
2. Une **vérification d'intégrité du déploiement** au démarrage (manifest +
   master guid SHA-256), avec MessageBox propre en cas de fichier manquant ou
   corrompu — plus jamais le dialogue runtime cryptique *"Install .NET Desktop
   Runtime"*.
3. Une **logique single-instance** standardisée (mutex + named pipe canoniques)
   qui permet au launcher de transmettre les arguments CLI à l'instance déjà en
   cours sans spawner inutilement un second AppHost.

## Pré-requis

- App .NET 8 (`net8.0` ou `net8.0-windows`)
- Le launcher AOT publié dans le SDK :
  ```
  cd _SDKs\Apps\Wps.AppLauncher
  dotnet publish -c Release -r win-x64 -o published\win-x64
  ```
  (à refaire uniquement quand on change le code du launcher)

## ⚠️ Règle d'or : aucun fichier ne doit être écrit dans le DeployDir au runtime

Le manifest d'intégrité contient le **SHA-256 figé au build** de chaque fichier livré
avec l'app. Si un fichier change après le build, le launcher dira *"Fichier corrompu"*
au prochain démarrage et **refusera de lancer l'app** — alors que l'objectif du SDK
est précisément l'inverse : prévenir l'instabilité, pas en créer.

**Avant d'intégrer le SDK à une app, vérifier que l'app ne crée AUCUN fichier dans
son dossier d'installation au runtime.** Tout ce qui change à l'usage doit aller :

| Ressource | Bon emplacement |
|---|---|
| Cache, données utilisateur | `%LOCALAPPDATA%\<AppName>\` |
| Préférences, settings utilisateur | `%LOCALAPPDATA%\<AppName>\` ou `HKCU\Software\wipiSoft\<AppName>\` |
| Logs | `%LOCALAPPDATA%\<AppName>\logs\` (ou wipiLOG via `WpsDebugSender`) |
| Cache WebView2 | **`CoreWebView2EnvironmentOptions.UserDataFolder`** redirigé vers `%LOCALAPPDATA%\<AppName>\WebView2\` |

Pièges classiques à chasser **avant** la première intégration :

1. **WebView2 par défaut** : crée `<App>.app.exe.WebView2/EBWebView/...` à côté de
   l'exe (Crashpad, settings.dat, component_crx_cache, ~290 fichiers volatiles).
   Configurer explicitement `UserDataFolder` à la création de l'environnement :
   ```csharp
   var userDataFolder = Path.Combine(
       Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
       "<AppName>", "WebView2");
   if (!Directory.Exists(userDataFolder)) Directory.CreateDirectory(userDataFolder);
   var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
   await webView2.EnsureCoreWebView2Async(env);
   ```
   Mutualiser l'env via un `Lazy<Task<CoreWebView2Environment>>` static si plusieurs
   instances WebView2 coexistent (sinon WebView2 lance *"different environments"*).
2. **PDF.js / autre archive zip extraite** : par défaut on extrait à côté de l'exe.
   Rediriger vers `%LOCALAPPDATA%\<AppName>\` (l'archive `.zip` reste dans le
   DeployDir, protégée — l'extraction live ailleurs).
3. **WPF window state persisté en local** : du genre `<App>WindowStateSettings.json`
   à côté de l'exe — déplacer vers `%LOCALAPPDATA%`.
4. **`File.WriteAllText` dans `AppDomain.CurrentDomain.BaseDirectory`** : détecter
   les écritures relatives à l'exe (logs, dumps, temp files...).

### Exclusions par défaut dans le PS1

Le générateur de manifest (`WpsGenerateManifest.ps1`) maintient une liste **minimale**
de patterns runtime à ignorer :

```powershell
$pathExcludes = @(
    "*WindowStateSettings.json"  # convention WPF locale
)
```

`*.WebView2/*` n'y est **pas** : si une app oublie de rediriger `UserDataFolder`,
le manifest la coincera au premier démarrage suivant — c'est le signal voulu
(le bug est forcé tôt, pas en prod chez un client).

⚠️ **Règle stricte** : on n'allonge cette liste qu'en dernier recours. Chaque pattern
ajouté = surface non vérifiée par le launcher = risque qu'un fichier corrompu passe
inaperçu. **Préférer toujours rediriger l'écriture hors du DeployDir** côté code app.

## Checklist d'intégration

### 1. csproj de l'app

Ajouter dans `<PropertyGroup>` :

```xml
<WipiAppKind>Standalone</WipiAppKind>
<WipiAppName>OuiPDF</WipiAppName>          <!-- nom court, sans espace ni / \ : * ? " < > | -->
<WipiAppVersion>0.6.1</WipiAppVersion>
```

Importer les targets à la fin (avant `</Project>`) :

```xml
<Import Project="..\..\..\_SDKs\Apps\Wps.StandaloneApp.Sdk\Wps.StandaloneApp.Sdk.targets" />
```

Le chemin relatif suppose une app dans `dev/<repo>/<sub>/<App>/`. Ajuste si l'arborescence
diffère.

### 2. AUMID canonique (`WpsAppUserModelId`)

Sans config, Windows considère `<App>.exe` (launcher) et `<App>.app.exe` (AppHost)
comme deux apps distinctes → **deux icônes taskbar** (l'icône épinglée + l'icône de
la fenêtre AppHost). Pour les regrouper, le launcher et l'AppHost doivent partager
le **même AppUserModelID** (`wipiSoft.<AppName>`).

> ⚠️ **Le shortcut taskbar doit pointer sur `<App>.exe` (le launcher), PAS sur
> `<App>.app.exe`.** L'épinglage manuel "à partir d'une icône de fenêtre déjà
> ouverte" épingle le path du process actif (= l'AppHost) — c'est un piège. Pour
> épingler correctement : faire clic-droit sur `<App>.exe` dans l'explorateur →
> "Épingler à la barre des tâches". Si on épingle `<App>.app.exe`, le shortcut
> contourne le launcher (= pas de vérification manifest, et `WpsAppGuard` fait
> exit silencieusement → l'app ne se lance pas du tout depuis ce shortcut).

- Le **launcher** (`Wps.AppLauncher`) appelle `WpsAppUserModelId.SetCurrentProcess(appName)`
  au début de `Main`. Rien à faire côté SDK.
- L'**AppHost** doit appeler la même méthode au tout début d'`OnStartup`,
  **avant toute création de fenêtre WPF** (sinon Windows a déjà attribué l'AUMID
  par défaut à la fenêtre, et le set ultérieur n'a plus d'effet sur elle) :

  ```csharp
  protected override void OnStartup(StartupEventArgs e)
  {
      WpsAppUserModelId.SetCurrentProcess("<AppName>");
      // ... suite normale du démarrage
  }
  ```

  Source-link dans le csproj :

  ```xml
  <Compile Include="..\..\..\_libs\WpsAppUserModelId.cs" Link="Helpers\WpsAppUserModelId.cs" />
  ```

### 3. Garde-fou anti-lancement direct (`WpsAppGuard`)

L'AppHost `<App>.app.exe` ne doit **pas** pouvoir être exécuté directement (double-clic, CLI
explicite). Seul `<App>.exe` (le launcher) doit pouvoir le démarrer, car lui seul vérifie
l'intégrité du déploiement avant le start.

Mécanisme :

- Le **launcher** définit la variable d'environnement `WPS_LAUNCHER_TOKEN` à un token
  canonique avant `Process.Start` de l'AppHost. La variable est héritée par le child.
- L'**AppHost** appelle `WpsAppGuard.EnsureLaunchedByLauncherOrExit()` au tout début
  d'`OnStartup` (avant toute création de fenêtre). Si le token est absent ou invalide,
  l'AppHost fait `Environment.Exit(1)` **silencieusement** (pas de MessageBox, pas de
  fenêtre — l'utilisateur qui a double-cliqué par erreur ne voit rien apparaître).
- **Bypass debug** : `Debugger.IsAttached` court-circuite le check. Permet le `F5` direct
  sur l'AppHost depuis Visual Studio.

Source-link `WpsAppGuard.cs` dans le csproj de l'app :

```xml
<Compile Include="..\..\..\_libs\WpsAppGuard.cs" Link="Helpers\WpsAppGuard.cs" />
```

Et appel au tout début d'`App.OnStartup` (WPF) ou `Main` (console / WinForms) :

```csharp
protected override void OnStartup(StartupEventArgs e)
{
    WpsAppGuard.EnsureLaunchedByLauncherOrExit();
    // ... suite normale du démarrage
}
```

> **Sécurité** : ce n'est pas un mécanisme contre un attaquant déterminé (un user qui sait
> peut définir la variable manuellement). C'est une protection contre les **doubles-clics
> par erreur** sur `.app.exe` qui contournent la vérification d'intégrité du manifest.

### 4. Single-instance canonique côté app

Le launcher attend les noms IPC suivants (alignés sur `WipiAppName`) :

| Élément | Nom |
|---|---|
| Mutex single-instance | `wipiSoft.<AppName>.SingleInstance` |
| Named pipe IPC        | `wipiSoft.<AppName>.Pipe`           |

Côté `App.xaml.cs` (WPF) ou équivalent :

```csharp
const string MutexName = "wipiSoft.OuiPDF.SingleInstance";
const string PipeName  = "wipiSoft.OuiPDF.Pipe";

_mutex = new Mutex(true, MutexName, out var isFirst);
if (!isFirst)
{
    SendArgsToRunningInstance(e.Args); // legacy : ouvrait dans l'instance existante
    Shutdown();
    return;
}
StartPipeServer(); // accept connections from launcher when files are dropped on <App>.exe
```

### 5. Protocole pipe

Le launcher envoie **un argument par ligne** sur le pipe, puis ferme la connexion
(EOF). Le serveur lit en boucle :

```csharp
using var reader = new StreamReader(pipeServer);
var paths = new List<string>();
string? line;
while ((line = await reader.ReadLineAsync()) is not null)
{
    if (!string.IsNullOrEmpty(line)) paths.Add(line);
}

Application.Current.Dispatcher.Invoke(() =>
{
    BringToForeground();          // TOUJOURS, qu'il y ait des paths ou non
    foreach (var p in paths) OpenFile(p);
});
```

Pas de format JSON, pas de message-mode. Une simple ligne = un fichier, lecture
jusqu'à EOF.

**Cas du double-clic à vide** : si l'utilisateur double-clique sur `<App>.exe`
alors que l'app tourne déjà, le launcher se connecte au pipe sans rien écrire
(args vide → 0 ligne) puis ferme. Côté serveur, c'est le signal *"reveille-toi"*
→ l'instance fait `BringToForeground` même sans paths à ouvrir. **Ne pas
court-circuiter** ce cas en testant `args.Length == 0` avant la connexion ni
en bypassant `BringToForeground` quand `paths.Count == 0`.

### 6. Build

```bash
dotnet build <App>.csproj -c Release -p:Platform=x64
```

Le post-build :

1. Renomme `<App>.exe` → `<App>.app.exe`
2. Copie `Wps.AppLauncher.exe` → `<App>.exe`
3. Injecte l'icône `<ApplicationIcon>` dans le launcher (rcedit-x64.exe)
4. Génère `<App>.deploy.manifest.json` + `<App>.deploy.guid`

Tu obtiens dans `bin\x64\Release\net8.0-windows\` :

```
OuiPDF.exe                       ← launcher AOT (~5 Mo) avec icône OuiPDF
OuiPDF.app.exe                   ← AppHost .NET (le vrai)
OuiPDF.dll
OuiPDF.runtimeconfig.json
OuiPDF.deps.json
OuiPDF.deploy.manifest.json      ← intégrité (liste fichiers + SHA-256)
OuiPDF.deploy.guid               ← scellé du manifest (32 hex chars)
... + autres fichiers
```

### 7. Test

Pour valider la chaîne :

- **Démarrage à blanc** : double-clic `OuiPDF.exe` → ouverture normale.
- **Avec argument** : `OuiPDF.exe rapport.pdf` → ouverture du PDF.
- **Détection corruption** : supprimer un .dll critique → relancer → MessageBox
  *"Déploiement invalide : Fichier manquant : <name>.dll"*.
- **Single-instance** : double-clic sur un PDF alors qu'OuiPDF est déjà ouvert →
  le PDF s'ouvre dans l'instance existante (le launcher passe par le pipe sans
  spawner un second AppHost).

## Override

| Propriété | Effet |
|---|---|
| `<WipiSkipLauncher>true</WipiSkipLauncher>` | Désactive complètement la transformation post-build (utile pour debugger directement le `<App>.exe` natif sans la couche launcher). |
| `<WipiSkipForceClean>true</WipiSkipForceClean>` | Désactive le clean automatique de `$(TargetDir)` en Release (cf. ci-dessous). À utiliser quand on veut un rebuild incrémental Release rapide pour un test ponctuel. |
| `<WpsLauncherSrc>...</WpsLauncherSrc>` | Path custom vers `Wps.AppLauncher.exe` (par défaut : `_SDKs/Apps/Wps.AppLauncher/published/win-x64/Wps.AppLauncher.exe`). |
| `<WpsRceditPath>...</WpsRceditPath>` | Path custom vers `rcedit-x64.exe` (par défaut : `_tools/rcedit-x64.exe`). Si absent, l'injection d'icône est silencieusement skippée. |

## Build Release : clean automatique du `TargetDir`

En **Release** uniquement, la cible `WpsForceCleanInRelease` supprime
`$(TargetDir)` avant chaque build (`BeforeTargets=BeforeBuild`). Garantit que
le manifest d'intégrité ne contient que les fichiers réellement produits par
le build courant — pas les artefacts résiduels d'un build précédent (raccourci
`.lnk` créé manuellement, `.bak` d'un éditeur, fichier de test laissé là, etc.)
qui causeraient des "fichier manquant" / "fichier corrompu" au prochain
démarrage si l'utilisateur les déplace.

**En Debug**, le clean automatique est désactivé : on garde l'incrémentalité
MSBuild rapide pour le cycle de dev. Les `$nameExcludes` / `$pathExcludes` du
PS1 absorbent les artefacts courants. Si tu vois apparaître un faux positif
"Fichier manquant" en Debug, fais un `dotnet clean` ponctuel.

## Pièges courants

- **Launcher AOT pas publié** : la target échoue avec un message clair pointant la
  commande `dotnet publish` à lancer.
- **Single-instance pas alignée** : si l'app utilise encore un mutex/pipe legacy
  (`OuiPDFAppMutex` / `OuiPDFPipe`), le launcher ne sait pas quoi faire avec une
  instance running et tombera sur le spawn d'une seconde instance qui se shutdown.
  Aligner sur le naming canonique.
- **Icône non injectée** : vérifier que `<ApplicationIcon>` pointe bien sur un .ico
  existant à la racine du projet, et que `_tools/rcedit-x64.exe` est présent. La
  target log un warning silencieux sinon.
