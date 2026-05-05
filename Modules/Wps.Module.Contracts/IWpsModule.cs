namespace Wps.Module;

/// <summary>
/// Hooks lifecycle optionnels pour un module wipiSoft. Implémenter cette interface (typiquement
/// sur la <c>MainWindow</c>) puis l'enregistrer via <c>WpsModule.Register(this)</c> permet de
/// recevoir les notifications du host pertinentes pour le code applicatif (close demandé,
/// redimensionnement, etc.).
///
/// Tous les hooks ont une implémentation par défaut vide (DIM) → un module n'implémente que
/// ce dont il a besoin. Si un module n'enregistre rien, le SDK gère quand même toute la
/// mécanique IPC (heartbeat, ready) en silence.
/// </summary>
public interface IWpsModule
{
    /// <summary>Le host est connecté et a accepté la version du contrat (WELCOME reçu).
    /// Les pipes IPC sont ouverts. À ce stade le READY n'est PAS encore envoyé : si tu fais
    /// <c>WpsModule.Bootstrap(autoReady: false)</c>, c'est ici typiquement que tu peux
    /// déclencher ton init asynchrone (browser, etc.) avant d'appeler
    /// <c>WpsModule.NotifyReadyAsync()</c> quand tout est prêt.</summary>
    void OnHostConnected() { }

    /// <summary>Le host demande au module de s'arrêter proprement. Réaction typique :
    /// <c>Window.Close()</c> ou un flush de l'état métier avant fermeture.</summary>
    void OnShutdownRequested() { }

    /// <summary>Le host signale les dimensions courantes du slot d'affichage (en DIPs).
    /// Permet au module de pré-rendre à la bonne taille avant d'être visible.
    /// Émis y compris quand le module est parké (-32000) → utile pour les compositors lourds
    /// (CefSharp, WebView2) qui doivent layouter à la bonne taille hors écran.</summary>
    void OnResizeRequested(double dipW, double dipH, double dpi) { }
}
