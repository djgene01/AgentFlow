using System.Globalization;
using System.Text.Json;
using AgentFlow.Core.Chat;
using AgentFlow.Core.Workflows;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AgentFlow.Storage;

/// <summary>
/// SQLite store under %LOCALAPPDATA%\AgentFlow\app.db. Claude Code's own transcripts stay where
/// the CLI keeps them; this database holds only the app's view of each project's chat.
/// </summary>
public sealed partial class SqliteChatStore : IChatStore, IRunStore, IDisposable
{
    private const int SchemaVersion = 3;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnection _db;
    private readonly Lock _gate = new();

    public SqliteChatStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        _db.Open();
        _db.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        Migrate();
    }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentFlow", "app.db");

    private void Migrate()
    {
        var version = _db.ExecuteScalar<long>("PRAGMA user_version;");
        if (version >= SchemaVersion) return;

        if (version < 1) CreateVersion1();
        if (version < 2) CreateVersion2();
        if (version < 3) _db.Execute("ALTER TABLE Run ADD COLUMN StateJson TEXT;");
        _db.Execute($"PRAGMA user_version = {SchemaVersion};");
    }

    private void CreateVersion2() => _db.Execute("""
        CREATE TABLE IF NOT EXISTS Run (
            Id TEXT PRIMARY KEY,
            ProjectId TEXT NOT NULL REFERENCES Project(Id) ON DELETE CASCADE,
            WorkflowName TEXT NOT NULL,
            WorkflowJson TEXT NOT NULL,
            Task TEXT NOT NULL,
            Status TEXT NOT NULL,
            BaseCommit TEXT,
            CurrentStageKey TEXT,
            StartedAt TEXT NOT NULL,
            FinishedAt TEXT
        );
        CREATE INDEX IF NOT EXISTS IX_Run_Project ON Run (ProjectId, StartedAt);
        CREATE TABLE IF NOT EXISTS StageRun (
            Id TEXT PRIMARY KEY,
            RunId TEXT NOT NULL REFERENCES Run(Id) ON DELETE CASCADE,
            StageKey TEXT NOT NULL,
            StageName TEXT NOT NULL,
            Attempt INTEGER NOT NULL,
            Status TEXT NOT NULL,
            Model TEXT, Effort TEXT, SessionId TEXT,
            ResultText TEXT, StructuredOutputJson TEXT,
            CostUsd TEXT, InputTokens INTEGER, OutputTokens INTEGER, DurationMs INTEGER,
            StartedAt TEXT NOT NULL,
            FinishedAt TEXT
        );
        CREATE INDEX IF NOT EXISTS IX_StageRun_Run ON StageRun (RunId, StartedAt);
        """);

    private void CreateVersion1()
    {
        _db.Execute("""
            CREATE TABLE IF NOT EXISTS Project (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                FolderPath TEXT NOT NULL UNIQUE COLLATE NOCASE,
                LastSessionId TEXT,
                LastModel TEXT,
                LastEffort TEXT,
                LastPermissionMode TEXT,
                CreatedAt TEXT NOT NULL,
                LastActivityAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ChatItem (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL REFERENCES Project(Id) ON DELETE CASCADE,
                Seq INTEGER NOT NULL,
                Kind TEXT NOT NULL,
                PayloadJson TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UNIQUE (ProjectId, Seq)
            );
            CREATE TABLE IF NOT EXISTS RawEvent (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProjectId TEXT NOT NULL REFERENCES Project(Id) ON DELETE CASCADE,
                TurnId TEXT NOT NULL,
                SessionId TEXT,
                Json TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_RawEvent_Turn ON RawEvent (ProjectId, TurnId);
            CREATE TABLE IF NOT EXISTS Setting (Key TEXT PRIMARY KEY, Value TEXT);
            """);
    }

    // ---- projects ----

    public IReadOnlyList<Project> GetProjects()
    {
        lock (_gate)
            return _db.Query<ProjectRow>("SELECT * FROM Project ORDER BY LastActivityAt DESC").Select(ToProject).ToList();
    }

    public Project? GetProject(string id)
    {
        lock (_gate)
            return _db.QuerySingleOrDefault<ProjectRow>("SELECT * FROM Project WHERE Id = @id", new { id }) is { } r ? ToProject(r) : null;
    }

    public void SaveProject(Project p)
    {
        lock (_gate)
        {
            _db.Execute("""
                INSERT INTO Project (Id, Name, FolderPath, LastSessionId, LastModel, LastEffort, LastPermissionMode, CreatedAt, LastActivityAt)
                VALUES (@Id, @Name, @FolderPath, @LastSessionId, @LastModel, @LastEffort, @LastPermissionMode, @CreatedAt, @LastActivityAt)
                ON CONFLICT(Id) DO UPDATE SET
                    Name = excluded.Name, FolderPath = excluded.FolderPath, LastSessionId = excluded.LastSessionId,
                    LastModel = excluded.LastModel, LastEffort = excluded.LastEffort,
                    LastPermissionMode = excluded.LastPermissionMode, LastActivityAt = excluded.LastActivityAt
                """,
                new
                {
                    p.Id, p.Name, p.FolderPath, p.LastSessionId, p.LastModel, p.LastEffort, p.LastPermissionMode,
                    CreatedAt = Iso(p.CreatedAt), LastActivityAt = Iso(p.LastActivityAt),
                });
        }
    }

    public void DeleteProject(string id)
    {
        lock (_gate) _db.Execute("DELETE FROM Project WHERE Id = @id", new { id });
    }

    // ---- chat ----

    public IReadOnlyList<ChatEntry> GetEntries(string projectId)
    {
        lock (_gate)
        {
            var rows = _db.Query<ChatRow>("SELECT * FROM ChatItem WHERE ProjectId = @projectId ORDER BY Seq", new { projectId });
            var list = new List<ChatEntry>();
            foreach (var r in rows)
            {
                ChatEntryData? data;
                try { data = JsonSerializer.Deserialize<ChatEntryData>(r.PayloadJson, Json); }
                catch (JsonException) { data = new NoticeData(NoticeLevel.Warning, $"(an unreadable chat item of kind '{r.Kind}')"); }
                if (data is not null)
                    list.Add(new ChatEntry(Guid.Parse(r.Id), r.ProjectId, r.Seq, data, Parse(r.CreatedAt)));
            }
            return list;
        }
    }

    public long NextSeq(string projectId)
    {
        lock (_gate)
        {
            // Seq is reserved in memory as well, because entries are created before they are saved.
            var dbMax = _db.ExecuteScalar<long?>("SELECT MAX(Seq) FROM ChatItem WHERE ProjectId = @projectId", new { projectId }) ?? 0;
            var next = Math.Max(dbMax, _reserved.GetValueOrDefault(projectId)) + 1;
            _reserved[projectId] = next;
            return next;
        }
    }

    private readonly Dictionary<string, long> _reserved = new(StringComparer.Ordinal);

    public void SaveEntry(ChatEntry e)
    {
        var payload = JsonSerializer.Serialize(e.Data, Json);
        lock (_gate)
        {
            _db.Execute("""
                INSERT INTO ChatItem (Id, ProjectId, Seq, Kind, PayloadJson, CreatedAt)
                VALUES (@Id, @ProjectId, @Seq, @Kind, @PayloadJson, @CreatedAt)
                ON CONFLICT(Id) DO UPDATE SET PayloadJson = excluded.PayloadJson, Kind = excluded.Kind
                """,
                new { Id = e.Id.ToString("D"), e.ProjectId, e.Seq, Kind = KindOf(e.Data), PayloadJson = payload, CreatedAt = Iso(e.CreatedAt) });
        }
    }

    public void AppendRawEvent(string projectId, Guid turnId, string? sessionId, string json)
    {
        lock (_gate)
        {
            _db.Execute("INSERT INTO RawEvent (ProjectId, TurnId, SessionId, Json, CreatedAt) VALUES (@projectId, @turnId, @sessionId, @json, @now)",
                new { projectId, turnId = turnId.ToString("D"), sessionId, json, now = Iso(DateTimeOffset.UtcNow) });
        }
    }

    // ---- settings ----

    public string? GetSetting(string key)
    {
        lock (_gate) return _db.ExecuteScalar<string?>("SELECT Value FROM Setting WHERE Key = @key", new { key });
    }

    public void SetSetting(string key, string? value)
    {
        lock (_gate)
            _db.Execute("INSERT INTO Setting (Key, Value) VALUES (@key, @value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value", new { key, value });
    }

    public void Dispose() => _db.Dispose();

    // ---- helpers ----

    private static string KindOf(ChatEntryData d) => d switch
    {
        UserMessageData => "user",
        AssistantTextData => "assistant",
        ToolCallData => "tool",
        NoticeData => "notice",
        TurnSummaryData => "summary",
        SessionDividerData => "divider",
        _ => d.GetType().Name,
    };

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static Project ToProject(ProjectRow r) => new(r.Id, r.Name, r.FolderPath, r.LastSessionId, r.LastModel, r.LastEffort,
        r.LastPermissionMode, Parse(r.CreatedAt), Parse(r.LastActivityAt));

    private sealed class ProjectRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string FolderPath { get; set; } = "";
        public string? LastSessionId { get; set; }
        public string? LastModel { get; set; }
        public string? LastEffort { get; set; }
        public string? LastPermissionMode { get; set; }
        public string CreatedAt { get; set; } = "";
        public string LastActivityAt { get; set; } = "";
    }

    private sealed class ChatRow
    {
        public string Id { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public long Seq { get; set; }
        public string Kind { get; set; } = "";
        public string PayloadJson { get; set; } = "";
        public string CreatedAt { get; set; } = "";
    }
}
