using System.IO;
using System.Text;
using System.Text.Json;
using PingPilot.Models;

namespace PingPilot.Services;

/// <summary>
/// Writes ping results incrementally into a temporary JSON-lines log.
/// When the user saves the session, only results from the selected dashboard
/// group are moved to the permanent session, alongside a snapshot of its computers.
/// </summary>
public sealed class SessionStoreService : IDisposable
{
    private readonly string _sessionsDirectory;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };
    private readonly object _sync = new();
    private readonly HashSet<Guid> _computerIds = new();
    private readonly Dictionary<Guid, long> _entryCountsByGroup = new();
    private readonly Dictionary<Guid, HashSet<Guid>> _computerIdsByGroup = new();

    private StreamWriter? _activeWriter;
    private string? _activeLogPath;
    private MonitoringSession? _activeSession;
    private bool _finished;
    private int _writesSinceFlush;
    private const int FlushEveryEntries = 50;

    public SessionStoreService()
    {
        _sessionsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PingPilot",
            "Sessions");
    }

    public MonitoringSession StartSession()
    {
        lock (_sync)
        {
            if (_activeWriter != null)
                throw new InvalidOperationException("A monitoring session is already active.");

            Directory.CreateDirectory(_sessionsDirectory);

            var session = new MonitoringSession { StartedAt = DateTime.Now };
            _activeLogPath = Path.Combine(_sessionsDirectory, $".{session.Id:N}.pending.jsonl");
            var stream = new FileStream(
                _activeLogPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);

            _activeWriter = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = false
            };
            _writesSinceFlush = 0;
            _activeSession = session;
            _computerIds.Clear();
            _entryCountsByGroup.Clear();
            _computerIdsByGroup.Clear();
            _finished = false;
            return session;
        }
    }

    public void Append(MonitoringSession session, SessionLogEntry entry)
    {
        lock (_sync)
        {
            if (_finished || _activeWriter == null || _activeSession?.Id != session.Id)
                return;

            _activeWriter.WriteLine(JsonSerializer.Serialize(entry));
            _writesSinceFlush++;
            // Flush in batches rather than forcing a disk flush for every ping.
            // This lowers I/O overhead during long monitoring sessions.
            if (_writesSinceFlush >= FlushEveryEntries)
            {
                _activeWriter.Flush();
                _writesSinceFlush = 0;
            }
            session.EntryCount++;
            if (_computerIds.Add(entry.ComputerId))
                session.ComputerCount = _computerIds.Count;

            _entryCountsByGroup.TryGetValue(entry.GroupId, out long groupEntryCount);
            _entryCountsByGroup[entry.GroupId] = groupEntryCount + 1;

            if (!_computerIdsByGroup.TryGetValue(entry.GroupId, out HashSet<Guid>? groupComputerIds))
            {
                groupComputerIds = new HashSet<Guid>();
                _computerIdsByGroup[entry.GroupId] = groupComputerIds;
            }

            groupComputerIds.Add(entry.ComputerId);
        }
    }

    /// <summary>
    /// Restarts the active session from zero: the pending log is truncated and all counters
    /// are cleared. Saved (permanent) sessions are not touched.
    /// </summary>
    public void ResetActiveSession(MonitoringSession session)
    {
        lock (_sync)
        {
            EnsureActiveSession(session);

            _activeWriter?.Dispose();
            _activeWriter = new StreamWriter(
                new FileStream(_activeLogPath!, FileMode.Create, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = false
            };

            _writesSinceFlush = 0;
            _computerIds.Clear();
            _entryCountsByGroup.Clear();
            _computerIdsByGroup.Clear();

            session.EntryCount = 0;
            session.ComputerCount = 0;
            session.StartedAt = DateTime.Now;
        }
    }

    /// <summary>Returns an exact count for the selected group from the active log.</summary>
    public (long EntryCount, int ComputerCount) GetActiveSummary(Guid groupId)
    {
        lock (_sync)
        {
            if (_activeSession == null)
                return (0, 0);

            _entryCountsByGroup.TryGetValue(groupId, out long count);
            int computerCount = _computerIdsByGroup.TryGetValue(groupId, out HashSet<Guid>? computerIds)
                ? computerIds.Count
                : 0;

            return (count, computerCount);
        }
    }

    /// <summary>
    /// Saves only the selected group. Events from other groups stay out of the
    /// permanent session file even though the live monitor checks every group.
    /// </summary>
    public void SaveSession(
        MonitoringSession session,
        Guid groupId,
        string groupName,
        IReadOnlyCollection<SessionComputerSnapshot> computers)
    {
        lock (_sync)
        {
            EnsureActiveSession(session);

            if (groupId == Guid.Empty)
                throw new ArgumentException("A group must be selected to save a session.", nameof(groupId));

            long originalEntryCount = session.EntryCount;
            int originalComputerCount = session.ComputerCount;
            DateTime? originalEndedAt = session.EndedAt;
            Guid originalGroupId = session.GroupId;
            string originalGroupName = session.GroupName;
            List<SessionComputerSnapshot> originalComputers = session.Computers;

            session.EndedAt = DateTime.Now;
            session.GroupId = groupId;
            session.GroupName = groupName;
            session.Computers = computers.ToList();

            string logPath = GetLogPath(session.Id);
            string metadataPath = GetMetadataPath(session.Id);
            string metadataTempPath = metadataPath + ".tmp";
            string filteredTempPath = logPath + ".tmp";

            try
            {
                _activeWriter?.Flush();
                _writesSinceFlush = 0;
                _activeWriter?.Dispose();
                _activeWriter = null;

                long filteredCount = 0;
                var filteredComputerIds = new HashSet<Guid>();

                using (var reader = new StreamReader(_activeLogPath!, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                using (var writer = new StreamWriter(filteredTempPath, append: false, new UTF8Encoding(false)))
                {
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        SessionLogEntry? entry;
                        try
                        {
                            entry = JsonSerializer.Deserialize<SessionLogEntry>(line);
                        }
                        catch (JsonException)
                        {
                            continue;
                        }

                        if (entry == null || entry.GroupId != groupId)
                            continue;

                        // Keep the saved group label consistent even if it was renamed
                        // while monitoring was running.
                        entry.GroupName = groupName;
                        writer.WriteLine(JsonSerializer.Serialize(entry));
                        filteredCount++;
                        filteredComputerIds.Add(entry.ComputerId);
                    }
                }

                session.EntryCount = filteredCount;
                var allComputerIds = session.Computers.Select(computer => computer.Id).ToHashSet();
                allComputerIds.UnionWith(filteredComputerIds);
                session.ComputerCount = allComputerIds.Count;

                string metadataJson = JsonSerializer.Serialize(session, _jsonOptions);
                File.WriteAllText(metadataTempPath, metadataJson, new UTF8Encoding(false));

                File.Move(filteredTempPath, logPath, overwrite: true);
                File.Move(metadataTempPath, metadataPath, overwrite: true);

                if (File.Exists(_activeLogPath))
                    File.Delete(_activeLogPath);

                _finished = true;
                _activeLogPath = null;
                _activeSession = null;
                _computerIds.Clear();
                _entryCountsByGroup.Clear();
                _computerIdsByGroup.Clear();
            }
            catch
            {
                if (File.Exists(metadataTempPath))
                    File.Delete(metadataTempPath);
                if (File.Exists(filteredTempPath))
                    File.Delete(filteredTempPath);

                // The pending full log is kept until both permanent files are ready.
                // If finalization failed, reopen it so the user can retry or discard.
                if (_activeWriter == null && _activeSession?.Id == session.Id &&
                    !string.IsNullOrWhiteSpace(_activeLogPath) && File.Exists(_activeLogPath))
                {
                    _activeWriter = new StreamWriter(
                        new FileStream(_activeLogPath, FileMode.Append, FileAccess.Write, FileShare.Read),
                        new UTF8Encoding(false))
                    {
                        AutoFlush = false
                    };
                    _writesSinceFlush = 0;
                }

                session.EntryCount = originalEntryCount;
                session.ComputerCount = originalComputerCount;
                session.EndedAt = originalEndedAt;
                session.GroupId = originalGroupId;
                session.GroupName = originalGroupName;
                session.Computers = originalComputers;
                throw;
            }
        }
    }

    /// <summary>
    /// Saves a copy of the selected group's events as a permanent session on request.
    /// Unlike <see cref="SaveSession"/>, the active log keeps running, so monitoring and
    /// the live journal are not interrupted. Returns null when the group has no events yet.
    /// </summary>
    public MonitoringSession? SaveSnapshot(
        MonitoringSession active,
        Guid groupId,
        string groupName,
        IReadOnlyCollection<SessionComputerSnapshot> computers,
        string name,
        string comment)
    {
        lock (_sync)
        {
            EnsureActiveSession(active);

            if (groupId == Guid.Empty)
                throw new ArgumentException("A group must be selected to save a session.", nameof(groupId));

            // Дописываем буфер на диск, чтобы в копию попали и самые свежие записи.
            _activeWriter?.Flush();
            _writesSinceFlush = 0;

            var saved = new MonitoringSession
            {
                StartedAt = active.StartedAt,
                EndedAt = DateTime.Now,
                GroupId = groupId,
                GroupName = groupName,
                Name = name,
                Comment = comment,
                Computers = computers.ToList()
            };

            string logPath = GetLogPath(saved.Id);
            string metadataPath = GetMetadataPath(saved.Id);
            string logTempPath = logPath + ".tmp";
            string metadataTempPath = metadataPath + ".tmp";

            try
            {
                long count = 0;
                long successfulCount = 0;
                long latencySum = 0;
                long latencyCount = 0;
                var computerIds = new HashSet<Guid>();

                // Журнал открыт на запись основным потоком, поэтому читаем его с общим доступом.
                using (var stream = new FileStream(_activeLogPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                using (var writer = new StreamWriter(logTempPath, append: false, new UTF8Encoding(false)))
                {
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        SessionLogEntry? entry;
                        try
                        {
                            entry = JsonSerializer.Deserialize<SessionLogEntry>(line);
                        }
                        catch (JsonException)
                        {
                            // Последняя строка могла быть записана не до конца.
                            continue;
                        }

                        if (entry == null || entry.GroupId != groupId)
                            continue;

                        entry.GroupName = groupName;
                        writer.WriteLine(JsonSerializer.Serialize(entry));
                        count++;
                        if (entry.Success)
                            successfulCount++;
                        if (entry.LatencyMs is long latency)
                        {
                            latencySum += latency;
                            latencyCount++;
                        }
                        computerIds.Add(entry.ComputerId);
                    }
                }

                if (count == 0)
                {
                    File.Delete(logTempPath);
                    return null;
                }

                saved.EntryCount = count;
                saved.AverageLatencyMs = latencyCount == 0 ? null : (double)latencySum / latencyCount;
                saved.LossPercent = (count - successfulCount) * 100.0 / count;
                computerIds.UnionWith(saved.Computers.Select(computer => computer.Id));
                saved.ComputerCount = computerIds.Count;

                File.WriteAllText(metadataTempPath, JsonSerializer.Serialize(saved, _jsonOptions), new UTF8Encoding(false));

                File.Move(logTempPath, logPath, overwrite: true);
                File.Move(metadataTempPath, metadataPath, overwrite: true);
                return saved;
            }
            catch
            {
                if (File.Exists(logTempPath))
                    File.Delete(logTempPath);
                if (File.Exists(metadataTempPath))
                    File.Delete(metadataTempPath);
                throw;
            }
        }
    }

    public void DiscardSession(MonitoringSession session)
    {
        lock (_sync)
        {
            if (_activeSession?.Id != session.Id)
                return;

            _activeWriter?.Dispose();
            _activeWriter = null;

            if (!string.IsNullOrWhiteSpace(_activeLogPath) && File.Exists(_activeLogPath))
                File.Delete(_activeLogPath);

            _activeLogPath = null;
            _activeSession = null;
            _computerIds.Clear();
            _entryCountsByGroup.Clear();
            _computerIdsByGroup.Clear();
            _finished = true;
        }
    }

    public IReadOnlyList<MonitoringSession> LoadSavedSessions()
    {
        Directory.CreateDirectory(_sessionsDirectory);
        var sessions = new List<MonitoringSession>();

        foreach (string metadataPath in Directory.EnumerateFiles(_sessionsDirectory, "*.meta.json"))
        {
            try
            {
                string json = File.ReadAllText(metadataPath);
                MonitoringSession? session = JsonSerializer.Deserialize<MonitoringSession>(json);
                if (session == null || !File.Exists(GetLogPath(session.Id)))
                    continue;

                session.Computers ??= new List<SessionComputerSnapshot>();
                sessions.Add(session);
            }
            catch (JsonException)
            {
                // Skip malformed metadata while keeping other saved sessions available.
            }
            catch (IOException)
            {
                // Skip a file that cannot currently be read.
            }
        }

        return sessions.OrderByDescending(s => s.StartedAt).ToList();
    }

    public IReadOnlyList<SessionLogEntry> LoadEntries(Guid sessionId)
    {
        string path = GetLogPath(sessionId);
        if (!File.Exists(path))
            return Array.Empty<SessionLogEntry>();

        var entries = new List<SessionLogEntry>();
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                SessionLogEntry? entry = JsonSerializer.Deserialize<SessionLogEntry>(line);
                if (entry != null)
                    entries.Add(entry);
            }
            catch (JsonException)
            {
                // Ignore one malformed line, not the entire session.
            }
        }

        return entries.OrderByDescending(entry => entry.Timestamp).ToList();
    }

    public void UpdateSessionDetails(MonitoringSession session, string name, string comment)
    {
        lock (_sync)
        {
            string metadataPath = GetMetadataPath(session.Id);
            string metadataTempPath = metadataPath + ".tmp";
            string originalName = session.Name;
            string originalComment = session.Comment;

            session.Name = name;
            session.Comment = comment;

            try
            {
                File.WriteAllText(
                    metadataTempPath,
                    JsonSerializer.Serialize(session, _jsonOptions),
                    new UTF8Encoding(false));
                File.Move(metadataTempPath, metadataPath, overwrite: true);
            }
            catch
            {
                if (File.Exists(metadataTempPath))
                    File.Delete(metadataTempPath);

                session.Name = originalName;
                session.Comment = originalComment;
                throw;
            }
        }
    }

    public void DeleteSession(Guid sessionId)
    {
        string metadataPath = GetMetadataPath(sessionId);
        string logPath = GetLogPath(sessionId);

        if (File.Exists(metadataPath))
            File.Delete(metadataPath);
        if (File.Exists(logPath))
            File.Delete(logPath);
    }

    private void EnsureActiveSession(MonitoringSession session)
    {
        if (_finished || _activeSession?.Id != session.Id)
            throw new InvalidOperationException("This monitoring session is no longer active.");
    }

    private string GetLogPath(Guid sessionId) =>
        Path.Combine(_sessionsDirectory, $"{sessionId:N}.jsonl");

    private string GetMetadataPath(Guid sessionId) =>
        Path.Combine(_sessionsDirectory, $"{sessionId:N}.meta.json");

    public void Dispose()
    {
        lock (_sync)
        {
            _activeWriter?.Dispose();
            _activeWriter = null;
        }
    }
}
