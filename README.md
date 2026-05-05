# _SDKs

SDKs csproj wipiSoft, organisés par domaine fonctionnel. Chaque sous-dossier regroupe une
famille cohérente de SDKs qui se référencent entre eux et exposent un mini-framework à
destination des apps wipiSoft.

Repo GitHub : <https://github.com/Gatoche/_SDKs>

## Différence avec `_libs/`

| `_libs/` (sibling) | `_SDKs/` |
|---|---|
| Helpers `.cs` standalone, source-linkés via `<Compile Include>` | SDKs structurés, référencés via `<ProjectReference>` |
| Pas de dépendance NuGet "encapsulée" — l'assembly du consommateur hérite des deps | Dépendances NuGet propres, types publics dédiés, targets MSBuild propres |
| Ex: `WpsDebugSender.cs`, `WpsHKCU.cs`, `WpsFileHelper.cs`, `wpsServices.cs` | Ex: `Wps.Module.Sdk`, `Wps.Host.Sdk`, futur `Wps.Licensing.*` |

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

## Licence

Projet propriétaire — wipiSoft
