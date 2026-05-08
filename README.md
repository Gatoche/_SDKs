# _SDKs

SDKs csproj wipiSoft, organisés par domaine fonctionnel. Chaque sous-dossier regroupe une
famille cohérente de SDKs qui se référencent entre eux et exposent un mini-framework à
destination des apps wipiSoft.

Repo GitHub : <https://github.com/Gatoche/_SDKs>

## Différence avec `_libs/`

| `_libs/` (sibling) | `_SDKs/` |
|---|---|
| Helpers `.cs` standalone, source-linkés via `<Compile Include>` | SDKs structurés, référencés via `<ProjectReference>` ou via `<Import>` de `.targets` |
| Pas de dépendance NuGet "encapsulée" — l'assembly du consommateur hérite des deps | Dépendances NuGet propres, types publics dédiés, targets MSBuild propres |
| Ex: `WpsDebugSender.cs`, `WpsHKCU.cs`, `WpsFileHelper.cs`, `wpsServices.cs`, `WpsDeployVerifier.cs`, `WpsGenerateManifest.ps1` | Ex: `Wps.Module.Sdk`, `Wps.Host.Sdk`, `Wps.StandaloneApp.Sdk`, `Wps.AppLauncher` |

Choix du mode : si le composant est un fichier unique sans état partagé → `_libs/`. S'il a
ses propres dépendances NuGet, ses types publics, ses targets de validation/déploiement, ou
plusieurs fichiers cohérents → `_SDKs/<Domaine>/`.

## Domaines

### [`Modules/`](Modules/) — Framework Module ↔ Host wipiSoft

Convention de communication entre une **app wipiSoft** (Module avec UI, ou ModuleService
headless invocable) et un **host** orchestrateur (ModuloSlot, futur wipiTools).

Architecture en 5 csproj :

```
Wps.Module.Contracts ─── wire-protocol (HELLO, WELCOME, READY, INVOKE, ...) — net8.0 pur
Wps.Module.Sdk.Core ──── pipes IPC duplex + helpers semver — net8.0 pur (réutilisé partout)
        │
        ├─→ Wps.Module.Sdk ──── façade côté Module (UI WPF embedded)
        ├─→ Wps.ModuleService.Sdk ─── façade côté ModuleService (headless invocable)
        └─→ Wps.Host.Sdk ──── façade côté Host (orchestrateur, lance + invoque)
```

Pour transformer une app existante en Module ou ModuleService :

- App WPF avec UI principale → **[HOWTO Module](Modules/Wps.Module.Sdk/HOWTO.md)**
- App headless invocable → **[HOWTO ModuleService](Modules/Wps.ModuleService.Sdk/HOWTO.md)**

Ou plus simplement : déclencher le skill `wipisoft-module-converter` (cf.
[`_skills/`](../_skills/)) qui automatise toute la procédure.

### [`Apps/`](Apps/) — Apps wipiSoft standalone

Pour les apps **lancées directement par l'utilisateur** (raccourci, double-clic, file
association, drag-drop) — par opposition aux Modules/ModuleServices pilotés par un host.
Ex: OuiPDF, MailTools.

Architecture en 2 composants :

```
Wps.AppLauncher ────── launcher natif NativeAOT (~3 Mo, win-x64)
                       compilé une seule fois, copié et renommé en <App>.exe
                       par le post-build de chaque app standalone
        │
        └─→ Wps.StandaloneApp.Sdk ─── target MSBuild qui orchestre :
              - copie de l'AppHost depuis obj/apphost.exe → <App>.app.exe
              - copie du launcher → <App>.exe
              - injection de l'icône via rcedit
              - génération du manifest d'intégrité (WpsGenerateManifest.ps1)
```

Pour intégrer le SDK dans une app standalone : **[HOWTO Standalone](Apps/Wps.StandaloneApp.Sdk/HOWTO.md)**.

Le launcher vérifie le manifest au démarrage (via `WpsDeployVerifier`, partagé avec les
SDKs Modules) et fail-fast avec une `MessageBox` propre si le déploiement est corrompu —
plus de dialogue runtime cryptique *"Install .NET Desktop Runtime"*. Il fait également
office de **single-instance smart** : si l'app tourne déjà (mutex
`wipiSoft.<App>.SingleInstance`), il transmet ses arguments via le pipe canonique
`wipiSoft.<App>.Pipe` (1 ligne par fichier) et exit immédiatement, sans spawner un second
AppHost qui mourrait aussitôt.

### `Licensing/` — (futur)

Framework de licensing wipiSoft. À venir.

## Conventions csproj communes (apps qui consomment les SDKs Modules)

```xml
<PropertyGroup>
  <WipiModuleKind>Module</WipiModuleKind>      <!-- ou ModuleService -->
  <WipiModuleName>NomCourt</WipiModuleName>    <!-- sans espace, ni \ / : * ? " < > | -->
  <WipiModuleVersion>1.0</WipiModuleVersion>
  <LGO>Winpharma</LGO>                         <!-- facultatif, séparateur ; -->
  <Company>wipiSoft</Company>                  <!-- recommandé : détection wpsServices -->
</PropertyGroup>
```

Les targets unifiés (`Wps.Module.Sdk.Core.targets`, importés transitivement) :

- **Valident au build** que `<WipiModuleName>` ne contient ni espace ni caractère Windows
  interdit, et que `<WipiModuleKind>` ∈ `{ Module, ModuleService }`
- **Injectent dans le PE** via `AssemblyMetadata` : `WipiModuleName`, `WipiModuleVersion`,
  `WipiModuleKind`, `WipiLGO` (lus côté host par `WpsModuleMetadata`)
- **Auto-deploy post-build** vers `C:\_wipiSoft\Apps\Modules\<WipiModuleName>\`
  (override possible via `<WipiModulesDeployDir>`)

Rétrocompat : l'ancienne convention `<WipiModule>true</WipiModule>` (avant `<WipiModuleKind>`)
reste acceptée et est mappée automatiquement sur `Module`.

## Conventions csproj communes (apps qui consomment Wps.StandaloneApp.Sdk)

```xml
<PropertyGroup>
  <WipiAppKind>Standalone</WipiAppKind>
  <WipiAppName>OuiPDF</WipiAppName>           <!-- sans espace, ni \ / : * ? " < > | -->
  <WipiAppVersion>0.6.1</WipiAppVersion>
  <Company>wipiSoft</Company>                 <!-- recommandé : détection wpsServices -->
</PropertyGroup>

<Import Project="..\..\..\_SDKs\Apps\Wps.StandaloneApp.Sdk\Wps.StandaloneApp.Sdk.targets" />
```

L'app doit aligner son single-instance sur les noms canoniques attendus par le launcher :

| Élément | Nom |
|---|---|
| Mutex single-instance | `wipiSoft.<AppName>.SingleInstance` |
| Named pipe IPC        | `wipiSoft.<AppName>.Pipe`           |
| Protocole pipe        | 1 ligne par argument, lecture jusqu'à EOF du stream |

Override possible : `<WipiSkipLauncher>true</WipiSkipLauncher>` désactive la transformation
post-build (utile pour debugger directement le `<App>.exe` natif sans la couche launcher).

## Licence

Projet propriétaire — wipiSoft
