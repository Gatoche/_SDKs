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

### 2. Single-instance canonique côté app

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

### 3. Protocole pipe

Le launcher envoie **un argument par ligne** sur le pipe, puis ferme la connexion
(EOF). Le serveur lit en boucle :

```csharp
using var reader = new StreamReader(pipeServer);
string? line;
while ((line = await reader.ReadLineAsync()) is not null)
{
    if (!string.IsNullOrEmpty(line)) OpenFile(line);
}
```

Pas de format JSON, pas de message-mode. Une simple ligne = un fichier, lecture
jusqu'à EOF.

### 4. Build

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

### 5. Test

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
| `<WpsLauncherSrc>...</WpsLauncherSrc>` | Path custom vers `Wps.AppLauncher.exe` (par défaut : `_SDKs/Apps/Wps.AppLauncher/published/win-x64/Wps.AppLauncher.exe`). |
| `<WpsRceditPath>...</WpsRceditPath>` | Path custom vers `rcedit-x64.exe` (par défaut : `_tools/rcedit-x64.exe`). Si absent, l'injection d'icône est silencieusement skippée. |

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
