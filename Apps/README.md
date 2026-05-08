# Wps SDKs — Apps

SDKs pour les **apps wipiSoft standalone** (OuiPDF, MailTools, etc.) — toutes
les apps qui ne sont **pas** des Modules ou ModuleServices lancés par un host
comme `ModuloSlot`.

## Contenu

| Projet | Rôle |
|---|---|
| `Wps.AppLauncher/` | Launcher natif (NativeAOT) compilé une seule fois et copié par le post-build de chaque app standalone. Vérifie l'intégrité du déploiement avant de déléguer au vrai AppHost. |
| `Wps.StandaloneApp.Sdk/` | Targets MSBuild qui transforment une app classique en app standalone "wipiSoft" (manifest, launcher devant l'AppHost, icône, single-instance canonique). |

## Lien avec les Modules

Le code de vérification (`WpsDeployVerifier`) et le générateur de manifest
(`WpsGenerateManifest.ps1`) **vivent dans `_libs/`** : ils sont partagés
entre `Wps.Module.Sdk.Core` (côté Module/ModuleService) et `Wps.StandaloneApp.Sdk`
(côté app standalone).

## HOWTO

Voir [Wps.StandaloneApp.Sdk/HOWTO.md](Wps.StandaloneApp.Sdk/HOWTO.md) pour
l'intégration dans une app.
