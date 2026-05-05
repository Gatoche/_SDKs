# Modules — Framework Module ↔ Host wipiSoft

Mini-framework de communication entre les apps wipiSoft (Module avec UI ou ModuleService
headless invocable) et un host orchestrateur (ModuloSlot, futur wipiTools).

## Architecture en 5 csproj

```
Wps.Module.Contracts ─── wire-protocol (HELLO, WELCOME, READY, INVOKE, ...) — net8.0 pur
Wps.Module.Sdk.Core ──── pipes IPC duplex + helpers semver — net8.0 pur (réutilisé partout)
        │
        ├─→ Wps.Module.Sdk ──── façade côté Module (UI WPF embedded)
        ├─→ Wps.ModuleService.Sdk ─── façade côté ModuleService (headless invocable)
        └─→ Wps.Host.Sdk ──── façade côté Host (orchestrateur, lance + invoque)
```

| Csproj | Rôle | Référencé par |
|---|---|---|
| `Wps.Module.Contracts` | Constantes wire-protocol (HELLO/WELCOME/READY/PING/PONG/INVOKE/SHOW_SETTINGS), version contrat (`CurrentVersion = "1.2"`), enum `WpsModuleKind { Module, ModuleService }`, conventions pipes nommés. Cible `net8.0`, aucune dépendance UI. | Tous les SDK |
| `Wps.Module.Sdk.Core` | Bas-niveau partagé : `WpsPipeDuplex` (pipes inbound + outbound, ReadLoop, SendAsync thread-safe, Closed event), `WpsContractVersion` (validation semver lax). Cible `net8.0`, neutre. | Tous les SDK |
| `Wps.Module.Sdk` | Façade côté Module (UI WPF embedded) : `WpsModule.Bootstrap()` + `WpsModule.NotifyReadyAsync()` + parking HWND + heartbeat reply via Dispatcher. | Apps Module WPF |
| `Wps.ModuleService.Sdk` | Façade côté ModuleService (headless) : `WpsModuleService.BootstrapAsync()` + `RegisterInvokeHandler<TParams,TResult>()` + `RegisterSettingsWindow()` + `RunAsync()` boucle d'attente. Marshall auto UI thread + `BringToForegroundAndClick`. | Apps ModuleService |
| `Wps.Host.Sdk` | Façade côté host : `WpsModuleSlot` (Module embedded), `WpsModuleServiceClient` (ModuleService invocable), `WpsModuleDiscovery.Scan()`, `WpsModuleUsageTracker`, `WpsServiceDaemonConfig` (HKCU). UI partagée : `Ui/ServiceControlPage`, `Ui/WpsTitleBar`, `Resources/SharedStyles.xaml` (switch toggle, scrollbar Chrome, title bar buttons). | Hosts orchestrateurs |

## HOWTO

Pour transformer une app existante :

- **App WPF avec UI principale** (embedded dans un slot du host) → suis le
  **[HOWTO Module](Wps.Module.Sdk/HOWTO.md)**
- **App headless invocable** (sans UI principale, expose des méthodes) → suis le
  **[HOWTO ModuleService](Wps.ModuleService.Sdk/HOWTO.md)**

Les deux HOWTO sont autonomes : checklist + blocs csproj prêts à copier + code App.xaml.cs
de référence + tests + pièges courants.

Ou plus simple : utilise le skill Claude Code `wipisoft-module-converter` (cf. [`_skills/`](../../_skills/))
qui pilote automatiquement toute la procédure.

## Dépendances source-linked vers `_libs/`

Deux SDKs ré-exposent un helper de `_libs/` :

- `Wps.Module.Sdk.Core` source-linke `..\..\..\_libs\WpsDebugSender.cs` → exposé
  transitivement à tous ceux qui référencent un SDK Module/ModuleService/Host.
- `Wps.Host.Sdk` source-linke `..\..\..\_libs\WpsModuleMetadata.cs` → utilisé par
  `WpsModuleDiscovery` côté host.

Pourquoi pas dans le SDK directement ? Ces deux fichiers servent aussi en dehors du framework
Module (toutes les apps wipiSoft loggent via WpsDebugSender ; `wipiManager.DevAll` lit les
metadata d'apps qui ne sont ni Module ni Host). Ils restent donc en source-linked dans `_libs/`
et le SDK les ré-expose pour la commodité des consommateurs Module-aware.
