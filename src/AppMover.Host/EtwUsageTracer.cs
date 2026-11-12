using System.Collections.Concurrent;
using System.Diagnostics;
using AppMover.Core.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;

namespace AppMover.Host;

/// <summary>
/// Kernel ETW session (process, file and registry events) following the processes of an application: those whose
/// command line is in its install folders, those started by AppMover, and their children. Requires administrator rights.
/// </summary>
public sealed class EtwUsageTracer(ILogger logger) : IUsageTracer
{
    public IUsageTraceSession Start(string appId, IReadOnlyList<string> installRoots, string? launch) =>
        new Session(appId, installRoots, launch, logger);

    private sealed class Session : IUsageTraceSession
    {
        private readonly string _appId;
        private readonly IReadOnlyList<string> _roots;
        private readonly ILogger _logger;
        private readonly DateTime _started = DateTime.UtcNow;
        private readonly TraceEventSession _session;
        private readonly Task _processing;
        private readonly ConcurrentDictionary<int, string> _processes = new();
        private readonly ConcurrentDictionary<string, byte> _written = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _read = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _registry = new(StringComparer.OrdinalIgnoreCase);
        private UsageTrace? _result;

        public Session(string appId, IReadOnlyList<string> installRoots, string? launch, ILogger logger)
        {
            _appId = appId;
            _roots = installRoots;
            _logger = logger;

            _session = new TraceEventSession(KernelTraceEventParser.KernelSessionName) { StopOnDispose = true };
            _session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.FileIOInit |
                KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.Registry);

            var kernel = _session.Source.Kernel;
            kernel.ProcessStart += OnProcess;
            kernel.ProcessDCStart += OnProcess; // processes already running when the trace starts
            kernel.FileIOCreate += e =>
            {
                if (_processes.ContainsKey(e.ProcessID) && !string.IsNullOrEmpty(e.FileName))
                    (e.CreateDisposition == CreateDisposition.OPEN_EXISTING ? _read : _written).TryAdd(e.FileName, 0);
            };
            kernel.FileIOWrite += e =>
            {
                if (_processes.ContainsKey(e.ProcessID) && !string.IsNullOrEmpty(e.FileName))
                    _written.TryAdd(e.FileName, 0);
            };
            kernel.RegistryCreate += OnRegistryWrite;
            kernel.RegistrySetValue += OnRegistryWrite;

            _processing = Task.Run(() => _session.Source.Process());

            if (launch is not null)
            {
                var process = Process.Start(new ProcessStartInfo(launch) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(launch) });
                if (process is not null)
                    _processes[process.Id] = launch;
            }
        }

        private void OnProcess(ProcessTraceData e)
        {
            if (_processes.ContainsKey(e.ParentID)
                || _roots.Any(root => e.CommandLine?.Contains(root, StringComparison.OrdinalIgnoreCase) == true))
                _processes[e.ProcessID] = e.CommandLine ?? e.ImageFileName;
        }

        private void OnRegistryWrite(RegistryTraceData e)
        {
            if (_processes.ContainsKey(e.ProcessID) && e.KeyName.StartsWith(@"\REGISTRY\", StringComparison.OrdinalIgnoreCase))
                _registry.TryAdd(e.KeyName, 0);
        }

        public UsageTrace Stop()
        {
            if (_result is not null)
                return _result;
            _session.Dispose();
            if (!_processing.Wait(TimeSpan.FromSeconds(10)))
                _logger.LogWarning("The trace session did not stop in time");

            _result = new UsageTrace
            {
                AppId = _appId,
                StartedUtc = _started,
                StoppedUtc = DateTime.UtcNow,
                Processes = _processes.Values.Distinct().ToList(),
                FilesWritten = _written.Keys.Order().ToList(),
                FilesRead = _read.Keys.Where(f => !_written.ContainsKey(f)).Order().ToList(),
                RegistryKeysWritten = _registry.Keys.Order().ToList(),
            };
            _logger.LogInformation("Trace of {App}: {Processes} process(es), {Written} file(s) written, {Read} read, {Keys} key(s) written",
                _appId, _result.Processes.Count, _result.FilesWritten.Count, _result.FilesRead.Count, _result.RegistryKeysWritten.Count);
            return _result;
        }

        public void Dispose() => Stop();
    }
}
