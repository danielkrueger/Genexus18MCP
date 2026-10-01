using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    internal sealed class SharedWorkerAttachInfo
    {
        public string AttachId { get; init; } = string.Empty;
        public int HostPid { get; init; }
        public long HostStartTimeUtcTicks { get; init; }
        public int WorkerPid { get; init; }
        public long WorkerStartTimeUtcTicks { get; init; }
        public long Generation { get; init; }
        public bool SdkReady { get; init; }
    }

    /// <summary>
    /// One Gateway-side connection to a shared Worker host. The host protocol is
    /// deliberately separate from JSON-RPC: only the attach/heartbeat/detach
    /// envelopes use the <c>gxmcp</c> marker; regular Worker JSON-RPC is passed
    /// through as one newline-delimited frame per request or notification.
    /// </summary>
    internal sealed class SharedWorkerConnection : IDisposable
    {
        internal const int ProtocolVersion = 1;
        internal const int MaxFrameBytes = 4 * 1024 * 1024;

        /// <summary>
        /// Absolute ceiling past which a frame is abusive rather than merely large.
        /// Issue #347: between <see cref="MaxFrameBytes"/> and this ceiling an oversized
        /// frame is parsed and, if well-formed, refused per request; past it we refuse to
        /// buffer and fail closed.
        /// </summary>
        internal const int HardFrameCeilingBytes = 4 * MaxFrameBytes;

        private readonly SharedWorkerIdentity _identity;
        private readonly SharedWorkerRecord _record;
        private readonly string _clientId;
        private readonly string _nonce = Guid.NewGuid().ToString("N");
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
        private NamedPipeClientStream? _pipe;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private Timer? _heartbeatTimer;
        private Task? _readerTask;
        private int _disposed;
        private int _disconnected;
        private string? _lastError;

        internal SharedWorkerConnection(SharedWorkerIdentity identity, SharedWorkerRecord record, string clientId)
        {
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _record = record ?? throw new ArgumentNullException(nameof(record));
            _clientId = string.IsNullOrWhiteSpace(clientId) ? Guid.NewGuid().ToString("N") : clientId;
        }

        internal SharedWorkerAttachInfo? AttachInfo { get; private set; }
        internal bool IsConnected => Volatile.Read(ref _disconnected) == 0 && Volatile.Read(ref _disposed) == 0;
        internal int? HostPid => AttachInfo?.HostPid;
        internal int? WorkerPid => AttachInfo?.WorkerPid;
        internal long? Generation => AttachInfo?.Generation;
        internal string? AttachId => AttachInfo?.AttachId;
        internal string? LastError => _lastError;
        internal string? LastRestartDiagnostic { get; private set; }

        internal event Action<string>? LineReceived;
        internal event Action<Exception?>? Disconnected;
        internal event Action<string?>? WorkerRestarted;

        internal void Connect(int timeoutMs)
        {
            if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            if (string.IsNullOrWhiteSpace(_record.PipeName))
                throw new InvalidOperationException("Shared Worker registry record has no pipe name.");

            _pipe = new NamedPipeClientStream(
                ".",
                _record.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            _pipe.Connect(timeoutMs);
            _reader = new StreamReader(_pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), bufferSize: 64 * 1024, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            WriteLineAsync(BuildAttachRequest(_identity, _clientId, Environment.ProcessId, GetCurrentProcessStartTicks(), _nonce), timeoutMs)
                .GetAwaiter()
                .GetResult();

            string? ackLine = ReadLineWithTimeout(timeoutMs);
            if (!TryParseAttachAck(ackLine, _identity, out SharedWorkerAttachInfo? attachInfo, out string error))
                throw new InvalidOperationException("Shared Worker attach handshake failed: " + error);

            AttachInfo = attachInfo;
            _readerTask = Task.Run(ReadLoopAsync);
            _heartbeatTimer = new Timer(_ => _ = SendHeartbeatAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }

        internal Task SendAsync(JObject rpc, CancellationToken cancellationToken)
        {
            if (rpc == null) throw new ArgumentNullException(nameof(rpc));
            return WriteLineAsync(rpc.ToString(Formatting.None), cancellationToken);
        }

        internal static JObject BuildAttachRequest(
            SharedWorkerIdentity identity,
            string clientId,
            int gatewayPid,
            long gatewayStartTimeUtcTicks,
            string nonce)
        {
            return new JObject
            {
                ["gxmcp"] = "attach",
                ["type"] = "attach",
                ["version"] = ProtocolVersion,
                ["protocolVersion"] = ProtocolVersion,
                ["key"] = identity.Key,
                ["identityKey"] = identity.Key,
                ["clientId"] = clientId,
                ["gatewayPid"] = gatewayPid,
                ["gatewayStartTimeUtcTicks"] = gatewayStartTimeUtcTicks,
                ["nonce"] = nonce,
                ["attachNonce"] = nonce
            };
        }

        internal static bool TryParseAttachAck(
            string? line,
            SharedWorkerIdentity identity,
            out SharedWorkerAttachInfo? info,
            out string error)
        {
            info = null;
            error = "empty attach acknowledgement";
            if (string.IsNullOrWhiteSpace(line)) return false;
            try
            {
                JObject frame = GxMcp.Common.JsonIngress.ParseObject(line);
                string frameType = frame["gxmcp"]?.ToString() ?? frame["type"]?.ToString() ?? string.Empty;
                if (!string.Equals(frameType, "attach_ack", StringComparison.Ordinal))
                {
                    error = "expected gxmcp=attach_ack";
                    return false;
                }
                int? frameVersion = frame.Value<int?>("version") ?? frame.Value<int?>("protocolVersion");
                if (frameVersion != ProtocolVersion)
                {
                    error = "protocol version mismatch";
                    return false;
                }
                string frameKey = frame["key"]?.ToString() ?? frame["identityKey"]?.ToString() ?? string.Empty;
                if (!string.Equals(frameKey, identity.Key, StringComparison.Ordinal))
                {
                    error = "shared Worker identity mismatch";
                    return false;
                }

                string attachId = frame["attachId"]?.ToString() ?? string.Empty;
                int hostPid = frame.Value<int?>("hostPid") ?? 0;
                long hostStart = frame.Value<long?>("hostStartTimeUtcTicks") ?? 0;
                int workerPid = frame.Value<int?>("workerPid") ?? 0;
                long workerStart = frame.Value<long?>("workerStartTimeUtcTicks") ?? 0;
                long generation = frame.Value<long?>("generation") ?? 0;
                bool sdkReady = frame.Value<bool?>("ready") ?? false;
                if (string.IsNullOrWhiteSpace(attachId) || hostPid <= 0 || hostStart <= 0 || workerPid <= 0 || workerStart <= 0 || generation <= 0)
                {
                    error = "attach acknowledgement is missing process identity";
                    return false;
                }

                info = new SharedWorkerAttachInfo
                {
                    AttachId = attachId,
                    HostPid = hostPid,
                    HostStartTimeUtcTicks = hostStart,
                    WorkerPid = workerPid,
                    WorkerStartTimeUtcTicks = workerStart,
                    Generation = generation,
                    SdkReady = sdkReady
                };
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = "malformed attach acknowledgement: " + ex.Message;
                return false;
            }
        }

        internal static bool IsControlFrame(JObject frame, string name)
            => frame != null && string.Equals(frame["gxmcp"]?.ToString(), name, StringComparison.Ordinal);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                WriteLineAsync(new JObject
                {
                    ["gxmcp"] = "detach",
                    ["type"] = "detach",
                    ["version"] = ProtocolVersion,
                    ["protocolVersion"] = ProtocolVersion,
                    ["key"] = _identity.Key,
                    ["identityKey"] = _identity.Key,
                    ["attachId"] = AttachId ?? string.Empty,
                    ["attachmentId"] = AttachId ?? string.Empty
                }, 500).GetAwaiter().GetResult();
            }
            catch { }
            try { _cts.Cancel(); } catch { }
            try { _heartbeatTimer?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            try { _writeGate.Dispose(); } catch { }
            SignalDisconnected(null);
        }

        private async Task ReadLoopAsync()
        {
            Exception? failure = null;
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    string? line = await _reader!.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;

                    long frameBytes = Encoding.UTF8.GetByteCount(line);
                    if (frameBytes > MaxFrameBytes)
                    {
                        // Issue #347. Throwing here signalled a disconnect, which took down
                        // every attachment on this broker - one client's legitimately large
                        // response became everyone's outage. A frame that parses is a valid
                        // response that does not fit the transport, so it becomes a bounded
                        // per-request error delivered to the requester alone.
                        //
                        // A frame that does not parse, or that is beyond the hard ceiling,
                        // is malformed or abusive and still disconnects: the oversized
                        // path must not become a way past the fail-closed check.
                        string? oversizedRefusal = SharedWorkerOversizedFrame.TryBuildRefusal(
                            line, frameBytes, MaxFrameBytes, HardFrameCeilingBytes);
                        if (oversizedRefusal == null)
                            throw new InvalidDataException(
                                "Shared Worker frame exceeded the maximum size and could not be attributed to a request.");

                        LineReceived?.Invoke(oversizedRefusal);
                        continue;
                    }

                    JObject? frame = TryParseObject(line);
                    if (frame != null && IsControlFrame(frame, "worker_restarted"))
                    {
                        if (!TryUpdateWorkerInfo(frame, out string restartError))
                            throw new InvalidOperationException(restartError);
                        try { WorkerRestarted?.Invoke(LastRestartDiagnostic); } catch { }
                        continue;
                    }
                    if (frame != null && (IsControlFrame(frame, "heartbeat_ack") || IsControlFrame(frame, "heartbeat")))
                        continue;
                    if (frame != null && IsControlFrame(frame, "host_error"))
                        throw new InvalidOperationException(frame["message"]?.ToString() ?? "Shared Worker host error.");
                    LineReceived?.Invoke(line);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                failure = ex;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                SignalDisconnected(failure);
            }
        }

        private async Task SendHeartbeatAsync()
        {
            if (!IsConnected) return;
            try
            {
                await WriteLineAsync(new JObject
                {
                    ["gxmcp"] = "heartbeat",
                    ["type"] = "heartbeat",
                    ["version"] = ProtocolVersion,
                    ["protocolVersion"] = ProtocolVersion,
                    ["key"] = _identity.Key,
                    ["identityKey"] = _identity.Key,
                    ["attachId"] = AttachId ?? string.Empty,
                    ["attachmentId"] = AttachId ?? string.Empty,
                    ["utc"] = DateTime.UtcNow.ToString("O")
                }, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SignalDisconnected(ex);
            }
        }

        private Task WriteLineAsync(JObject frame, int timeoutMs)
            => WriteLineWithTimeoutAsync(frame, timeoutMs);

        private async Task WriteLineWithTimeoutAsync(JObject frame, int timeoutMs)
        {
            using var timeout = new CancellationTokenSource(timeoutMs);
            await WriteLineAsync(frame, timeout.Token).ConfigureAwait(false);
        }

        private Task WriteLineAsync(JObject frame, CancellationToken cancellationToken)
            => WriteLineAsync(frame.ToString(Formatting.None), cancellationToken);

        private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            if (Encoding.UTF8.GetByteCount(line) > MaxFrameBytes)
                throw new InvalidDataException("Shared Worker frame exceeded the maximum size.");
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_writer == null) throw new IOException("Shared Worker pipe is not connected.");
                await _writer.WriteLineAsync(line).ConfigureAwait(false);
                await _writer.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        private string? ReadLineWithTimeout(int timeoutMs)
        {
            using var timeout = new CancellationTokenSource(timeoutMs);
            Task<string?> read = _reader!.ReadLineAsync();
            try
            {
                return read.WaitAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Shared Worker attach acknowledgement timed out.");
            }
        }

        private void SignalDisconnected(Exception? failure)
        {
            if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;
            _lastError = failure?.ToString();
            try { Disconnected?.Invoke(failure); } catch { }
        }

        private bool TryUpdateWorkerInfo(JObject frame, out string error)
        {
            error = string.Empty;
            int hostPid = frame.Value<int?>("hostPid") ?? 0;
            long hostStart = frame.Value<long?>("hostStartTimeUtcTicks") ?? 0;
            int workerPid = frame.Value<int?>("workerPid") ?? 0;
            long workerStart = frame.Value<long?>("workerStartTimeUtcTicks") ?? 0;
            long generation = frame.Value<long?>("generation") ?? 0;
            if (hostPid <= 0 || hostStart <= 0 || workerPid <= 0 || workerStart <= 0 || generation <= 0)
            {
                error = "worker restart frame is missing process identity";
                return false;
            }
            if (AttachInfo == null || hostPid != AttachInfo.HostPid || hostStart != AttachInfo.HostStartTimeUtcTicks)
            {
                error = "worker restart frame changed host identity";
                return false;
            }
            if (generation <= AttachInfo.Generation)
            {
                error = "worker restart generation did not advance";
                return false;
            }
            AttachInfo = new SharedWorkerAttachInfo
            {
                AttachId = AttachInfo.AttachId,
                HostPid = hostPid,
                HostStartTimeUtcTicks = hostStart,
                WorkerPid = workerPid,
                WorkerStartTimeUtcTicks = workerStart,
                Generation = generation,
                SdkReady = frame.Value<bool?>("ready") ?? false
            };
            LastRestartDiagnostic = frame["failureDiagnostic"]?.ToString();
            return true;
        }

        private static JObject? TryParseObject(string line)
        {
            try { return GxMcp.Common.JsonIngress.ParseObject(line); } catch { return null; }
        }

        private static long GetCurrentProcessStartTicks()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks; }
            catch { return 0; }
        }
    }
}
