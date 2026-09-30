using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 84 — genexus_multi_agent_lock. File-based advisory lock per
    /// (kbPath, target, part) stored as JSON under
    /// <c>&lt;kbPath&gt;/.gx/locks/&lt;sanitized&gt;.lock</c>.
    /// Actions: acquire / release / status. Auto-expires entries whose
    /// <c>atUtc + ttlSec</c> is in the past (treated as released so a fresh
    /// owner can take over).
    /// </summary>
    public class MultiAgentLockService
    {
        private readonly KbService _kbService;

        public MultiAgentLockService(KbService kbService)
        {
            _kbService = kbService;
        }

        public string Dispatch(string action, string target, string part, string ownerId, int ttlSec, string kbPathOverride = null, bool dryRun = false)
        {
            string kbPath = EffectiveKbPath.Resolve(_kbService, kbPathOverride);
            if (string.IsNullOrEmpty(kbPath))
                return McpResponse.Err(
                    code: "NoKbOpen",
                    message: "No KB is currently open.",
                    hint: "Open a KB first via genexus_kb action=open.",
                    nextSteps: new JArray { KbOpenNextStep.Step("Open a KB.") });
            if (dryRun)
            {
                string locksDir = System.IO.Path.Combine(kbPath, ".gx", "locks");
                string lockPath = System.IO.Path.Combine(locksDir, Sanitize(target, part) + ".lock");
                return McpResponse.Ok(
                    target: target,
                    code: "DryRun",
                    result: new JObject
                    {
                        ["preview"] = new JObject
                        {
                            ["action"] = action,
                            ["target"] = target,
                            ["part"] = part,
                            ["ownerId"] = ownerId,
                            ["ttlSec"] = ttlSec,
                            ["lockPath"] = lockPath,
                            ["note"] = "dryRun=true: no lock file written or deleted."
                        }
                    });
            }
            return DispatchCore(kbPath, action, target, part, ownerId, ttlSec);
        }

        public static string DispatchCore(string kbPath, string action, string target, string part, string ownerId, int ttlSec)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(target))
                    return McpResponse.Err(
                        code: "MissingTarget",
                        message: "target is required.",
                        hint: "Pass target=<object name> to identify which object to lock.");
                if (ttlSec <= 0) ttlSec = 300;
                if (ttlSec > 86400) ttlSec = 86400;

                string locksDir = Path.Combine(kbPath, ".gx", "locks");
                Directory.CreateDirectory(locksDir);
                string lockPath = Path.Combine(locksDir, Sanitize(target, part) + ".lock");

                action = (action ?? "status").ToLowerInvariant();

                using (var guard = LockGuard.TryAcquire(lockPath, TimeSpan.FromSeconds(5)))
                {
                    if (guard == null)
                        return McpResponse.Err(
                            code: "LockBusy",
                            message: "Another process is updating this object lock.",
                            hint: "Retry the lock operation; no ownership decision was made.",
                            target: target);

                    switch (action)
                    {
                    case "status":
                    {
                        var existing = TryReadLock(lockPath, out bool expired);
                        if (existing?["lockState"]?.ToString() == "corrupt")
                            return McpResponse.Err(
                                code: "LockCorrupt",
                                message: "The existing object lock is unreadable; refusing to guess ownership.",
                                hint: "Repair or remove the lock only after confirming no writer is active.",
                                target: target,
                                extra: new JObject { ["path"] = lockPath });
                        if (existing == null || expired)
                        {
                            return McpResponse.Ok(
                                target: target,
                                code: "LockStatusRetrieved",
                                result: new JObject
                                {
                                    ["held"] = false,
                                    ["holder"] = JValue.CreateNull(),
                                    ["path"] = lockPath
                                });
                        }
                        return McpResponse.Ok(
                            target: target,
                            code: "LockHeld",
                            result: new JObject
                            {
                                ["held"] = true,
                                ["holder"] = existing,
                                ["path"] = lockPath
                            });
                    }

                    case "acquire":
                    {
                        if (string.IsNullOrWhiteSpace(ownerId))
                            return McpResponse.Err(
                                code: "MissingOwnerId",
                                message: "ownerId is required for acquire.",
                                hint: "Pass ownerId=<unique agent id> to identify the lock holder.",
                                target: target);
                        var existing = TryReadLock(lockPath, out bool expired);
                        if (existing?["lockState"]?.ToString() == "corrupt")
                            return McpResponse.Err(
                                code: "LockCorrupt",
                                message: "The existing object lock is unreadable; refusing to replace it.",
                                hint: "Repair or remove the lock only after confirming no writer is active.",
                                target: target,
                                extra: new JObject { ["path"] = lockPath });
                        if (existing != null && !expired)
                        {
                            string existingOwner = existing["ownerId"]?.ToString();
                            if (!string.Equals(existingOwner, ownerId, StringComparison.Ordinal))
                            {
                                return McpResponse.Err(
                                    code: "AlreadyHeld",
                                    message: "Lock is already held by another owner.",
                                    hint: "Wait for the current holder to release the lock or for it to expire.",
                                    nextSteps: new JArray { McpResponse.NextStep("genexus_multi_agent_lock", new JObject { ["action"] = "status", ["target"] = target }, "Check current lock status.") },
                                    target: target,
                                    extra: new JObject { ["holder"] = existing });
                            }
                            // Same owner re-acquiring — refresh TTL.
                        }
                        var entry = new JObject
                        {
                            ["ownerId"] = ownerId,
                            ["atUtc"] = DateTime.UtcNow.ToString("o"),
                            ["ttlSec"] = ttlSec,
                            ["target"] = target,
                            ["part"] = part ?? string.Empty
                        };
                        string tmp = lockPath + ".tmp-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N");
                        try
                        {
                            File.WriteAllText(tmp, entry.ToString(Newtonsoft.Json.Formatting.None));
                            if (File.Exists(lockPath)) File.Replace(tmp, lockPath, null);
                            else File.Move(tmp, lockPath);
                        }
                        finally
                        {
                            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                        }
                        return McpResponse.Ok(
                            target: target,
                            code: "LockAcquired",
                            result: new JObject
                            {
                                ["held"] = true,
                                ["holder"] = entry,
                                ["path"] = lockPath,
                                ["takeover"] = expired
                            });
                    }

                    case "release":
                    {
                        if (!File.Exists(lockPath))
                        {
                            return McpResponse.Ok(
                                target: target,
                                code: "LockReleased",
                                result: new JObject
                                {
                                    ["held"] = false,
                                    ["note"] = "no lock file"
                                });
                        }
                        var existing = TryReadLock(lockPath, out bool expired);
                        if (existing?["lockState"]?.ToString() == "corrupt")
                        {
                            return McpResponse.Err(
                                code: "LockCorrupt",
                                message: "The existing object lock is unreadable; refusing to release it.",
                                hint: "Repair or remove the lock only after confirming no writer is active.",
                                target: target,
                                extra: new JObject { ["path"] = lockPath });
                        }
                        if (existing != null && !expired)
                        {
                            string existingOwner = existing["ownerId"]?.ToString();
                            if (!string.Equals(existingOwner, ownerId, StringComparison.Ordinal))
                            {
                                return McpResponse.Err(
                                    code: "WrongOwner",
                                    message: "ownerId mismatch; refusing to release.",
                                    hint: "Only the lock owner can release it. Pass the correct ownerId.",
                                    target: target,
                                    extra: new JObject { ["holder"] = existing });
                            }
                        }
                        File.Delete(lockPath);
                        return McpResponse.Ok(
                            target: target,
                            code: "LockReleased",
                            result: new JObject
                            {
                                ["held"] = false,
                                ["takeover"] = expired
                            });
                    }

                        default:
                            return McpResponse.Err(
                                code: "UnknownAction",
                                message: "action must be acquire|release|status; got '" + action + "'.",
                                hint: "Pass action=acquire, action=release, or action=status.",
                                nextSteps: new JArray(
                                    McpResponse.NextStep(
                                        tool: "genexus_multi_agent_lock",
                                        args: new JObject { ["action"] = "status", ["target"] = target },
                                        why: "Inspect current lock state before retrying with the right action.")));
                    }
                }
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "LockOperationFailed",
                    message: ex.Message,
                    hint: "Check file system permissions for the .gx/locks directory.",
                    target: target);
            }
        }

        private sealed class LockGuard : IDisposable
        {
            private readonly Mutex _mutex;
            private bool _held;

            private LockGuard(Mutex mutex)
            {
                _mutex = mutex;
                _held = true;
            }

            internal static LockGuard TryAcquire(string lockPath, TimeSpan timeout)
            {
                string name;
                using (var sha = SHA256.Create())
                {
                    byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(lockPath ?? string.Empty));
                    var hex = new StringBuilder(digest.Length * 2);
                    foreach (byte value in digest) hex.Append(value.ToString("x2"));
                    name = "Local\\GxMcp.MultiAgentLock." + hex;
                }

                var mutex = new Mutex(false, name);
                bool acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(timeout); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired)
                    {
                        mutex.Dispose();
                        return null;
                    }
                    return new LockGuard(mutex);
                }
                catch
                {
                    if (acquired) { try { mutex.ReleaseMutex(); } catch { } }
                    mutex.Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                if (!_held) return;
                _held = false;
                try { _mutex.ReleaseMutex(); } catch { }
                try { _mutex.Dispose(); } catch { }
            }
        }

        private static JObject TryReadLock(string path, out bool expired)
        {
            expired = false;
            if (!File.Exists(path)) return null;
            try
            {
                string raw = File.ReadAllText(path);
                var entry = JObject.Parse(raw);
                int ttl = entry["ttlSec"]?.ToObject<int?>() ?? 300;
                string atStr = entry["atUtc"]?.ToString();
                if (DateTime.TryParse(atStr, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var at))
                {
                    if (at.AddSeconds(ttl) < DateTime.UtcNow) expired = true;
                }
                return entry;
            }
            catch
            {
                // Corruption is not evidence of expiry. Preserve a sentinel so
                // status/acquire/release fail closed instead of guessing ownership.
                expired = false;
                return new JObject { ["lockState"] = "corrupt" };
            }
        }

        private static string Sanitize(string target, string part)
        {
            return LockFileKey.Sanitize(target, part);
        }
    }
}
