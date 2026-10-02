using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Helpers;

/// <summary>One node's own setting overrides plus their effective (inherited) values, as shown and
/// edited on Admin/Nodes/{id}. <see cref="Snapshot"/> is the stale-save guard: a hash of every value
/// the edit form can submit, rendered into the form and re-computed from a fresh read at save time —
/// a mismatch means someone else changed the node in between, and the save is rejected rather than
/// silently overwriting their change.</summary>
public sealed record NodeSettingsState(
    int EffectiveRetentionDays, int? RetentionOverride,
    string ArchiveEnabledOverride, bool EffectiveArchiveEnabled, int? ArchiveRetentionOverride, int EffectiveArchiveRetentionDays,
    string ModelFamilyOverride, string DFineWeightsOverride, string YoloXSizeOverride, string LocalModelNameOverride,
    int? MaxFpsOverride, string EffectiveModelFamily, string AspectModeOverride, string EffectiveAspectMode,
    string DFineTensorRtModeOverride, string EffectiveDFineTensorRtMode,
    string DetectionBackendOverride, string EffectiveDetectionBackend, string ExternalInferenceUrlOverride,
    string ExternalInferenceModelOverride, int? ExternalInferenceInputSizeOverride, bool HasExternalInferenceApiKeyOverride,
    string Snapshot)
{
    public static async Task<NodeSettingsState> LoadAsync(Node n, ISettingsResolver settings)
    {
        async Task<string?> Own(string key) => await settings.GetOwnOverrideAsync(SettingScope.Node, n.Id, key);
        static int? ParseInt(string? raw) => int.TryParse(raw, out var v) ? v : null;

        var retentionOwn = await Own("Retention.Days");
        var archiveEnabledOverride = await Own("Archive.Enabled") ?? "";
        var archiveRetentionOwn = await Own("Archive.RetentionDays");
        var modelFamilyOverride = await Own("Detection.ModelFamily") ?? "";
        var dfineWeightsOverride = await Own("Detection.DFineWeights") ?? "";
        var yoloXSizeOverride = await Own("Detection.YoloXSize") ?? "";
        var localModelNameOverride = await Own("Detection.LocalModelName") ?? "";
        var maxFpsOwn = await Own("Detection.MaxFps");
        var aspectModeOverride = await Own("Detection.AspectMode") ?? "";
        var dfineTensorRtModeOverride = await Own("Detection.DFineTensorRtMode") ?? "";
        var backendOverride = await Own("Detection.Backend") ?? "";
        var externalUrlOverride = await Own("Detection.ExternalInferenceUrl") ?? "";
        var externalModelOverride = await Own("Detection.ExternalInferenceModel") ?? "";
        var externalInputSizeOwn = await Own("Detection.ExternalInferenceInputSize");
        var hasExternalApiKeyOverride = !string.IsNullOrEmpty(await Own("Detection.ExternalInferenceApiKey"));

        // Computed from the raw values just read (not the parsed/defaulted ones), so the render-time
        // and save-time hashes match byte-for-byte when nothing changed.
        var snapshot = ComputeSnapshot(
            n.Name, n.StorageRootPath, n.ArchiveRootPath, n.AiAccelerator?.ToString(), n.DisableAiObjectDetection.ToString(),
            n.DirectStreamingMode, n.AllowInsecureClientEndpoint?.ToString(), n.ClientEndpointHost, n.ClientCertPfxPath,
            n.PrimaryProxyId?.ToString(), n.BackupProxyId?.ToString(), n.BackupNodeId?.ToString(),
            NormalizeInt(retentionOwn), modelFamilyOverride, dfineWeightsOverride, yoloXSizeOverride,
            NormalizeInt(maxFpsOwn), aspectModeOverride, dfineTensorRtModeOverride,
            backendOverride, externalUrlOverride, externalModelOverride, NormalizeInt(externalInputSizeOwn),
            hasExternalApiKeyOverride.ToString(), archiveEnabledOverride, NormalizeInt(archiveRetentionOwn),
            localModelNameOverride);

        return new NodeSettingsState(
            await settings.GetAsync("Retention.Days", 30, nodeId: n.Id), ParseInt(retentionOwn),
            archiveEnabledOverride, await settings.GetAsync("Archive.Enabled", false, nodeId: n.Id),
            ParseInt(archiveRetentionOwn), await settings.GetAsync("Archive.RetentionDays", 0, nodeId: n.Id),
            modelFamilyOverride, dfineWeightsOverride, yoloXSizeOverride, localModelNameOverride,
            ParseInt(maxFpsOwn), await settings.GetAsync("Detection.ModelFamily", "Auto", nodeId: n.Id),
            aspectModeOverride, await settings.GetAsync("Detection.AspectMode", "Letterbox", nodeId: n.Id),
            dfineTensorRtModeOverride, await settings.GetAsync("Detection.DFineTensorRtMode", "Off", nodeId: n.Id),
            backendOverride, await settings.GetAsync("Detection.Backend", "BuiltIn", nodeId: n.Id),
            externalUrlOverride, externalModelOverride, ParseInt(externalInputSizeOwn), hasExternalApiKeyOverride,
            snapshot);
    }

    /// <summary>A node's archive root must be short enough that a segment path built under it stays
    /// within Segment.FilePath's 450-char column, and separate from the node's recording root so the
    /// orphan-import sweep never re-imports archived files. Returns null when valid.</summary>
    public static string? ValidateArchiveRoot(string? archiveRoot, string? storageRoot)
    {
        if (string.IsNullOrWhiteSpace(archiveRoot)) return null;
        if (archiveRoot.Length > 300)
            return "Archive storage root is too long — keep it under 300 characters.";
        if (!string.IsNullOrWhiteSpace(storageRoot))
        {
            var a = archiveRoot.TrimEnd('/', '\\');
            var s = storageRoot.TrimEnd('/', '\\');
            if (a.Equals(s, StringComparison.OrdinalIgnoreCase)
                || a.StartsWith(s + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || a.StartsWith(s + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || s.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || s.StartsWith(a + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "Archive storage root must be a separate location from this node's recording storage root.";
        }
        return null;
    }

    /// <summary>"010" and "10" (or null and "") must never register as a spurious mismatch.</summary>
    private static string NormalizeInt(string? raw) => int.TryParse(raw, out var v) ? v.ToString() : "";

    /// <summary>U+0001 separates fields (it can't appear in any value) and null folds to U+0000 so
    /// null and "" never collide. Secrets are excluded — they use "blank means leave alone".</summary>
    private static string ComputeSnapshot(params string?[] parts)
    {
        var joined = string.Join('\u0001', parts.Select(p => p ?? "\u0000"));
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash);
    }
}

/// <summary>confirm() prompts for the node actions — shared by the Nodes list's action menu and the
/// node edit page. Razor HTML-encodes the result, so no manual entity-escaping is needed.</summary>
public static class NodeConfirm
{
    public static string Restart(string name, int cameraCount) =>
        $"return confirm('Restart the LarisVMS node service on \"{name}\"?\\n\\nRecording on its {cameraCount} camera(s) will stop for a few seconds. This restarts the service only, not the machine.');";

    public static string ResetAuth(string name) =>
        $"return confirm('Reset the check-in nonce for \"{name}\"?\\n\\nUse this only if the node shows repeated auth failures after a missed heartbeat — it clears the rolling nonce so the node re-syncs on its next check-in. No effect on a healthy node.');";

    public static string Maintenance(bool currentlyInMaintenance, int cameraCount, string? backupNodeName)
    {
        var message = currentlyInMaintenance
            ? "Take this node out of maintenance? Its cameras return to it once it is a confirmed-healthy quorum."
            : backupNodeName is not null
                ? $"Put this node into maintenance? Its {cameraCount} camera(s) fail over to node {backupNodeName} for recording within about 15 seconds, and will not fail back until you turn maintenance off."
                : $"Put this node into maintenance? It has NO backup node - its {cameraCount} camera(s) will STOP recording until maintenance is turned off.";
        return $"return confirm('{message}');";
    }

    public static string Delete(string name) =>
        $"return confirm('Delete node \"{name}\"? Cameras assigned to it will be unassigned, not deleted. Past recordings are unaffected.');";
}
