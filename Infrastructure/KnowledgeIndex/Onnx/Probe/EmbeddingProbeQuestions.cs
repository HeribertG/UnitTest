// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Onnx.Probe;

/// <summary>
/// The 25 chat questions the query embedder sees per probe round: 5 to 30 words, in the four UI
/// languages plus English, covering the intents that actually reach the assistant (staff, schedule,
/// absences, expenses, groups, reports). Lengths differ from question to question, so a round presents
/// the session a spread of sequence shapes the way real traffic does - memory patterns are cached per
/// shape.
/// </summary>
/// <param name="round">Zero-based round index; round 0 uses the unrotated order so the stored reference components compare across variants.</param>
public static class EmbeddingProbeQuestions
{
    private static readonly string[] Questions =
    [
        "Wie lege ich einen neuen Mitarbeiter an?",
        "Zeig mir alle offenen Ferienanträge der Gruppe Nachtdienst für den kommenden Monat und sortiere sie nach Eingangsdatum.",
        "Welche Schichten sind diese Woche noch nicht besetzt?",
        "Ich möchte die Spesen für die letzte Dienstreise erfassen, inklusive Verpflegung, Kilometergeld und Übernachtung im Hotel.",
        "Wie ändere ich den Vertrag eines Mitarbeiters rückwirkend zum ersten Januar, ohne die bereits abgerechneten Stunden zu verlieren?",
        "Kann ich einen Dienstplan für mehrere Wochen im Voraus automatisch erzeugen lassen?",
        "Wo finde ich die Auswertung der geleisteten Überstunden pro Abteilung?",
        "Lösche die Gruppe Aushilfen.",
        "Comment puis-je ajouter un nouveau collaborateur dans le système et lui attribuer directement un contrat de travail à temps partiel?",
        "Quelles absences sont enregistrées pour l'équipe de nuit cette semaine?",
        "Je voudrais exporter le planning du mois prochain au format PDF pour l'envoyer à la direction.",
        "Come posso creare un nuovo dipendente?",
        "Mostrami tutte le assenze approvate del mese scorso, raggruppate per reparto e con il totale delle ore mancanti.",
        "Vorrei modificare l'orario di un turno già assegnato senza cancellare le pause pianificate.",
        "How do I close the accounting period for August so that nobody can change the entries afterwards?",
        "Show me every employee whose contract ends within the next three months.",
        "Which reports can I generate about absences, overtime and expenses for a single group over a full year?",
        "Add a night shift on Friday for Hans Muster.",
        "Wie richte ich eine Eskalationskette ein, damit bei einer unbesetzten Schicht automatisch der Bereitschaftsdienst benachrichtigt wird?",
        "Wer hat gestern die Schicht im Objekt Bahnhof übernommen?",
        "Ich brauche eine Übersicht über alle Mitarbeiter mit abgelaufener Sicherheitsausbildung.",
        "Peux-tu me montrer le solde des heures supplémentaires de chaque collaborateur du groupe Sécurité à la fin du trimestre?",
        "Qual è il modo più veloce per assegnare lo stesso turno a cinque persone contemporaneamente?",
        "Explain what the schedule optimizer does when two shifts collide on the same day for the same person.",
        "Setze den Urlaub von Anna Beispiel auf genehmigt.",
    ];

    public static int Count => Questions.Length;

    /// <summary>Returns the question a caller picks in a given round, rotating the order per round.</summary>
    public static string Get(int round, int index) =>
        Questions[(index + round * RoundStride) % Questions.Length];

    // Coprime with the question count, so a rotation never repeats the previous round's pairing of
    // caller slot to sequence length.
    private const int RoundStride = 7;
}
