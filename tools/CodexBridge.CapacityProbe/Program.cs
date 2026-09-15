using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexBridge.Core;
using CodexBridge.Host.Services;
using CodexBridge.Remote.Protocol;
using CodexBridge.Windows;

var root = args.Length == 1 ? Path.GetFullPath(args[0]) : throw new ArgumentException("Probe root is required.");
Directory.CreateDirectory(root);
var projectPaths = Enumerable.Range(0, 100).Select(index => Path.Combine(root, $"project-{index:D3}")).ToArray();
foreach (var path in projectPaths) Directory.CreateDirectory(path);
var rollout = Path.Combine(projectPaths[0], "large.jsonl");
if (!File.Exists(rollout))
{
    await using var writer = new StreamWriter(rollout, false, new UTF8Encoding(false));
    for (var index = 1; index <= 100_000; index++)
    {
        var line = JsonSerializer.Serialize(new
        {
            timestamp = "2026-08-22T00:00:00Z",
            type = "event_msg",
            payload = new { type = index % 2 == 0 ? "agent_message" : "user_message", message = $"event-{index}" },
        });
        await writer.WriteLineAsync(line);
    }
}

var threads = Enumerable.Range(0, 10_000).Select(index => new ThreadSummary(
    index == 0 ? "large-thread" : $"thread-{index:D5}",
    $"thread {index}",
    projectPaths[0],
    $"preview {index}",
    100_000 - index,
    false,
    index == 0 ? rollout : Path.Combine(projectPaths[0], $"missing-{index}.jsonl"),
    "probe")).ToArray();
var catalog = new ProbeCatalog(projectPaths[0], threads);
var policy = new TargetPolicy(projectPaths);
var indexStore = new RolloutIndexStore(Path.Combine(root, "indexes"));
var workspace = new WorkspaceQueryService(catalog, new RolloutConversationReader(), policy, index: indexStore);

GC.Collect();
var memoryBefore = GC.GetTotalMemory(true);
var build = Stopwatch.StartNew();
await indexStore.GetSummaryAsync(rollout);
build.Stop();
var memoryAfterBuild = GC.GetTotalMemory(true);

var indexHit = Stopwatch.StartNew();
await indexStore.GetSummaryAsync(rollout);
indexHit.Stop();

var projectsTimer = Stopwatch.StartNew();
var projects = await workspace.GetProjectsAsync();
projectsTimer.Stop();

var projectId = WorkspaceQueryService.CreateProjectId(projectPaths[0]);
var threadsTimer = Stopwatch.StartNew();
var threadPage = await workspace.GetThreadsAsync(projectId, pageSize: 30);
threadsTimer.Stop();

var eventsTimer = Stopwatch.StartNew();
var eventPage = await workspace.GetEventsAsync("large-thread", pageSize: 40);
eventsTimer.Stop();
var responseBytes = RpcJson.Encode(new RpcResponse(
    true,
    JsonSerializer.SerializeToElement(eventPage, RpcJson.Options),
    null)).Length;

var result = new
{
    projects = projects.Count,
    largeProjectThreads = threadPage.TotalApproximate,
    events = eventPage.LatestSequence,
    indexBuildMs = build.Elapsed.TotalMilliseconds,
    indexHitMs = indexHit.Elapsed.TotalMilliseconds,
    projectsMs = projectsTimer.Elapsed.TotalMilliseconds,
    threadsFirstPageMs = threadsTimer.Elapsed.TotalMilliseconds,
    eventsFirstPageMs = eventsTimer.Elapsed.TotalMilliseconds,
    responseBytes,
    managedMemoryDeltaBytes = memoryAfterBuild - memoryBefore,
    pass = projectsTimer.ElapsedMilliseconds < 300 &&
        threadsTimer.ElapsedMilliseconds < 500 &&
        eventsTimer.ElapsedMilliseconds < 500 &&
        responseBytes <= RpcJson.MaximumResponsePayloadLength,
};
Console.WriteLine(JsonSerializer.Serialize(result));
return result.pass ? 0 : 1;

sealed class ProbeCatalog(string largeProject, ThreadSummary[] threads) : IThreadCatalog
{
    public Task<IReadOnlyList<ThreadSummary>> ListByProjectAsync(
        string projectPath,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ThreadSummary>>(
            string.Equals(projectPath, largeProject, StringComparison.OrdinalIgnoreCase) ? threads : []);

    public Task<ThreadSummary?> GetAsync(string threadId, CancellationToken cancellationToken = default) =>
        Task.FromResult(threads.FirstOrDefault(thread => thread.Id == threadId));
}
