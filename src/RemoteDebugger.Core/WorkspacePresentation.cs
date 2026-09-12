using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemoteDebugger.Core;

public sealed record WorkspaceAvailability(bool CanPair, bool CanOperate, bool CanCancel, bool CanDownload)
{
    public static WorkspaceAvailability For(bool session, bool healthy, bool pairing, bool terminating, bool action, bool selectedFile)
    {
        bool operate = session && healthy && !pairing && !terminating;
        return new(!session && !pairing && !terminating, operate, session && action && !terminating, operate && selectedFile);
    }
}

public static class WorkspacePresentation
{
    public static (string Status, string Detail) Footer(int page, string connection, string screen, string resources,
        string files, string diagnostics, string host, string measurements, string directory, string operation) => page switch
    {
        1 => ("Écran distant", screen),
        2 => (resources, measurements),
        3 => (files, string.IsNullOrWhiteSpace(directory) ? "Espace de travail" : directory),
        4 => (diagnostics, "Action : " + operation),
        _ => (connection, string.IsNullOrWhiteSpace(host) ? "Saisissez l’adresse du PC distant" : "PC : " + host)
    };

    public static string Identity(bool session, bool healthy, string host, string fingerprint) => !session
        ? "Aucune connexion authentifiée."
        : $"{(healthy ? "Identité vérifiée" : "Dernière identité vérifiée")} · {host} · {fingerprint[..Math.Min(16, fingerprint.Length)]}…";

    public static string ArgumentsFor(string template, int pid)
    {
        var value = JsonNode.Parse(template) as JsonObject ?? throw new JsonException("Les arguments doivent être un objet JSON.");
        if (value.ContainsKey("pid")) value["pid"] = pid;
        return value.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static bool UsesPid(string template) => JsonNode.Parse(template) is JsonObject value && value.ContainsKey("pid");
}
