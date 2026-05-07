namespace Wps.Module.Core;

/// <summary>
/// Levée par <see cref="WpsModuleServiceClient.LaunchAsync"/> (et autres lanceurs côté host)
/// quand le déploiement du module à lancer ne passe pas la vérification d'intégrité
/// <see cref="WpsDeployVerifier.Verify"/> : manifest absent, master guid mismatch,
/// fichier manquant ou fichier corrompu.
///
/// <para>Le caller (typiquement la pageslot UI du module/service) doit catcher cette
/// exception et afficher <see cref="Result"/>.<see cref="WpsDeployVerifier.VerifyResult.DisplayMessage"/>
/// dans la zone d'erreur, plutôt que de laisser un dialogue runtime cryptique apparaître
/// suite à un déploiement incomplet (push interrompu, antivirus, etc.).</para>
/// </summary>
public sealed class WpsDeployInvalidException : Exception
{
    public WpsDeployVerifier.VerifyResult Result { get; }

    public WpsDeployInvalidException(WpsDeployVerifier.VerifyResult result)
        : base(result.DisplayMessage)
    {
        Result = result;
    }
}
