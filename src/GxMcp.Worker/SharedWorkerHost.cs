using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker
{
    /// <summary>
    /// Broker mode for one normal Worker child. The broker owns the child stdio and
    /// exposes one bounded named-pipe attachment per Gateway. It never touches the
    /// GeneXus SDK; all SDK work remains serialized by the child Worker.
    /// </summary>
    internal static class SharedWorkerHost
    {
        internal static StreamWriter CreateChildStdinWriter(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            return new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024)
            {
                AutoFlush = false,
                NewLine = "\n"
            };
        }

        internal static bool IsRequested(string[] args)
        {
            if (args == null) return false;
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], "--shared-host", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static int Run(string[] args)
        {
            try
            {
                var options = SharedWorkerHostOptions.Parse(args);
                using (var runtime = new SharedWorkerHostRuntime(options))
                    return runtime.Run();
            }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine("[SharedWorkerHost] startup failed: " + ex); } catch { }
                return 1;
            }
        }
    }

    internal sealed class SharedWorkerHostOptions
    {
        public string IdentityKey { get; private set; }
        public string PipeName { get; private set; }
        public string KbPath { get; private set; }
        public string WorkerExecutable { get; private set; }
        public string InstallationPath { get; private set; }
        public string Driver { get; private set; }
        public string Major { get; private set; }
        public int IdleTimeoutMs { get; private set; }
        public int AttachTimeoutMs { get; private set; }

        internal static SharedWorkerHostOptions Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < (args ?? new string[0]).Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, "--shared-host", StringComparison.OrdinalIgnoreCase)) continue;
                if (!arg.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) continue;
                values[arg.Substring(2)] = args[++i];
            }

            string executable = Get(values, "worker-executable");
            if (string.IsNullOrWhiteSpace(executable))
                executable = Process.GetCurrentProcess().MainModule.FileName;
            string kb = Get(values, "kb");
            string install = Get(values, "installation");
            string driver = Get(values, "driver");
            string major = Get(values, "major");
            string key = Get(values, "identity-key");
            if (string.IsNullOrWhiteSpace(key))
            {
                if (!SharedWorkerHostProtocol.TryCreateIdentityKey(executable, kb, install, driver, major, out key, out string identityError))
                    throw new InvalidDataException(identityError);
            }
            if (!SharedWorkerHostProtocol.IsIdentityKey(key)) throw new InvalidDataException("Invalid shared Worker identity key.");
            string pipe = Get(values, "pipe-name");
            if (string.IsNullOrWhiteSpace(pipe))
            {
                if (!SharedWorkerHostProtocol.TryBuildPipeName(key, out pipe, out string pipeError))
                    throw new InvalidDataException(pipeError);
            }
            if (!SharedWorkerHostProtocol.IsValidPipeName(pipe)) throw new InvalidDataException("Invalid shared Worker pipe name.");
            if (string.IsNullOrWhiteSpace(kb) || string.IsNullOrWhiteSpace(install) || string.IsNullOrWhiteSpace(driver) || string.IsNullOrWhiteSpace(major))
                throw new InvalidDataException("Shared Worker host requires --kb, --installation, --driver, and --major.");

            return new SharedWorkerHostOptions
            {
                IdentityKey = key,
                PipeName = pipe,
                KbPath = kb,
                WorkerExecutable = executable,
                InstallationPath = install,
                Driver = driver,
                Major = major,
                IdleTimeoutMs = ParsePositive(values, "idle-ms", 120000),
                AttachTimeoutMs = ParsePositive(values, "attach-timeout-ms", 30000)
            };
        }

        private static string Get(IDictionary<string, string> values, string key)
            => values.TryGetValue(key, out string value) ? value : string.Empty;

        private static int ParsePositive(IDictionary<string, string> values, string key, int fallback)
        {
            if (values.TryGetValue(key, out string value) && int.TryParse(value, out int parsed) && parsed > 0 && parsed <= 86400000)
                return parsed;
            return fallback;
        }
    }

    internal sealed class SharedWorkerHostRuntime : IDisposable
    {
        private const int QueueCapacity = 256;
        private readonly SharedWorkerHostOptions _options;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly ConcurrentDictionary<string, SharedWorkerHostAttachment> _attachments = new ConcurrentDictionary<string, SharedWorkerHostAttachment>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, SharedWorkerRequestRoute> _routes = new ConcurrentDictionary<string, SharedWorkerRequestRoute>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, SharedWorkerRequestRoute> _progressOwners = new ConcurrentDictionary<string, SharedWorkerRequestRoute>(StringComparer.Ordinal);
        private readonly object _recordGate = new object();
        private readonly object _childGate = new object();
        private readonly Queue<DateTime> _respawnHistory = new Queue<DateTime>();
        private Process _child;
        private StreamWriter _childStdin;
        private Thread _childWriter;
        private Thread _childReader;
        private Thread _childErrorReader;
        private Thread _acceptThread;
        private string _lastChildError = string.Empty;
        private string _lastChildFailureDiagnostic = string.Empty;
        private DateTime _lastDetachUtc = DateTime.UtcNow;
        private long _lastBackgroundActivityTicks = DateTime.UtcNow.Ticks;
        private long _generation = 1;

        // Issue #335: broker-owned recycle election and the generation each attachment
        // observes. Recycle decisions belong here - the broker owns the child - so that
        // two Gateways watching the same Worker cannot each decide to kill it. The
        // election flag is cleared when the new child starts, which is what makes exactly
        // one attachment act per sweep.
        private long _brokerGeneration = 1;
        private int _recycleElected;
        private bool _sdkReady;
        private string _recordPath;
        private SharedWorkerHostRegistryRecord _record;
        private int _disposed;

        internal SharedWorkerHostRuntime(SharedWorkerHostOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _recordPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GxMcp", "shared-workers", _options.IdentityKey + ".json");
        }

        internal int Run()
        {
            StartChild();
            StartPipeAcceptLoop();
            while (!_stop.IsCancellationRequested)
            {
                if (_child == null || _child.HasExited)
                {
                    if (!TryRespawnChild()) break;
                    continue;
                }
                DateTime lastBackgroundActivity = new DateTime(Interlocked.Read(ref _lastBackgroundActivityTicks), DateTimeKind.Utc);
                if (_attachments.IsEmpty
                    && (DateTime.UtcNow - _lastDetachUtc).TotalMilliseconds >= _options.IdleTimeoutMs
                    && (DateTime.UtcNow - lastBackgroundActivity).TotalMilliseconds >= _options.IdleTimeoutMs)
                    break;
                WriteRecord();
                Thread.Sleep(250);
            }
            return 0;
        }

        private void StartChild()
        {
            Volatile.Write(ref _lastChildError, string.Empty);
            var start = new ProcessStartInfo
            {
                FileName = _options.WorkerExecutable,
                Arguments = "--kb " + Quote(_options.KbPath) + " --driver " + Quote(_options.Driver) + " --major " + Quote(_options.Major),
                WorkingDirectory = Path.GetDirectoryName(_options.WorkerExecutable) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.EnvironmentVariables["GX_PROGRAM_DIR"] = _options.InstallationPath;
            start.EnvironmentVariables["GX_KB_PATH"] = _options.KbPath;
            start.EnvironmentVariables["GXMCP_DRIVER"] = _options.Driver;
            start.EnvironmentVariables["GXMCP_TARGET_MAJOR"] = _options.Major;
            string legacyProvider = Environment.GetEnvironmentVariable("GXMCP_GXPUBLIC_PROVIDER");
            if (!string.IsNullOrWhiteSpace(legacyProvider))
                start.EnvironmentVariables["GXMCP_GXPUBLIC_PROVIDER"] = legacyProvider;
            start.EnvironmentVariables["GXMCP_SHARED_CHILD"] = "1";
            start.EnvironmentVariables.Remove("GX_MCP_PIPE");

            var child = new Process { StartInfo = start, EnableRaisingEvents = true };
            if (!child.Start()) throw new InvalidOperationException("Could not start shared Worker child.");
            _child = child;
            // Issue #335: a new child starts a new supervision generation and ends any
            // outstanding recycle election. Clearing the election is what makes the sweep
            // single-shot: without it, every attachment would keep seeing "recycle
            // elected" and could each act on it.
            Interlocked.Increment(ref _brokerGeneration);
            Volatile.Write(ref _recycleElected, 0);
            var childStdin = SharedWorkerHost.CreateChildStdinWriter(child.StandardInput.BaseStream);
            _childStdin = childStdin;
            _record = new SharedWorkerHostRegistryRecord
            {
                Identity = new SharedWorkerHostIdentity
                {
                    WorkerExecutable = Normalize(_options.WorkerExecutable),
                    KbPath = Normalize(_options.KbPath),
                    InstallationPath = Normalize(_options.InstallationPath),
                    Driver = (_options.Driver ?? string.Empty).Trim().ToLowerInvariant(),
                    Major = (_options.Major ?? string.Empty).Trim().ToLowerInvariant(),
                    Key = _options.IdentityKey
                },
                HostPid = Process.GetCurrentProcess().Id,
                HostStartTimeUtcTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                WorkerPid = child.Id,
                WorkerStartTimeUtcTicks = child.StartTime.ToUniversalTime().Ticks,
                PipeName = _options.PipeName,
                Generation = _generation,
                UpdatedUtc = DateTime.UtcNow,
                State = "starting"
            };
            WriteRecord();

            _childWriter = new Thread(() => ChildWriterLoop(childStdin)) { IsBackground = true, Name = "SharedWorkerChildWriter" };
            _childReader = new Thread(() => ChildReaderLoop(child)) { IsBackground = true, Name = "SharedWorkerChildReader" };
            _childErrorReader = new Thread(() => ChildErrorReaderLoop(child)) { IsBackground = true, Name = "SharedWorkerChildErrorReader" };
            _childWriter.Start();
            _childReader.Start();
            _childErrorReader.Start();
        }

        private void StartPipeAcceptLoop()
        {
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "SharedWorkerPipeAccept" };
            _acceptThread.Start();
        }

        private void AcceptLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        _options.PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        64 * 1024,
                        64 * 1024);
                    server.WaitForConnection();
                    if (_stop.IsCancellationRequested) { server.Dispose(); break; }
                    var attachment = new SharedWorkerHostAttachment(this, server, _options.AttachTimeoutMs);
                    ThreadPool.QueueUserWorkItem(_ => _ = attachment.RunAsync());
                    server = null;
                }
                catch (Exception ex)
                {
                    try { server?.Dispose(); } catch { }
                    HostLog("pipe accept failed: " + ex.Message);
                    if (!_stop.IsCancellationRequested) Thread.Sleep(250);
                }
            }
        }

        internal bool Attach(SharedWorkerHostAttachment attachment, SharedWorkerEnvelope request, out string error)
        {
            error = string.Empty;
            if (!SharedWorkerHostProtocol.TryValidateAttach(request, _options.IdentityKey, out error)) return false;
            attachment.AttachmentId = Guid.NewGuid().ToString("N");
            if (!_attachments.TryAdd(attachment.ConnectionId, attachment))
            {
                error = "could not register attachment";
                return false;
            }
            _lastDetachUtc = DateTime.UtcNow;
            SendHostFrame(attachment, "attach_ack", new JObject
            {
                ["attachId"] = attachment.AttachmentId,
                ["attachmentId"] = attachment.AttachmentId,
                ["hostPid"] = _record.HostPid,
                ["hostStartTimeUtcTicks"] = _record.HostStartTimeUtcTicks,
                ["workerPid"] = _record.WorkerPid,
                ["workerStartTimeUtcTicks"] = _record.WorkerStartTimeUtcTicks,
                ["generation"] = _record.Generation,
                ["ready"] = _sdkReady
            });
            return true;
        }

        internal void Detach(SharedWorkerHostAttachment attachment)
        {
            if (attachment == null) return;
            _attachments.TryRemove(attachment.ConnectionId, out _);
            RemoveRoutesForAttachment(attachment.AttachmentId);
            _lastDetachUtc = DateTime.UtcNow;
        }

        internal void Receive(SharedWorkerHostAttachment attachment, string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (SharedWorkerHostProtocol.TryParseEnvelope(line, out SharedWorkerEnvelope envelope, out _))
            {
                if (envelope.Type == "heartbeat")
                {
                    if (!SharedWorkerHostProtocol.TryValidateSessionEnvelope(
                        envelope, true, _options.IdentityKey, attachment.AttachmentId, out string sessionError))
                    {
                        SendHostError(attachment, sessionError);
                        return;
                    }
                    // Issue #335: the heartbeat_ack now carries the broker's own view of the child.
                    //
                    // In shared mode the Gateway does not own the child Process, so it
                    // cannot ask the OS anything: no PID, no working set, no way to tell a
                    // progressing SDK call from a deadlocked one. That left a shared Worker
                    // with no heap or stall supervision at all - the exact gap that made
                    // these checks worth having for an isolated Worker.
                    //
                    // The broker is the only party that can answer them, so it does, here -
                    // on a frame that already exists. Extending heartbeat_ack rather than
                    // adding a frame type keeps this off the protocol-migration path
                    // entirely: an older Gateway ignores the new fields, and an older
                    // broker's ack still satisfies the newer Gateway's reader.
                    SendHostFrame(attachment, "heartbeat_ack", BuildHeartbeatAck(attachment.AttachmentId));
                }
                else if (envelope.Type == "detach")
                {
                    if (!SharedWorkerHostProtocol.TryValidateSessionEnvelope(
                        envelope, true, _options.IdentityKey, attachment.AttachmentId, out string sessionError))
                    {
                        SendHostError(attachment, sessionError);
                        return;
                    }
                    attachment.Stop();
                }
                else
                {
                    SendHostError(attachment, "unexpected host frame after attach");
                }
                return;
            }

            JObject request;
            try { request = GxMcp.Common.JsonIngress.ParseObject(line); }
            catch { SendHostError(attachment, "malformed JSON-RPC frame"); return; }
            if (!string.Equals(request.Value<string>("jsonrpc"), "2.0", StringComparison.Ordinal))
            {
                SendHostError(attachment, "expected JSON-RPC 2.0 frame");
                return;
            }

            if (string.Equals(request.Value<string>("method"), "notifications/cancelled", StringComparison.Ordinal))
            {
                if (SharedWorkerHostProtocol.TryRewriteCancellationForChild(
                    request, _routes.Values, attachment.AttachmentId, out JObject childCancellation, out _))
                    attachment.EnqueueChild(childCancellation.ToString(Formatting.None));
                return;
            }

            lock (_childGate)
            {
                if (!SharedWorkerHostProtocol.TryRewriteRequestForChild(request, attachment.AttachmentId, attachment.NextSequence(), out JObject childRequest, out SharedWorkerRequestRoute route, out string error))
                {
                    SendHostError(attachment, error);
                    return;
                }
                _routes[route.ChildRequestId] = route;
                if (!string.IsNullOrWhiteSpace(route.ChildProgressToken)) _progressOwners[route.ChildProgressToken] = route;
                string payload = childRequest.ToString(Formatting.None);
                if (!attachment.EnqueueChild(payload))
                {
                    _routes.TryRemove(route.ChildRequestId, out _);
                    if (!string.IsNullOrWhiteSpace(route.ChildProgressToken)) _progressOwners.TryRemove(route.ChildProgressToken, out _);
                    attachment.Enqueue(new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = route.ClientRequestId.DeepClone(),
                        ["error"] = new JObject
                        {
                            ["code"] = -32029,
                            ["message"] = "Shared Worker command queue is full; retry this operation.",
                            ["data"] = new JObject { ["code"] = "SHARED_WORKER_BACKPRESSURE", ["retryable"] = true }
                        }
                    }.ToString(Formatting.None));
                }
            }
        }

        private bool TryRespawnChild()
        {
            if (_stop.IsCancellationRequested) return false;
            lock (_childGate)
            {
                if (_stop.IsCancellationRequested) return false;
                while (_respawnHistory.Count > 0 && (DateTime.UtcNow - _respawnHistory.Peek()).TotalMinutes >= 1)
                    _respawnHistory.Dequeue();
                if (_respawnHistory.Count >= 3)
                {
                    BroadcastHostError("Shared Worker child crashed repeatedly; host stopped fail-closed.");
                    return false;
                }
                _respawnHistory.Enqueue(DateTime.UtcNow);
                string failureDiagnostic = BuildChildFailureDiagnostic(_child);
                _lastChildFailureDiagnostic = failureDiagnostic;
                FailRoutesForRespawn();
                try { _childStdin?.Dispose(); } catch { }
                _childStdin = null;
                try { _child?.Dispose(); } catch { }
                _sdkReady = false;
                _generation++;
                lock (_recordGate) { if (_record != null) _record.State = "recovering"; }
                WriteRecord();
                try
                {
                    Thread.Sleep(250);
                    StartChild();
                    BroadcastWorkerRestarted(failureDiagnostic);
                    BroadcastJson(new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["method"] = "notifications/worker/restarting",
                        ["params"] = new JObject { ["generation"] = _generation }
                    }.ToString(Formatting.None));
                    return true;
                }
                catch (Exception ex)
                {
                    BroadcastHostError("Shared Worker child respawn failed after " + failureDiagnostic + ": " + ex.Message);
                    return false;
                }
            }
        }

        private void BroadcastWorkerRestarted(string failureDiagnostic)
        {
            foreach (var attachment in _attachments.Values)
            {
                SendHostFrame(attachment, "worker_restarted", new JObject
                {
                    ["hostPid"] = _record.HostPid,
                    ["hostStartTimeUtcTicks"] = _record.HostStartTimeUtcTicks,
                    ["workerPid"] = _record.WorkerPid,
                    ["workerStartTimeUtcTicks"] = _record.WorkerStartTimeUtcTicks,
                    ["generation"] = _record.Generation,
                    ["ready"] = false,
                    ["failureStage"] = "child_exit",
                    ["failureDiagnostic"] = failureDiagnostic
                });
            }
        }

        private void FailRoutesForRespawn()
        {
            foreach (var entry in _routes)
            {
                if (_routes.TryRemove(entry.Key, out SharedWorkerRequestRoute route))
                {
                    if (!string.IsNullOrWhiteSpace(route.ChildProgressToken)) _progressOwners.TryRemove(route.ChildProgressToken, out _);
                    var error = new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = route.ClientRequestId.DeepClone(),
                        ["error"] = new JObject
                        {
                            ["code"] = -32050,
                            ["message"] = "Shared Worker restarted while this request was running.",
                            ["data"] = new JObject { ["code"] = "SHARED_WORKER_RESTARTED", ["retryable"] = true, ["generation"] = _generation }
                        }
                    };
                    SendJson(_attachments, route.AttachmentId, error.ToString(Formatting.None));
                }
            }
            foreach (var attachment in _attachments.Values)
                attachment.ClearChildQueue();
        }

        private void ChildWriterLoop(StreamWriter childStdin)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    bool wrote = false;
                    foreach (SharedWorkerHostAttachment attachment in _attachments.Values)
                    {
                        if (!attachment.TryDequeueChild(out string line)) continue;
                        wrote = true;
                        if (_stop.IsCancellationRequested) break;
                        childStdin.WriteLine(line);
                        childStdin.Flush();
                    }
                    if (wrote) continue;
                    Thread.Sleep(5);
                }
            }
            catch (Exception ex) { HostLog("child stdin closed: " + ex.Message); }
        }

        private void ChildReaderLoop(Process child)
        {
            try
            {
                string line;
                while (!_stop.IsCancellationRequested && (line = child.StandardOutput.ReadLine()) != null)
                {
                    long frameBytes = Encoding.UTF8.GetByteCount(line);
                    if (frameBytes > SharedWorkerHostProtocol.MaxFrameBytes)
                    {
                        // Issue #347. This used to broadcast a host error and cancel the
                        // broker, so one client's legitimately large read, batch or
                        // persisted-content result took down every other attachment. A
                        // large frame that parses is a valid response that does not fit
                        // the transport, and the answer is a bounded per-request error -
                        // not a shared outage.
                        //
                        // Malformed and abusive frames still stop the host, unchanged: the
                        // oversized path must not become a way past the fail-closed check,
                        // so an unparseable frame is still fatal here.
                        var disposition = SharedWorkerHostProtocol.ClassifyOversizedFrame(
                            line, out string oversizedRequestId, frameBytes);

                        if (disposition == SharedWorkerHostProtocol.OversizedFrameDisposition.FailClosed)
                        {
                            BroadcastHostError(
                                frameBytes > SharedWorkerHostProtocol.HardFrameCeilingBytes
                                    ? "Shared Worker child emitted a frame beyond the hard ceiling (" + frameBytes
                                      + " bytes); host stopped fail-closed."
                                    : "Shared Worker child emitted an unparseable oversized frame; host stopped fail-closed.");
                            _stop.Cancel();
                            break;
                        }

                        if (disposition == SharedWorkerHostProtocol.OversizedFrameDisposition.RejectRequest)
                        {
                            var refusal = SharedWorkerHostProtocol.BuildOversizedResponse(
                                oversizedRequestId, frameBytes, SharedWorkerHostProtocol.MaxFrameBytes);
                            // Route it exactly as a normal response would be routed, to the
                            // attachment that owns the request, and nowhere else.
                            if (_routes.TryRemove(oversizedRequestId, out SharedWorkerRequestRoute oversizedRoute))
                            {
                                if (!string.IsNullOrWhiteSpace(oversizedRoute.ChildProgressToken))
                                    _progressOwners.TryRemove(oversizedRoute.ChildProgressToken, out _);
                                var clientFrame = new JObject
                                {
                                    ["jsonrpc"] = "2.0",
                                    ["id"] = oversizedRoute.ClientRequestId ?? JValue.CreateNull(),
                                    ["error"] = refusal["error"]
                                };
                                SendJson(_attachments, oversizedRoute.AttachmentId, clientFrame.ToString(Formatting.None));
                            }
                            else
                            {
                                // No live route: the request already completed, timed out or
                                // detached. Log it rather than guessing an owner.
                                HostLog("dropped oversized response for unrouted request id=" + oversizedRequestId);
                            }
                        }
                        else
                        {
                            HostLog("dropped oversized child notification (" + frameBytes + " bytes); no requester to answer");
                        }

                        // The host keeps serving every other attachment.
                        continue;
                    }
                    JObject frame;
                    try { frame = GxMcp.Common.JsonIngress.ParseObject(line); }
                    catch
                    {
                        // The normal Worker emits two plain-text handshake lines before
                        // switching to newline-delimited JSON-RPC. They are part of the
                        // existing stdio contract, not malformed broker frames.
                        if (line.StartsWith("WORKER_HANDSHAKE_", StringComparison.Ordinal)) continue;
                        BroadcastHostError("Shared Worker child emitted malformed JSON; host stopped fail-closed.");
                        _stop.Cancel();
                        break;
                    }
                    string method = frame.Value<string>("method") ?? string.Empty;
                    if (string.Equals(method, "notifications/worker/sdk_ready", StringComparison.Ordinal))
                    {
                        _sdkReady = true;
                        lock (_recordGate) { _record.State = "ready"; }
                        WriteRecord();
                    }
                    if (string.Equals(method, "notifications/worker/build_active", StringComparison.Ordinal)
                        || string.Equals(method, "notifications/worker/index_active", StringComparison.Ordinal))
                    {
                        Interlocked.Exchange(ref _lastBackgroundActivityTicks, DateTime.UtcNow.Ticks);
                    }
                    if (frame["id"] != null && frame["id"].Type != JTokenType.Null)
                    {
                        string childId = frame["id"].ToString();
                        if (_routes.TryRemove(childId, out SharedWorkerRequestRoute route))
                        {
                            if (!string.IsNullOrWhiteSpace(route.ChildProgressToken)) _progressOwners.TryRemove(route.ChildProgressToken, out _);
                            if (SharedWorkerHostProtocol.TryRestoreResponse(frame, route, out JObject restored, out _))
                                SendJson(_attachments, route.AttachmentId, restored.ToString(Formatting.None));
                        }
                        continue;
                    }
                    if (SharedWorkerHostProtocol.TryRouteChildNotification(
                        frame, _progressOwners, out string attachmentId, out JObject routed, out bool broadcast, out string routeError))
                    {
                        if (broadcast)
                            BroadcastJson(routed.ToString(Formatting.None));
                        else if (!string.IsNullOrWhiteSpace(attachmentId))
                            SendJson(_attachments, attachmentId, routed.ToString(Formatting.None));
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(method))
                        HostLog("dropping unroutable child notification method=" + method + " reason=" + routeError);
                }
            }
            catch (Exception ex) { HostLog("child stdout closed: " + ex.Message); }
            finally
            {
                if (!_stop.IsCancellationRequested)
                    HostLog("shared Worker child stdout ended; respawn monitor will recover it");
            }
        }

        private void ChildErrorReaderLoop(Process child)
        {
            try
            {
                string line;
                while (!_stop.IsCancellationRequested && (line = child.StandardError.ReadLine()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        Volatile.Write(ref _lastChildError, RedactDiagnostic(line));
                    HostLog("child stderr: " + line);
                }
            }
            catch { }
        }

        private void SendHostError(SharedWorkerHostAttachment attachment, string message)
            => SendHostFrame(attachment, "host_error", new JObject { ["message"] = message });

        private void SendHostFrame(SharedWorkerHostAttachment attachment, string type, JObject data)
        {
            data = data ?? new JObject();
            data["type"] = type;
            data["protocolVersion"] = SharedWorkerHostProtocol.ProtocolVersion;
            data["identityKey"] = _options.IdentityKey;
            data["gxmcp"] = type;
            data["version"] = SharedWorkerHostProtocol.ProtocolVersion;
            data["key"] = _options.IdentityKey;
            attachment.Enqueue(data.ToString(Formatting.None));
        }

        private static void SendJson(ConcurrentDictionary<string, SharedWorkerHostAttachment> attachments, string attachmentId, string json)
        {
            foreach (var attachment in attachments.Values)
                if (string.Equals(attachment.AttachmentId, attachmentId, StringComparison.Ordinal)) { attachment.Enqueue(json); return; }
        }

        private void BroadcastJson(string json)
        {
            foreach (var attachment in _attachments.Values) attachment.Enqueue(json);
        }

        /// <summary>
        /// How long a busy SDK lane may go without reporting movement before the broker
        /// calls it stalled. Issue #335. Matches the connection-recover window so the two
        /// paths cannot disagree about the same Worker.
        /// </summary>
        internal const int SdkStallAfterMs = 90_000;

        /// <summary>
        /// Issue #371. The grace period for a busy lane that has never reported progress,
        /// matching the Gateway's window so the two cannot disagree about the same child.
        /// Only operations that emit progress notifications prove they can move; a single
        /// object read, a save, or a COM call blocked behind a modal dialog emits none and
        /// would otherwise read as busy-unproven forever.
        /// </summary>
        internal const int NoProgressStallAfterMsDefault = 600_000;

        internal static readonly int NoProgressStallAfterMs = ResolveNoProgressStallAfterMs();

        private const string NoProgressStallEnvVar = "GXMCP_SDK_NO_PROGRESS_STALL_MS";

        private static int ResolveNoProgressStallAfterMs()
        {
            var raw = Environment.GetEnvironmentVariable(NoProgressStallEnvVar);
            if (int.TryParse(raw, out int configured) && configured > 0) return configured;
            return NoProgressStallAfterMsDefault;
        }

        /// <summary>
        /// Classifies the SDK lane. Issue #335.
        ///
        /// <para>
        /// The distinction that matters is progressing versus stalled. A lane that is busy
        /// and still reporting movement is legitimate long work, and treating it as a
        /// fault recycles a Worker in the middle of an operation - which destroys the
        /// operation, not just the Worker. A lane that has gone quiet past the window has
        /// not moved, and waiting longer only extends a dead Worker into an indefinite one.
        /// </para>
        ///
        /// <para>
        /// A busy lane with no observed progress is <c>busy-unproven</c>, not
        /// <c>busy-stalled</c>: one sample cannot tell a healthy short call from a
        /// deadlocked one, and guessing "stalled" would kill a Worker three seconds into
        /// ordinary work. Past <see cref="NoProgressStallAfterMs"/> of unproven progress it
        /// becomes <c>busy-stalled-unproven</c>, a recovery candidate - issue #371.
        /// </para>
        /// </summary>
        internal static string ClassifySdkState(bool active, bool sawProgress, long lastProgressMs, long elapsedMs = 0)
        {
            if (!active) return "idle";
            if (!sawProgress || lastProgressMs < 0)
                return elapsedMs >= NoProgressStallAfterMs ? "busy-stalled-unproven" : "busy-unproven";
            return lastProgressMs >= SdkStallAfterMs ? "busy-stalled" : "busy-progressing";
        }

        /// <summary>Why <see cref="ClassifySdkState"/> reached its verdict, or null.</summary>
        internal static string SdkStallReason(string sdkState)
            => sdkState == "busy-stalled-unproven" ? "no-progress-ceiling"
                : sdkState == "busy-stalled" ? "progress-stopped"
                : null;

        /// <summary>
        /// Builds the heartbeat acknowledgement, including the broker's view of the child.
        ///
        /// <para>
        /// Issue #335. Everything here is the broker's own observation - it owns the child
        /// process and the SDK lane, and a shared Gateway owns neither. The SDK section is
        /// the part that matters most: <c>progressing</c> and <c>stalled</c> are
        /// deliberately different answers, because treating a slow-but-progressing build as
        /// a fault recycles a Worker mid-operation and loses the work.
        /// </para>
        ///
        /// <para>
        /// Deliberately no exception can escape: a heartbeat that throws would take down
        /// the attachment loop, and a diagnostic that cannot be gathered must degrade to
        /// "unknown" rather than to a lost connection.
        /// </para>
        /// </summary>
        private JObject BuildHeartbeatAck(string attachmentId)
        {
            var ack = new JObject
            {
                ["attachmentId"] = attachmentId,
                ["utc"] = DateTime.UtcNow.ToString("O")
            };

            try
            {
                var child = _child;
                var pid = child?.Id ?? 0;
                long workingSet = 0, privateBytes = 0, availableBefore = 0, availableAfter = 0;

                if (child != null && !child.HasExited)
                {
                    try
                    {
                        child.Refresh();
                        workingSet = child.WorkingSet64;
                        privateBytes = child.PrivateMemorySize64;
                    }
                    catch
                    {
                        // A process that exited between the check and the read. Reported
                        // as zero rather than guessed; the exit itself is what matters.
                    }
                    try
                    {
                        availableBefore = GC.GetTotalMemory(false);
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                        availableAfter = GC.GetTotalMemory(false);
                    }
                    catch { }
                }

                // The SDK-lane section reuses the worker's own status projection rather than
                // duplicating the busy/progress bookkeeping here - it is the same
                // distinction the connection-recover path uses, so a shared Worker and an
                // isolated one report "progressing" and "stalled" identically.
                var sdk = Program.GetSdkBusyStatus();
                bool sdkActive = sdk["active"]?.ToObject<bool?>() ?? false;
                bool sawProgress = sdk["sawProgress"]?.ToObject<bool?>() ?? false;
                long lastProgressMs = sdk["lastProgressMs"]?.ToObject<long?>() ?? -1;
                long sdkElapsedMs = sdk["elapsedMs"]?.ToObject<long?>() ?? 0;
                string sdkState = ClassifySdkState(sdkActive, sawProgress, lastProgressMs, sdkElapsedMs);
                var q = sdk["queueDepths"] as JObject;

                ack["supervision"] = new JObject
                {
                    ["available"] = true,
                    // Identity the Gateway cannot otherwise obtain.
                    ["childPid"] = pid,
                    ["childAlive"] = child != null && !child.HasExited,
                    ["attachments"] = _attachments.Count,
                    // Memory, in the same units the isolated path reports.
                    ["workingSetBytes"] = workingSet,
                    ["privateBytes"] = privateBytes,
                    ["managedHeapBeforeBytes"] = availableBefore,
                    ["managedHeapAfterBytes"] = availableAfter,
                    // Queue and SDK activity.
                    ["queueTotal"] = q?["total"]?.ToObject<int?>() ?? 0,
                    ["queueP0"] = q?["p0"]?.ToObject<int?>() ?? 0,
                    ["queueP1"] = q?["p1"]?.ToObject<int?>() ?? 0,
                    ["queueP2"] = q?["p2"]?.ToObject<int?>() ?? 0,
                    ["sdkBusy"] = sdkActive,
                    ["sdkOperation"] = sdk["operation"]?.ToString(),
                    ["sdkElapsedMs"] = sdkElapsedMs,
                    ["sdkSawProgress"] = sawProgress,
                    // -1 means "no progress has ever been observed for this operation",
                    // which is a different answer from 0 ("progress just now").
                    ["sdkLastProgressMs"] = lastProgressMs,
                    // The classification, computed where the evidence is. A Gateway
                    // receiving this does not have to reimplement the distinction, and
                    // cannot disagree with the broker about which child it describes.
                    // Issue #371: elapsed time is part of the input, so a lane that never
                    // emits progress can still be recognised as stuck.
                    ["sdkState"] = sdkState,
                    ["sdkStallReason"] = SdkStallReason(sdkState),
                    ["sdkNoProgressStallAfterMs"] = NoProgressStallAfterMs,
                    // Generation, so every attachment can tell it is looking at the same
                    // child. A Gateway must never unilaterally recycle another's child;
                    // this is the value they compare before acting.
                    ["brokerGeneration"] = Interlocked.Read(ref _brokerGeneration),
                    // The broker decides recycling. A Gateway may *ask*; only this flag
                    // says the broker has already elected a victim, and only one
                    // attachment ever sees it true for a given sweep.
                    ["recycleElected"] = Volatile.Read(ref _recycleElected) != 0,
                    ["capturedAtUtc"] = DateTime.UtcNow.ToString("O")
                };
            }
            catch (Exception ex)
            {
                ack["supervision"] = new JObject
                {
                    ["available"] = false,
                    ["error"] = ex.GetType().Name
                };
            }

            return ack;
        }

        private void BroadcastHostError(string message)
        {
            foreach (var attachment in _attachments.Values)
                SendHostError(attachment, message);
        }

        private void RemoveRoutesForAttachment(string attachmentId)
        {
            foreach (var entry in _routes)
                if (string.Equals(entry.Value.AttachmentId, attachmentId, StringComparison.Ordinal))
                    _routes.TryRemove(entry.Key, out _);
            foreach (var entry in _progressOwners)
                if (string.Equals(entry.Value.AttachmentId, attachmentId, StringComparison.Ordinal))
                    _progressOwners.TryRemove(entry.Key, out _);
        }

        private void WriteRecord()
        {
            if (_record == null) return;
            lock (_recordGate)
            {
                _record.UpdatedUtc = DateTime.UtcNow;
                string directory = Path.GetDirectoryName(_recordPath);
                Directory.CreateDirectory(directory);
                string temp = _recordPath + ".tmp-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllText(temp, JsonConvert.SerializeObject(_record, Formatting.None), new UTF8Encoding(false));
                    if (File.Exists(_recordPath)) File.Replace(temp, _recordPath, null);
                    else File.Move(temp, _recordPath);
                }
                finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            foreach (var attachment in _attachments.Values) attachment.Stop();
            try { _childStdin?.Close(); } catch { }
            try
            {
                if (_child != null && !_child.HasExited)
                {
                    _child.Kill();
                    _child.WaitForExit(3000);
                }
            }
            catch { }
            try { if (File.Exists(_recordPath)) File.Delete(_recordPath); } catch { }
        }

        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

        private string BuildChildFailureDiagnostic(Process child)
        {
            int exitCode = -1;
            try { exitCode = child != null && child.HasExited ? child.ExitCode : -1; } catch { }
            string stderr = Volatile.Read(ref _lastChildError) ?? string.Empty;
            string detail = "stage=child_exit exitCode=" + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(stderr)) detail += " stderr=" + stderr;
            return detail.Length <= 4096 ? detail : detail.Substring(0, 4096) + "…";
        }

        private static string RedactDiagnostic(string value)
        {
            // Same rule as PreviewService.LogValue: this copy was the only one
            // that knew about connectionstring, so a connection string reaching
            // a log was masked only on the paths that went through here.
            string redacted = LogRedaction.Redact(value);
            return redacted.Length <= 2048 ? redacted : redacted.Substring(0, 2048) + "…";
        }

        private static string Normalize(string value)
        {
            try { return Path.GetFullPath(value).TrimEnd('\\', '/').ToLowerInvariant(); }
            catch { return (value ?? string.Empty).Trim().TrimEnd('\\', '/').ToLowerInvariant(); }
        }
        private static void HostLog(string message) { try { Console.Error.WriteLine("[SharedWorkerHost] " + message); } catch { } }
    }

    internal sealed class SharedWorkerHostAttachment
    {
        private readonly SharedWorkerHostRuntime _host;
        private readonly NamedPipeServerStream _pipe;
        private readonly int _attachTimeoutMs;
        private readonly BlockingCollection<string> _output = new BlockingCollection<string>(128);
        private readonly BlockingCollection<string> _input = new BlockingCollection<string>(64);
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private int _stopped;
        private long _sequence;

        internal SharedWorkerHostAttachment(SharedWorkerHostRuntime host, NamedPipeServerStream pipe, int attachTimeoutMs)
        {
            _host = host;
            _pipe = pipe;
            _attachTimeoutMs = attachTimeoutMs;
            _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 64 * 1024, true);
            _writer = new StreamWriter(pipe, new UTF8Encoding(false), 64 * 1024, true) { AutoFlush = true, NewLine = "\n" };
            ConnectionId = Guid.NewGuid().ToString("N");
        }

        internal string ConnectionId { get; }
        internal string AttachmentId { get; set; } = string.Empty;
        internal long NextSequence() => Interlocked.Increment(ref _sequence);

        internal bool EnqueueChild(string line)
        {
            if (Volatile.Read(ref _stopped) != 0) return false;
            try { return _input.TryAdd(line); }
            catch (InvalidOperationException) { return false; }
        }

        internal bool TryDequeueChild(out string line)
        {
            line = string.Empty;
            try { return _input.TryTake(out line); }
            catch (InvalidOperationException) { return false; }
        }

        internal async Task RunAsync()
        {
            var writer = new Thread(WriterLoop) { IsBackground = true, Name = "SharedWorkerAttachmentWriter" };
            writer.Start();
            try
            {
                string first = await ReadLineWithTimeoutAsync().ConfigureAwait(false);
                if (first == null) return;
                if (!SharedWorkerHostProtocol.TryParseEnvelope(first, out SharedWorkerEnvelope envelope, out string error)
                    || !string.Equals(envelope.Type, "attach", StringComparison.Ordinal)
                    || !_host.Attach(this, envelope, out error))
                {
                    EnqueueError(error);
                    return;
                }
                string line;
                while (Volatile.Read(ref _stopped) == 0 && (line = await _reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    _host.Receive(this, line);
            }
            catch (Exception ex)
            {
                EnqueueError("attachment closed: " + ex.Message);
            }
            finally { Stop(); _host.Detach(this); }
        }

        private async Task<string> ReadLineWithTimeoutAsync()
        {
            Task<string> read = _reader.ReadLineAsync();
            Task completed = await Task.WhenAny(read, Task.Delay(_attachTimeoutMs)).ConfigureAwait(false);
            if (completed != read)
            {
                EnqueueError("attachment handshake timed out after " + _attachTimeoutMs + "ms");
                Stop();
                return null;
            }
            return await read.ConfigureAwait(false);
        }

        internal bool Enqueue(string line)
        {
            if (Volatile.Read(ref _stopped) != 0) return false;
            if (_output.TryAdd(line)) return true;
            Stop();
            return false;
        }

        internal void ClearChildQueue()
        {
            while (_input.TryTake(out _)) { }
        }

        internal void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            try { _input.CompleteAdding(); } catch { }
            try { _output.CompleteAdding(); } catch { }
            try { _pipe.Dispose(); } catch { }
        }

        private void EnqueueError(string message)
        {
            try
            {
                var error = new JObject
                {
                    ["type"] = "host_error",
                    ["protocolVersion"] = SharedWorkerHostProtocol.ProtocolVersion,
                    ["gxmcp"] = "host_error",
                    ["version"] = SharedWorkerHostProtocol.ProtocolVersion,
                    ["message"] = message ?? "attachment rejected"
                };
                _output.TryAdd(error.ToString(Formatting.None));
            }
            catch { }
        }

        private void WriterLoop()
        {
            try
            {
                foreach (string line in _output.GetConsumingEnumerable())
                {
                    _writer.WriteLine(line);
                    _writer.Flush();
                }
            }
            catch { }
            finally { Stop(); }
        }
    }

    internal sealed class SharedWorkerHostIdentity
    {
        public string WorkerExecutable { get; set; } = string.Empty;
        public string KbPath { get; set; } = string.Empty;
        public string InstallationPath { get; set; } = string.Empty;
        public string Driver { get; set; } = string.Empty;
        public string Major { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
    }

    internal sealed class SharedWorkerHostRegistryRecord
    {
        public SharedWorkerHostIdentity Identity { get; set; } = new SharedWorkerHostIdentity();
        public int HostPid { get; set; }
        public long HostStartTimeUtcTicks { get; set; }
        public int WorkerPid { get; set; }
        public long WorkerStartTimeUtcTicks { get; set; }
        public string PipeName { get; set; } = string.Empty;
        public long Generation { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public string State { get; set; } = string.Empty;
    }
}