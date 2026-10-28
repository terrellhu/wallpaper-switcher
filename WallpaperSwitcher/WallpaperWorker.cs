using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace WallpaperSwitcher;

internal sealed record WorkerRequest(string Id, string Operation, string DataDirectory, WallpaperFit Fit,
    string[] Paths, string? SnapshotHash);
internal sealed record WorkerResult(string Id, bool Success, string? Error);

// Every invocation is a fresh STA process. The parent assigns a kill-on-close job before sending GO.
internal static class WallpaperWorker
{
    internal static string? SnapshotHash(string data) => File.Exists(Path.Combine(data, "restore.json"))
        ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(data, "restore.json")))) : null;

    internal static int Run(string requestPath)
    {
        WorkerRequest? request = null;
        string resultPath = Path.Combine(Path.GetDirectoryName(requestPath)!, "result.json");
        try
        {
            request = JsonSerializer.Deserialize<WorkerRequest>(File.ReadAllText(requestPath))!;
            if (Console.ReadLine() != request.Id) return 4; // EOF also exits if the parent died before assignment.
            using var mutex = new Mutex(false, @"Local\SimpleWallpaperSwitcher.DesktopWorker");
            bool acquired;
            try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new InvalidOperationException("另一个壁纸操作尚未结束。");
            try
            {
                // Fault injection never calls desktop APIs and requires an isolated test marker.
                if (request.Operation.StartsWith("test-", StringComparison.Ordinal))
                {
                    if (!File.Exists(Path.Combine(request.DataDirectory, "allow-worker-tests"))) throw new InvalidOperationException("测试未启用。");
                    File.WriteAllText(Path.Combine(request.DataDirectory, "test-worker-started"), Environment.ProcessId.ToString());
                    if (request.Operation == "test-hang") Thread.Sleep(Timeout.Infinite);
                    if (request.Operation == "test-crash") Environment.Exit(91);
                    if (request.Operation == "test-delay") Thread.Sleep(700);
                }
                else
                {
                    WallpaperNative.AssertDesktopAvailable();
                    using var wallpaper = new WindowsWallpaper(request.DataDirectory);
                    wallpaper.SetFitMode(request.Fit);
                    switch (request.Operation)
                    {
                        case "capture":
                            if (wallpaper.HasPendingRestore) throw new IOException("原背景记录尚未恢复，禁止覆盖。");
                            wallpaper.Capture();
                            break;
                        case "apply":
                            if (!wallpaper.HasPendingRestore) throw new IOException("缺少原背景记录，已阻止切换。");
                            wallpaper.ApplyFrame(request.Paths);
                            break;
                        case "restore":
                            if (request.SnapshotHash is null || SnapshotHash(request.DataDirectory) != request.SnapshotHash)
                                throw new IOException("恢复记录已变化，已停止操作。");
                            wallpaper.Restore(keepRecord: true);
                            break;
                        case "inspect": _ = WallpaperNative.GetImage(); break;
                        default: throw new ArgumentException("未知壁纸操作。");
                    }
                    WallpaperNative.AssertDesktopAvailable();
                }
            }
            finally { mutex.ReleaseMutex(); }
            SettingsStore.WriteAtomic(resultPath, new WorkerResult(request.Id, true, null));
            return 0;
        }
        catch (Exception ex)
        {
            try { SettingsStore.WriteAtomic(resultPath, new WorkerResult(request?.Id ?? "", false, ex.Message)); } catch { }
            return 1;
        }
    }
}

public sealed class SafeWallpaperService : IWallpaperService, IDisposable
{
    private readonly string executable;
    private readonly string data;
    private readonly TimeSpan timeout;
    private readonly object stateLock = new();
    private CancellationTokenSource? activeCancellation;
    private int inFlight, generation, sessionGeneration;
    private bool disposed, stopped, sessionSuspended;
    private string StopPath => Path.Combine(data, "safety-stop.json");
    public bool HasPendingRestore => File.Exists(Path.Combine(data, "restore.json"));
    public bool IsSafetyStopped { get { lock (stateLock) return stopped || File.Exists(StopPath); } }
    public WallpaperFit FitMode { get; private set; } = WallpaperFit.Fill;
    public bool AutoCollage { get; set; } = true;
    public int CollageCount { get; set; }
    private string[] currentPaths = [];
    public int LastWorkerProcessId { get; private set; }
    public SafeWallpaperService(string? executablePath = null, string? dataDirectory = null, int timeoutMilliseconds = 8000)
    {
        executable = executablePath ?? Environment.ProcessPath!;
        data = dataDirectory ?? SettingsStore.DataDirectory;
        timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);
    }
    public void Capture() => Run("capture");
    public void Apply(string path) => ApplyFrame([path]);
    public void ApplyFrame(IReadOnlyList<string> paths)
    {
        Run("apply", paths.ToArray()); currentPaths = paths.ToArray();
    }
    public IReadOnlyList<string> PlanFrame(IReadOnlyList<string> candidates) =>
        CollageRenderer.PlanFrame(candidates, WindowsWallpaper.TargetSize(FitMode), AutoCollage, CollageCount);
    public void Restore() { if (HasPendingRestore) Run("restore"); }
    public void Recover()
    {
        // Explicit manual recovery is allowed after a failed session resume; UI gates locked sessions.
        lock (stateLock) sessionSuspended = false;
        Run(HasPendingRestore ? "restore" : "inspect", recovery: true);
    }
    public void SuspendSession()
    {
        lock (stateLock) { sessionSuspended = true; sessionGeneration++; activeCancellation?.Cancel(); }
    }
    public void EndSessionHold()
    {
        lock (stateLock)
        {
            if (IsSafetyStopped) throw new WallpaperOperationException("真正的壁纸错误需要手动恢复。");
            sessionSuspended = false;
        }
    }
    public void SetFitMode(WallpaperFit mode, bool updateDesktop = false)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        FitMode = mode;
        if (updateDesktop && currentPaths.Length > 0) ApplyFrame(currentPaths);
    }
    public void StopForSafety(string reason)
    {
        lock (stateLock)
        {
            stopped = true; generation++;
            activeCancellation?.Cancel();
            try { SettingsStore.WriteAtomic(StopPath, new { Time = DateTimeOffset.UtcNow, Reason = reason }); } catch { }
        }
    }
    internal void RunTest(string operation) => Run(operation);
    private void Run(string operation, string[]? paths = null, bool recovery = false)
    {
        if (Interlocked.CompareExchange(ref inFlight, 1, 0) != 0)
            throw new InvalidOperationException("正在处理壁纸，请勿重复操作。");
        using var cancellation = new CancellationTokenSource();
        int startGeneration, startSessionGeneration;
        try
        {
            lock (stateLock)
            {
                if (disposed) throw new ObjectDisposedException(nameof(SafeWallpaperService));
                if (IsSafetyStopped && !recovery) throw new WallpaperOperationException("安全停止中，请先手动恢复原背景。");
                if (sessionSuspended) throw new SessionInterruptedException();
                startSessionGeneration = sessionGeneration;
                startGeneration = generation; activeCancellation = cancellation;
            }
            string id = Guid.NewGuid().ToString("N");
            var request = new WorkerRequest(id, operation, data, FitMode, paths ?? [], WallpaperWorker.SnapshotHash(data));
            try
            {
                RunProcessAsync(request, cancellation.Token).GetAwaiter().GetResult();
                lock (stateLock)
                {
                    if (generation != startGeneration) throw new OperationCanceledException();
                    if (sessionGeneration != startSessionGeneration) throw new SessionInterruptedException();
                    if (operation == "restore")
                    {
                        if (WallpaperWorker.SnapshotHash(data) != request.SnapshotHash) throw new IOException("恢复记录发生变化。");
                        File.Delete(Path.Combine(data, "restore.json"));
                    }
                    if (recovery) { File.Delete(StopPath); stopped = false; }
                }
            }
            catch (SessionInterruptedException) { throw; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested && !IsSafetyStopped
                && sessionGeneration != startSessionGeneration)
            { throw new SessionInterruptedException(); }
            catch (Exception ex)
            {
                StopForSafety(ex.Message);
                throw new WallpaperOperationException("壁纸操作未确认完成，已安全停止；恢复记录已保留。" + ex.Message);
            }
        }
        finally
        {
            lock (stateLock) activeCancellation = null;
            Interlocked.Exchange(ref inFlight, 0);
        }
    }
    private async Task RunProcessAsync(WorkerRequest request, CancellationToken cancellation)
    {
        string folder = Path.Combine(data, "operations", request.Id);
        string requestPath = Path.Combine(folder, "request.json"), resultPath = Path.Combine(folder, "result.json");
        SettingsStore.WriteAtomic(requestPath, request);
        using var job = WorkerJob.Create();
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true
        };
        process.StartInfo.ArgumentList.Add("--wallpaper-worker");
        process.StartInfo.ArgumentList.Add(requestPath);
        bool started = false;
        try
        {
            started = process.Start();
            if (!started) throw new IOException("无法启动壁纸辅助进程。");
            LastWorkerProcessId = process.Id;
            WorkerJob.Assign(job, process);
            using var timeLimit = new CancellationTokenSource(timeout);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeLimit.Token);
            try
            {
                await process.StandardInput.WriteLineAsync(request.Id.AsMemory(), deadline.Token).ConfigureAwait(false);
                process.StandardInput.Close();
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A deadline already reached takes priority over a simultaneous lock notification.
                if (timeLimit.IsCancellationRequested)
                    throw new TimeoutException($"辅助进程超过 {timeout.TotalSeconds:g} 秒未响应。");
                cancellation.ThrowIfCancellationRequested();
                throw;
            }
            if (!File.Exists(resultPath)) throw new IOException("辅助进程异常退出，未返回结果。");
            var result = JsonSerializer.Deserialize<WorkerResult>(File.ReadAllText(resultPath));
            if (result?.Id != request.Id || !result.Success || process.ExitCode != 0)
                throw new IOException(result?.Error ?? "辅助进程返回无效结果。");
        }
        finally
        {
            if (started)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            }
            // The job is disposed even if termination failed; its handle cannot outlive the parent.
            foreach (string file in new[] { requestPath, requestPath + ".tmp", resultPath, resultPath + ".tmp" })
                try { File.Delete(file); } catch { }
            try { Directory.Delete(folder); } catch { }
        }
    }
    public void Dispose()
    {
        lock (stateLock) { disposed = true; activeCancellation?.Cancel(); }
    }
}

internal static class WorkerJob
{
    internal static SafeFileHandle Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        { int error = Marshal.GetLastWin32Error(); job.Dispose(); throw new System.ComponentModel.Win32Exception(error); }
        return job;
    }
    internal static void Assign(SafeFileHandle job, Process process)
    {
        if (!AssignProcessToJobObject(job, process.Handle)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
