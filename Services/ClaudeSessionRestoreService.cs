using System;
using System.Linq;

namespace DevezCode.Services;

public sealed record ClaudeSessionRestoreState(
    ClaudeTranscriptSnapshot Transcript,
    string? Model,
    string Effort,
    string PermissionMode,
    long? ContextTokens,
    long? ContextWindow);

/// <summary>CLI에서 이어받는 Claude 방의 transcript와 statusLine 상태를 GUI 초기값으로 합친다.</summary>
public static class ClaudeSessionRestoreService
{
    public static ClaudeSessionRestoreState Load(string roomId, string? cwd)
    {
        var sessionId = SettingsService.LoadClaudeCodeRoomSession(roomId);
        var transcript = SessionExporter.LoadClaudeConversationSnapshot(roomId, cwd);
        var runtime = ModelEffortService.ReadPersistedMetadata(roomId);
        var captured = ModelEffortService.ReadCapturedStatusLine(sessionId);
        var exactRuntime = !string.IsNullOrWhiteSpace(sessionId)
                           && string.Equals(runtime.SessionId, sessionId, StringComparison.OrdinalIgnoreCase);
        var legacyRuntime = string.IsNullOrWhiteSpace(runtime.SessionId);

        var exactRuntimeModel = exactRuntime ? CleanModel(runtime.Model) : null;
        exactRuntimeModel ??= CleanModel(captured.Model);
        var legacyRuntimeModel = legacyRuntime ? CleanModel(runtime.Model) : null;
        var exactRuntimeEffort = exactRuntime ? CleanEffort(runtime.Effort) : null;
        exactRuntimeEffort ??= CleanEffort(captured.Effort);
        var legacyRuntimeEffort = legacyRuntime ? CleanEffort(runtime.Effort) : null;
        var model = exactRuntimeModel
                    ?? CleanModel(transcript.Model)
                    ?? legacyRuntimeModel
                    ?? CleanModel(SettingsService.LoadClaudeCodeRoomModel(roomId));
        var effort = exactRuntimeEffort
                     ?? CleanEffort(transcript.Effort)
                     ?? legacyRuntimeEffort
                     ?? CleanEffort(SettingsService.LoadClaudeCodeRoomEffort(roomId))
                     ?? "high";
        var permissionMode = ClaudeGlobalSettings.IsSupportedPermissionMode(transcript.PermissionMode)
            ? transcript.PermissionMode!
            : SettingsService.LoadClaudeCodeRoomPermissionMode(roomId);
        var contextTokens = exactRuntime && runtime.ContextTokens is >= 0
            ? runtime.ContextTokens
            : captured.ContextTokens is >= 0 ? captured.ContextTokens : transcript.ContextTokens;
        var contextWindow = exactRuntime && runtime.ContextWindow is > 0
            ? runtime.ContextWindow
            : captured.ContextWindow is > 0 ? captured.ContextWindow : transcript.ContextWindow;

        return new ClaudeSessionRestoreState(
            transcript, model, effort, permissionMode, contextTokens, contextWindow);
    }

    private static string? CleanModel(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.StartsWith('<') || value.Length > 160) return null;
        return value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '/') ? value : null;
    }

    private static string? CleanEffort(string? value)
        => SettingsService.IsSupportedClaudeCodeEffort(value) ? value : null;
}
