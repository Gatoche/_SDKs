using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wps.Module;

/// <summary>
/// (v1.3 final) Payload JSON transporté dans le wire <c>CAN_CLOSE_NEED_USER|jsonPayload</c>.
/// Sérialisé par le SDK module/service (côté Send) et désérialisé par le SDK host (côté
/// Parse) — défini dans <c>Wps.Module.Contracts</c> pour rester cohérent des deux côtés.
///
/// <para>Forme JSON émise :</para>
/// <code>
/// {
///   "reason": "Document non sauvegardé",
///   "ask": "Voulez-vous fermer ?",
///   "answers": { "yes": "Oui", "yes-after": "Oui après traitement", "no": "Non" },
///   "allowClose": false
/// }
/// </code>
///
/// <para><b>Ordre d'insertion du dictionnaire</b> : Dictionary&lt;string,string&gt; en .NET 6+
/// préserve l'ordre d'insertion et System.Text.Json le respecte à la sérialisation. Côté
/// désérialisation, on récupère un Dictionary qui préserve aussi l'ordre du JSON. Donc
/// l'ordre des boutons déclaré par l'app est l'ordre d'affichage final côté host.</para>
/// </summary>
public sealed class NeedUserPayload
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("ask")]
    public string Ask { get; set; } = "";

    [JsonPropertyName("answers")]
    public Dictionary<string, string> Answers { get; set; } = new();

    [JsonPropertyName("allowClose")]
    public bool AllowClose { get; set; }

    // Options communes : pas de pretty-print (on veut une ligne unique pour le pipe
    // line-delimited), camelCase déjà géré via les attributs explicites ci-dessus.
    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,  // garde les caractères non-ASCII lisibles (é, à, etc.)
    };

    /// <summary>Sérialise en JSON single-line pour transit sur le pipe wire-protocol.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, _opts);

    /// <summary>Désérialise le payload JSON. Retourne <c>null</c> si le JSON est invalide
    /// ou vide — le caller doit alors logger et soit ignorer la trame, soit utiliser des
    /// valeurs par défaut.</summary>
    public static NeedUserPayload? Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<NeedUserPayload>(json, _opts); }
        catch { return null; }
    }
}
