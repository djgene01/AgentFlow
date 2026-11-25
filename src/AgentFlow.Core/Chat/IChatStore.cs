namespace AgentFlow.Core.Chat;

public sealed record Project(
    string Id,
    string Name,
    string FolderPath,
    string? LastSessionId,
    string? LastModel,
    string? LastEffort,
    string? LastPermissionMode,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt);

/// <summary>Persistence for projects and their chat. Implemented by AgentFlow.Storage.</summary>
public interface IChatStore
{
    IReadOnlyList<Project> GetProjects();
    Project? GetProject(string id);
    void SaveProject(Project project);
    void DeleteProject(string id);

    IReadOnlyList<ChatEntry> GetEntries(string projectId);
    long NextSeq(string projectId);
    void SaveEntry(ChatEntry entry);

    /// <summary>Appends a raw CLI output line so the chat can be rebuilt after a parser fix.</summary>
    void AppendRawEvent(string projectId, Guid turnId, string? sessionId, string json);

    string? GetSetting(string key);
    void SetSetting(string key, string? value);
}
