#!/usr/bin/env python3
"""Live HTTP benchmark harness for the Genexus18MCP gateway.

Drives the Streamable-HTTP MCP endpoint (default http://127.0.0.1:5000/mcp),
opens the configured KB, waits for the index to be Ready, then measures
round-trip latency for the discovery/read/write-dryrun ops an LLM calls
repeatedly.

Usage:
  python scripts/bench-live-http.py [--kb C:/KBs/KBTeste] [--alias live]
      [--iterations 12] [--port 5000] [--out bench-live.json] [--name baseline]
      [--ops whoami,query,edit_dryrun]      # subset of the op catalog
      [--compare baseline.json]             # print delta table vs a prior --out
      [--fail-on-regression]                # exit 1 when p50 exceeds the threshold

Ops (all read-only / dry-run — nothing persists on the KB):
  whoami, list_objects, query, search_source, inspect, read,
  edit_dryrun   (genexus_edit mode=full dryRun on a small Transaction source —
                 append-marker write through the read->write->project->diff
                 pipeline, no persist),
  analyze       (genexus_analyze mode=summary — mode=impact's full caller-walk
                 exceeds the gateway's ~50s sync cap for tracked ops without a
                 progress token (SafeLongPollSecondsWithoutProgress) and is not
                 latency-measurable),
  lifecycle_status (genexus_lifecycle action=status — index/build health)

Latency hygiene: measure on a freshly restarted gateway. An op that exceeds the
~50s cap returns a 'Gateway timeout' envelope while STILL RUNNING in the STA
worker, serializing every later call behind it (every op then times out at 50s).
If a run shows uniform ~50s timeouts, restart the gateway and re-run.

Transport: every call shares one keep-alive connection (see http_post). Do not
switch back to a connection per call: a CPython socket operation carrying a
timeout waits through select() on Windows, whose granularity is the ~15.6ms
system timer, so roughly one sample in three used to measure client-side connect
instead of the server — the gateway answered those same calls in ~1ms while the
harness reported p95 ~22ms, and relative p50/p95 gates compared that timer.

Comparison mode: run with --compare <baseline.json> (the --out file of an
earlier run) and the harness prints a per-op p50/p95 delta table plus a
mean-delta and a >+25% p50 regression warning. Typical workflow:
  run 1: python scripts/bench-live-http.py --out .gx-smoke-futures/bench-base.json
  run 2 (after optimizing): python scripts/bench-live-http.py \
      --compare .gx-smoke-futures/bench-base.json --out .gx-smoke-futures/bench-new.json
"""
import argparse
from dataclasses import dataclass
import http.client
import json
import os
import statistics
import sys
import time
import urllib.parse

BASE = "http://127.0.0.1:5000/mcp"
MAX_ITERATIONS = 20

# Op catalog order is the run order. `edit_dryrun` is prepared lazily (needs a
# real identifier from the target's source); the rest are static shapes.
ALL_OPS = [
    "whoami",
    "kb_list",
    "kb_select",
    "list_objects",
    "query",
    "search_source",
    "inspect",
    "read",
    "edit_dryrun",
    "analyze",
    "lifecycle_status",
    "graph",
    "design_system",
    "pattern_diagnose",
]
# The default must contain operations that are valid for a plain sessionless
# HTTP run. Session selection is intentionally unavailable there, and graph /
# design-system require a target that may not exist in every KB. Those remain
# opt-in through --ops so unsupported capabilities fail explicitly when asked.
DEFAULT_OPS = [
    "whoami", "kb_list", "list_objects", "query", "search_source",
    "inspect", "read", "lifecycle_status", "pattern_diagnose",
]

# Folders, modules and physical tables are valid list/query results but do not
# reliably expose a Source part through genexus_read. Prefer object families
# with source-backed parts when selecting the shared inspect/read target set.
_SOURCE_BACKED_TYPES = frozenset({
    "procedure", "transaction", "webpanel", "panel", "sdpanel",
    "workpanel", "dataprovider", "businesscomponent", "externalobject",
    "structureddata", "structureddatatype", "structured datatype",
})


@dataclass(frozen=True)
class RpcMeasurement:
    """A wire measurement that remains backward-compatible with ``(elapsed, envelope)``.

    The benchmark historically returned a two-item tuple from ``rpc`` and a few
    local probes/tests unpack that shape.  Keeping ``__iter__`` two-item while
    exposing response_bytes lets the gate measure payload size without making
    those callers silently start ignoring a third value.
    """

    elapsed_ms: float
    envelope: object
    response_bytes: int = 0
    status_code: object = None
    content_bytes: int = 0
    structured_content_bytes: int = 0
    estimated_tokens: int = 0

    def __iter__(self):
        yield self.elapsed_ms
        yield self.envelope


class HttpResponse:
    """Transport result: status, headers and the raw body bytes."""

    __slots__ = ("status", "headers", "body")

    def __init__(self, status, headers, body):
        self.status = status
        self.headers = headers
        self.body = body


_connection = None
_connection_target = None


def _endpoint(base):
    parsed = urllib.parse.urlsplit(base)
    return parsed.hostname or "127.0.0.1", parsed.port or 80, parsed.path or "/"


def _close_connection():
    global _connection, _connection_target
    connection, _connection = _connection, None
    _connection_target = None
    if connection is not None:
        try:
            connection.close()
        except Exception:
            pass


def http_post(payload, session_id=None, timeout=180):
    """POST to the gateway over ONE persistent connection.

    Opening a fresh TCP connection per call is not a neutral measurement here:
    on Windows a CPython socket operation that carries a timeout waits through
    ``select()``, whose wait granularity is the system timer, so about one call
    in three paid ~15.6ms of client-side connect. Isolated on the same port:
    those spikes vanish when the socket has no timeout (blocking connect p95
    0.42ms) and never appear in a .NET client (fresh-connection HttpClient p95
    1.59ms), while the gateway logged ~1ms per call throughout — yet the harness
    reported p95 ~22ms, so p50/p95 gates were comparing the client's timer.
    Reusing the connection keeps every sample on the server's cost, and
    reconnects once when the server drops an idle socket.

    Retrying is safe for this harness only: every op in the catalog is read-only
    or a dry run, so a retry cannot persist anything.
    """
    global _connection, _connection_target
    host, port, path = _endpoint(BASE)
    target = (host, port)
    headers = {"Accept": "application/json, text/event-stream",
               "Content-Type": "application/json"}
    if session_id:
        headers["MCP-Session-Id"] = session_id
    last_error = None
    for _ in range(2):
        if _connection is None or _connection_target != target:
            _close_connection()
            _connection = http.client.HTTPConnection(host, port, timeout=timeout)
            _connection_target = target
        else:
            _connection.timeout = timeout
            if _connection.sock is not None:
                _connection.sock.settimeout(timeout)
        try:
            _connection.request("POST", path, body=payload, headers=headers)
            response = _connection.getresponse()
            body = response.read()
            return HttpResponse(response.status, response.headers, body)
        except (http.client.HTTPException, OSError) as error:
            last_error = error
            _close_connection()
    raise last_error


def rpc(session_id, method, params, timeout=180, is_notification=False):
    req_body = {"jsonrpc": "2.0", "method": method, "params": params}
    if not is_notification:
        req_body["id"] = 1
    body = json.dumps(req_body).encode()
    t0 = time.perf_counter()
    response = http_post(body, session_id, timeout)
    elapsed = (time.perf_counter() - t0) * 1000.0
    raw_bytes = response.body
    raw = raw_bytes.decode("utf-8", errors="replace")
    status_code = response.status
    if isinstance(status_code, int) and status_code >= 400:
        return RpcMeasurement(elapsed, {"__http_error__": status_code},
                              len(raw_bytes), status_code)
    response_bytes = len(raw_bytes)
    # JSON-in-JSON: result.content[0].text holds the worker envelope
    try:
        outer = json.loads(raw)
    except Exception:
        return RpcMeasurement(elapsed, None, response_bytes, status_code)
    if not isinstance(outer, dict) or outer.get("error"):
        return RpcMeasurement(elapsed, {"isError": True}, response_bytes, status_code)
    result = outer.get("result")
    if not isinstance(result, dict) or result.get("isError"):
        return RpcMeasurement(elapsed, {"isError": True}, response_bytes, status_code)
    content_bytes = 0
    content = result.get("content")
    if isinstance(content, list):
        content_bytes = sum(
            len(str(item.get("text", "")).encode("utf-8"))
            for item in content if isinstance(item, dict)
        )
    structured = result.get("structuredContent")
    structured_bytes = len(json.dumps(structured, separators=(",", ":"), ensure_ascii=False).encode("utf-8")) \
        if isinstance(structured, (dict, list)) else 0
    estimated_tokens = (content_bytes + structured_bytes + 3) // 4
    if isinstance(result.get("structuredContent"), dict):
        return RpcMeasurement(elapsed, result["structuredContent"], response_bytes, status_code,
                              content_bytes, structured_bytes, estimated_tokens)
    try:
        txt = outer["result"]["content"][0]["text"]
        inner = json.loads(txt)
    except Exception:
        inner = None
    return RpcMeasurement(elapsed, inner, response_bytes, status_code,
                          content_bytes, structured_bytes, estimated_tokens)


def percentile(samples, p):
    s = sorted(samples)
    if not s:
        return 0.0
    k = (p / 100.0) * (len(s) - 1)
    lo = int(k)
    hi = min(lo + 1, len(s) - 1)
    frac = k - lo
    return s[lo] * (1 - frac) + s[hi] * frac


def agg(name, samples, out, byte_samples=None):
    if not samples:
        print(f"  {name:24s} NO SAMPLES")
        out[name] = {"n": 0, "samples": [], "responseBytes": {"n": 0, "samples": []}}
        return
    avg = statistics.mean(samples)
    p50 = percentile(samples, 50)
    p95 = percentile(samples, 95)
    p99 = percentile(samples, 99)
    byte_samples = [int(x) for x in (byte_samples or []) if isinstance(x, (int, float)) and x >= 0]
    byte_stats = {"n": len(byte_samples), "samples": byte_samples}
    if byte_samples:
        byte_stats.update({
            "p50": round(percentile(byte_samples, 50), 2),
            "p95": round(percentile(byte_samples, 95), 2),
            "avg": round(statistics.mean(byte_samples), 2),
        })
    print(f"  {name:24s} n={len(samples):3d} p50={p50:7.1f}ms p95={p95:7.1f}ms p99={p99:7.1f}ms avg={avg:7.1f}ms"
          + (f" bytes-p50={byte_stats['p50']:.0f} bytes-p95={byte_stats['p95']:.0f}" if byte_samples else ""))
    out[name] = {
        "n": len(samples),
        "p50": round(p50, 2),
        "p95": round(p95, 2),
        "p99": round(p99, 2),
        "avg": round(avg, 2),
        "samples": [round(x, 2) for x in samples],
        "responseBytes": byte_stats,
    }


def read_content_text(env):
    """Extract source text from a genexus_read envelope (content/lines/source/
    text priority). Needed for short Transaction sources the recursive dig
    (>=40-char strings) misses."""
    if not isinstance(env, dict):
        return None
    for key in ("content", "lines", "source", "text"):
        v = env.get(key)
        if isinstance(v, list):
            return "\n".join(str(x) for x in v[:40])
        if isinstance(v, str):
            return v
    return None


_ERROR_STATUS_PREFIXES = ("error", "fail", "invalid", "notfound", "notimplemented")


def _case_insensitive_value(env, key):
    for name, value in env.items():
        if str(name).lower() == key.lower():
            return value
    return None


def select_read_targets(items):
    """Return source-backed ``{name, type}`` targets from list_objects data.

    The first page of a KB commonly contains folders/modules. Measuring
    ``genexus_read`` against those entries would count deterministic
    ``SourcePartNotFound`` errors as latency samples and fail the live gate.
    If a fixture exposes no recognized source family, retain the original
    names as a diagnostic fallback so the gate still fails closed.
    """
    entries = []
    for item in items if isinstance(items, list) else []:
        if not isinstance(item, dict):
            continue
        name = item.get("name")
        if not isinstance(name, str) or not name.strip():
            continue
        object_type = item.get("type")
        entries.append({"name": name, "type": object_type} if object_type else {"name": name})
    preferred = [
        entry for entry in entries
        if _is_source_backed_type(entry.get("type"))
    ]
    return preferred or entries


def _is_source_backed_type(object_type):
    return str(object_type or "").strip().lower() in _SOURCE_BACKED_TYPES


def prepare_read_targets(session_id, alias, candidates, max_targets=30):
    """Probe candidates once and retain only readable Source-backed objects."""
    valid = []
    seen = set()
    for entry in candidates if isinstance(candidates, list) else []:
        key = (entry.get("name"), entry.get("type"))
        if key in seen:
            continue
        seen.add(key)
        try:
            measurement = rpc(session_id, "tools/call", {
                "name": "genexus_read",
                "arguments": {"kb": alias, **entry, "part": "Source", "limit": 0},
            }, timeout=120)
            _, envelope = measurement
        except (OSError, TimeoutError):
            continue
        if operation_envelope_is_ok("read", envelope):
            valid.append(entry)
            if len(valid) >= max_targets:
                break
    return valid


def _has_error_signal(env):
    if env.get("isError") or env.get("error"):
        return True
    status = str(_case_insensitive_value(env, "status") or "").strip().lower()
    if status and status.startswith(_ERROR_STATUS_PREFIXES):
        return True
    code = str(env.get("code") or "").strip().lower()
    return bool(code and code.startswith(_ERROR_STATUS_PREFIXES))


def envelope_is_ok(env):
    """True for an envelope with an explicit successful status.

    Some tools expose a typed status (for example ``search_source``), while
    others expose a statusless success shape (for example ``genexus_read``).
    Callers measuring a concrete operation must use
    :func:`operation_envelope_is_ok`, which validates that operation's shape.
    This helper remains strict for generic callers such as dry-run target
    discovery.
    """
    if not isinstance(env, dict) or not env or _has_error_signal(env):
        return False
    status = str(_case_insensitive_value(env, "status") or "").strip().lower()
    return status in ("ok", "success") or env.get("ok") is True


def operation_envelope_is_ok(operation, env):
    """Validate the minimum shape needed for a latency sample.

    The MCP wire contract deliberately allows successful statusless envelopes.
    A successful response without the requested collection is still a protocol
    failure, not a fast empty result. Empty ``results``/``items`` arrays remain
    valid because they are legitimate answers.
    """
    if not isinstance(env, dict) or not env or _has_error_signal(env):
        return False
    if operation in ("list_objects", "query"):
        return isinstance(env.get("results"), list) or isinstance(env.get("items"), list)
    if operation == "search_source":
        if isinstance(env.get("results"), list) or isinstance(env.get("items"), list):
            return True
        nested = env.get("result")
        return isinstance(nested, dict) and isinstance(nested.get("hits"), list)
    if operation == "whoami":
        return env.get("connected") is True and isinstance(env.get("kb"), dict)
    if operation == "inspect":
        return any(key in env for key in ("name", "identity", "summary", "availableParts", "type"))
    if operation == "read":
        return any(key in env for key in ("source", "content", "parts", "part", "versionToken", "isEmpty"))
    if operation == "lifecycle_status":
        return any(key in env for key in ("status", "Status", "Phase", "TaskId", "summary", "compact"))
    if operation == "analyze":
        return any(key in env for key in ("name", "type", "summary", "metrics", "criticalDependencies", "intents", "linter"))
    if operation == "kb_list":
        return any(key in env for key in ("open", "openKbs", "known", "declared", "kbs", "items", "results"))
    if operation == "kb_select":
        return any(key in env for key in ("selected", "sessionSelection", "selectionSource", "selectionState", "kbAlias", "alias"))
    if operation == "graph":
        return any(key in env for key in ("nodes", "edges", "graph", "content", "markdown", "result", "summary"))
    if operation == "design_system":
        return any(key in env for key in ("tokens", "styles", "classes", "designSystem", "result", "summary"))
    if operation == "pattern_diagnose":
        return any(key in env for key in ("findings", "diagnostics", "pattern", "actions", "result", "summary"))
    return envelope_is_ok(env)


def _population_matches(baseline, current):
    """Require an explicit, equivalent population for regression gating."""
    base_population = baseline.get("population")
    current_population = current.get("population")
    if not isinstance(base_population, dict) or not isinstance(current_population, dict):
        return False
    required = ("fixtureId", "fixtureRevision", "generator", "cacheMode",
                "concurrency", "iterations", "ops")
    for population in (base_population, current_population):
        if any(key not in population for key in required):
            return False
        if any(isinstance(population[key], str) and not population[key].strip()
               for key in ("fixtureId", "fixtureRevision", "generator", "cacheMode")):
            return False
        if not isinstance(population["concurrency"], int) or population["concurrency"] < 1:
            return False
        if not isinstance(population["iterations"], int) or population["iterations"] < 1:
            return False
        if not isinstance(population["ops"], list) or not population["ops"]:
            return False
    # Timestamp/label are intentionally excluded.  All other fields describe
    # the fixture, SDK/model, cache state, concurrency and requested sample set.
    return base_population == current_population


def print_comparison(baseline, current, max_p50_regression, max_p95_regression=25.0,
                     max_bytes_regression=25.0):
    print("\n=== COMPARISON (baseline -> current) ===")
    if not isinstance(baseline, dict):
        return None
    if not _population_matches(baseline, current):
        print("  benchmark populations differ or are missing explicit metadata")
        return None
    base_ops = baseline.get("ops", {})
    cur_ops = current.get("ops", {})
    if not isinstance(base_ops, dict) or set(base_ops) != set(cur_ops):
        print("  operation sets differ; comparison invalid")
        return None
    for stats in list(base_ops.values()) + list(cur_ops.values()):
        if not isinstance(stats, dict) or stats.get("n", 0) <= 0 or stats.get("failed", 0) or stats.get("skipped", 0):
            return None
        if any(not isinstance(stats.get(k), (int, float)) or not 0 < stats[k] < float("inf") for k in ("p50", "p95")):
            return None
        bytes_stats = stats.get("responseBytes")
        if (not isinstance(bytes_stats, dict) or bytes_stats.get("n", 0) <= 0
                or any(not isinstance(bytes_stats.get(k), (int, float)) or bytes_stats[k] < 0
                       for k in ("p50", "p95"))):
            return None
    keys = [k for k in ALL_OPS if k in base_ops and k in cur_ops]
    if not keys:
        keys = [k for k in base_ops if k in cur_ops]
    if not keys:
        print("  no overlapping ops to compare")
        return None
    print(f"  {'op':<18} {'base p50':>9} {'cur p50':>9} {'delta':>8}   "
          f"{'base p95':>9} {'cur p95':>9} {'delta':>8}   {'bytes delta':>11}")
    total = 0.0
    n = 0
    regressions = []
    for k in keys:
        b, c = base_ops[k], cur_ops[k]
        b50, c50 = b.get("p50", 0.0), c.get("p50", 0.0)
        b95, c95 = b.get("p95", 0.0), c.get("p95", 0.0)
        bb95 = b["responseBytes"].get("p95", 0.0)
        cb95 = c["responseBytes"].get("p95", 0.0)
        d50 = ((c50 - b50) / b50 * 100.0) if b50 else 0.0
        d95 = ((c95 - b95) / b95 * 100.0) if b95 else 0.0
        dbytes = ((cb95 - bb95) / bb95 * 100.0) if bb95 else 0.0
        print(f"  {k:<18} {b50:8.2f}ms {c50:8.2f}ms {d50:+7.1f}%   "
              f"{b95:8.2f}ms {c95:8.2f}ms {d95:+7.1f}%   {dbytes:+7.1f}%")
        total += d50
        n += 1
        if d50 > max_p50_regression:
            regressions.append((k, d50))
        if d95 > max_p95_regression:
            regressions.append((k + " p95", d95))
        if dbytes > max_bytes_regression:
            regressions.append((k + " responseBytes p95", dbytes))
    if n:
        print(f"\n  mean p50 delta: {total/n:+.1f}%")
    if regressions:
        for k, d in regressions:
            print(f"  WARNING: {k} regressed {d:+.1f}% — investigate before shipping")
    else:
        print(f"  no p50/p95/responseBytes regressions above +{max_p50_regression:.1f}%/"
              f"+{max_p95_regression:.1f}%/+{max_bytes_regression:.1f}%")
    return regressions


# ---------------------------------------------------------------------------
# Issue #358: the scale matrix.
#
# The harness drove exactly one KB through one client, so it could not express the
# 1/3-KB by 1/2-client grid the issue asks for, and it had no way to say "this cell
# could not run" other than by not appearing in the output. Both are addressed here
# as pure functions so they are testable without a gateway, a KB or a GeneXus
# installation - the native lane is optional precisely because it cannot run
# everywhere, and a lane whose behavior is only observable when it works is a lane
# that silently stops being checked.
#
# `=` separates a path from its alias rather than `:`: every Windows KB path starts
# with a drive letter, so a colon separator would make `C:/KBs/KBTeste` ambiguous
# with `C:/KBs/KBTeste:live`.
# ---------------------------------------------------------------------------

MATRIX_KB_COUNTS = (1, 3)
MATRIX_CLIENT_COUNTS = (1, 2)


def parse_kb_specs(spec, default_alias_prefix="kb"):
    """Parse ``--kbs`` into ``[{"path", "alias"}]``.

    Raises ValueError on a malformed entry rather than dropping it. A silently
    discarded KB turns a three-KB cell into a one-KB cell that still reports as a
    pass, which is the failure mode a scale matrix exists to prevent.
    """
    if spec is None:
        return []
    if isinstance(spec, (list, tuple)):
        raw_entries = list(spec)
    else:
        raw_entries = [part for part in str(spec).split(",")]

    specs = []
    for index, raw in enumerate(raw_entries):
        entry = raw.strip()
        if not entry:
            continue
        if "=" in entry:
            path, alias = entry.split("=", 1)
            path, alias = path.strip(), alias.strip()
            if not path:
                raise ValueError(f"KB spec {entry!r} has a path-less entry")
            if not alias:
                raise ValueError(f"KB spec {entry!r} declares an empty alias")
        else:
            path, alias = entry, f"{default_alias_prefix}{index + 1}"
        specs.append({"path": path, "alias": alias})

    aliases = [s["alias"] for s in specs]
    if len(set(a.lower() for a in aliases)) != len(aliases):
        raise ValueError(f"KB aliases must be unique; got {aliases}")
    # The same path twice under different aliases is the same KB declared twice. The
    # alias check cannot see it, and left alone it lets a "3-KB" matrix measure one KB
    # twice against a warm cache and report the result as a three-KB figure.
    paths = [os.path.normcase(os.path.abspath(s["path"])) for s in specs]
    if len(set(paths)) != len(paths):
        raise ValueError(f"KB paths must be distinct; got {[s['path'] for s in specs]}")
    return specs


def classify_kb_availability(spec):
    """Availability of one declared KB, decided without opening anything.

    Reports ``unavailable`` rather than an error. The issue requires that missing
    fixtures are not passes, and the only way to guarantee that is to make
    "unavailable" a value the summarizer cannot fold into success.
    """
    path = (spec or {}).get("path")
    if not path:
        return {"state": "unavailable", "reason": "no_path"}
    # os.path.exists rather than os.path.isdir: a path pointing at a file is not a KB,
    # and reporting it as one would let the run proceed and fail confusingly later.
    if not os.path.exists(path):
        return {"state": "unavailable", "reason": "path_not_found"}
    if not os.path.isdir(path):
        return {"state": "unavailable", "reason": "path_not_a_directory"}
    return {"state": "available", "reason": None}


def apply_kb_availability(cells, kb_specs):
    """Mark each cell unavailable when a KB it needs is missing.

    Separate from :func:`matrix_cells` so the grid shape and the availability
    decision are independently testable, and so the decision exists in one place.
    A cell is judged on the KBs it actually needs, which is what makes a 1-KB cell
    runnable while the 3-KB cell beside it is unavailable.
    """
    specs = list(kb_specs or [])
    for cell in cells or []:
        if cell.get("state") == "unavailable":
            continue  # too few KBs declared; matrix_cells already recorded why
        reasons = []
        for index in range(cell.get("kbCount", 0)):
            if index >= len(specs):
                reasons.append("kb_not_declared")
                continue
            state = classify_kb_availability(specs[index])
            if state["state"] != "available":
                reasons.append(f"{specs[index]['alias']}:{state['reason']}")
        cell["state"] = "unavailable" if reasons else "declared"
        cell["reason"] = ",".join(reasons) or None
    return cells


def matrix_cells(kb_specs, kb_counts=MATRIX_KB_COUNTS,
                 client_counts=MATRIX_CLIENT_COUNTS):
    """Enumerate the KB-count x client-count grid.

    A cell that cannot be filled - more KBs requested than declared - is emitted
    with ``state: unavailable`` rather than omitted, so the report shows the cell
    was considered and could not run instead of quietly narrowing the matrix.
    """
    cells = []
    declared = len(kb_specs or [])
    for kb_count in kb_counts:
        for client_count in client_counts:
            cell = {
                "cellId": f"kbs{kb_count}-clients{client_count}",
                "kbCount": kb_count,
                "clientCount": client_count,
                "kbs": [s["alias"] for s in (kb_specs or [])[:kb_count]],
                "state": "ready",
                "reason": None,
            }
            if declared < kb_count:
                cell["state"] = "unavailable"
                cell["reason"] = f"declared {declared} KB(s), cell needs {kb_count}"
            cells.append(cell)
    return cells


def summarize_cells(cells):
    """Overall outcome for a matrix run.

    ``unavailable`` outranks ``fail``: a grid whose missing fixtures were reported
    as failures would train a reader to ignore red. A cell in state ``declared``
    was enumerated but not exercised by this run and is ignored - but if that
    leaves nothing executed, the result is unavailable rather than an empty pass.
    """
    cells = [c for c in (cells or []) if c.get("state") != "declared"]
    if not cells:
        return {"outcome": "unavailable", "reason": "no_executed_cells",
                "pass": 0, "fail": 0, "unavailable": 0}

    pass_count = sum(1 for c in cells if c.get("state") == "pass")
    fail_count = sum(1 for c in cells if c.get("state") == "fail")
    unavailable_count = sum(1 for c in cells if c.get("state") == "unavailable")

    if unavailable_count:
        return {"outcome": "unavailable",
                "reason": f"{unavailable_count} cell(s) could not run",
                "pass": pass_count, "fail": fail_count, "unavailable": unavailable_count}
    if fail_count:
        return {"outcome": "fail", "reason": f"{fail_count} cell(s) failed",
                "pass": pass_count, "fail": fail_count, "unavailable": 0}
    return {"outcome": "pass", "reason": None,
            "pass": pass_count, "fail": 0, "unavailable": 0}


def _write_report(report, path):
    """Write the JSON report, creating the directory if needed."""
    out_dir = os.path.dirname(os.path.abspath(path))
    if out_dir and not os.path.exists(out_dir):
        os.makedirs(out_dir, exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2)
    print(f"\nWrote {path}")


def _open_session(client_name):
    """Initialize an independent MCP session. Returns (session_id, initialize_ms)."""
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                       "params": {"protocolVersion": "2025-03-26", "capabilities": {},
                                  "clientInfo": {"name": f"bench-live-http/{client_name}",
                                                 "version": "1.0"}}}).encode()
    started = time.perf_counter()
    response = http_post(body, None, 30)
    elapsed_ms = (time.perf_counter() - started) * 1000.0
    if response.status >= 400:
        raise RuntimeError(f"{client_name}: initialize returned HTTP {response.status}")
    session_id = response.headers.get("MCP-Session-Id")
    if not session_id:
        raise RuntimeError(f"{client_name}: no MCP-Session-Id in initialize response")
    rpc(session_id, "notifications/initialized", {}, is_notification=True)
    return session_id, elapsed_ms


def measure_read_under_load(session_id, load_session_id, alias, entries, samples, n):
    """Latency of an interactive read while another session is doing background work.

    The issue asks for interactive-read latency under background load, which is a
    different question from read latency on an idle worker: it is the measurement
    that shows whether a bulk operation makes a user-facing call slow. The load is
    issued on a separate session so the read is genuinely queued behind other work
    rather than behind its own predecessor.
    """
    if not entries:
        return {"n": 0, "samples": [], "readSamples": [], "loadSamples": [],
                "responseBytes": {"n": 0, "samples": []}}

    read_samples, load_samples, byte_samples = [], [], []
    for i in range(n):
        entry = entries[i % len(entries)]
        load_started = time.perf_counter()
        try:
            load_ms, load_env = rpc(load_session_id, "tools/call", {
                "name": "genexus_search_source",
                "arguments": {"kb": alias, "pattern": "parm", "maxResults": 5},
            }, timeout=180)
        except (OSError, TimeoutError):
            load_ms = None
        load_elapsed = (time.perf_counter() - load_started) * 1000.0
        if load_ms is not None and operation_envelope_is_ok("search_source", load_env):
            load_samples.append(load_elapsed)

        measurement = rpc(session_id, "tools/call", {
            "name": "genexus_read",
            "arguments": {"kb": alias, **entry, "part": "Source", "limit": 0},
        }, timeout=180)
        read_ms, read_env = measurement
        if not operation_envelope_is_ok("read", read_env):
            continue
        read_samples.append(read_ms)
        response_bytes = getattr(measurement, "response_bytes", 0)
        if isinstance(response_bytes, (int, float)) and response_bytes > 0:
            byte_samples.append(response_bytes)

    agg("read_under_load", read_samples, {"read_under_load": {}}, byte_samples)
    return {"n": len(read_samples), "samples": [round(x, 2) for x in read_samples],
            "p50": round(percentile(read_samples, 50), 2) if read_samples else None,
            "p95": round(percentile(read_samples, 95), 2) if read_samples else None,
            "loadIssued": len(load_samples),
            "loadP50Ms": round(percentile(load_samples, 50), 2) if load_samples else None,
            "responseBytes": {"n": len(byte_samples), "samples": byte_samples}}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--kb", default="C:/KBs/KBTeste")
    ap.add_argument("--alias", default="live")
    ap.add_argument("--iterations", type=int, default=12)
    ap.add_argument("--port", type=int, default=5000)
    ap.add_argument("--out", default=None)
    ap.add_argument("--name", default=None)
    ap.add_argument("--ops", default=None,
                    help="Comma-separated subset of the op catalog. Default: all ops.")
    ap.add_argument("--compare", default=None,
                    help="Path to a prior --out JSON; prints a p50/p95 delta table vs this run.")
    ap.add_argument("--max-p50-regression", type=float, default=25.0,
                    help="p50 regression percentage that is considered a failure (default: 25).")
    ap.add_argument("--fail-on-regression", action="store_true",
                    help="Exit 1 when comparison mode finds a p50 or p95 regression above its threshold.")
    ap.add_argument("--max-p95-regression", type=float, default=25.0,
                    help="Maximum p95 regression percentage (default: 25).")
    ap.add_argument("--max-bytes-regression", type=float, default=25.0,
                    help="Maximum response-byte p95 growth percentage (default: 25).")
    ap.add_argument("--fixture-id", default=None,
                    help="Stable synthetic fixture identity for population matching.")
    ap.add_argument("--fixture-revision", default=None,
                    help="Fixture seed/revision for population matching.")
    ap.add_argument("--generator", default=None,
                    help="GeneXus generator/SDK identity for population matching.")
    ap.add_argument("--cache-mode", choices=("cold", "warm", "mixed"), default="warm",
                    help="Cache state represented by this run (default: warm).")
    ap.add_argument("--concurrency", type=int, default=1,
                    help="Logical client concurrency represented by this run.")
    # Issue #358: the scale matrix. --kbs declares the KB axis; --clients opens that
    # many independent MCP sessions and, at 2 or more, measures interactive-read
    # latency while the extra sessions issue background work.
    ap.add_argument("--kbs", default=None,
                    help="Comma-separated KBs as 'path=alias' (aliases are required to "
                         "be unique). '=' rather than ':' because every Windows KB path "
                         "starts with a drive letter. Omit for a single --kb run.")
    ap.add_argument("--clients", type=int, default=1,
                    help="Independent MCP sessions to open (default: 1). Values above 1 "
                         "additionally measure interactive-read latency under background load.")
    ap.add_argument("--matrix", action="store_true",
                    help="Declare the full 1/3-KB by 1/2-client grid and report each cell "
                         "as pass, fail or unavailable. Unavailable never counts as pass.")
    args = ap.parse_args()

    if args.iterations < 1 or args.iterations > MAX_ITERATIONS or (args.fail_on_regression and not args.compare):
        print(f"FATAL: iterations must be between 1 and {MAX_ITERATIONS}, and regression gating requires a baseline")
        return 2

    if args.concurrency < 1:
        print("FATAL: --concurrency must be positive")
        return 2

    if not all(0 <= threshold < float("inf") for threshold in (args.max_p50_regression,
                                                                  args.max_p95_regression,
                                                                  args.max_bytes_regression)):
        print("FATAL: regression thresholds must be finite and non-negative")
        return 2

    # Issue #358. Parsed and validated before any KB is opened, because a malformed
    # matrix discovered halfway through a run has already measured the wrong thing.
    try:
        kb_specs = parse_kb_specs(args.kbs)
    except ValueError as ex:
        print(f"FATAL: {ex}")
        return 2

    if args.kbs and not kb_specs:
        print("FATAL: --kbs was given but declared no KB")
        return 2
    if args.clients < 1:
        print("FATAL: --clients must be positive")
        return 2
    if args.kbs and not args.kb:
        # The matrix replaces the single-KB axis; keep exactly one source of truth for
        # which KB is open rather than letting --kb silently win over --kbs.
        args.kb = kb_specs[0]["path"]
        args.alias = kb_specs[0]["alias"]

    # Availability is per declared KB, decided before anything is opened. A cell that
    # needs three KBs is unavailable when fewer were declared or any is missing; the
    # run below then exercises the one cell whose shape matches what it opened.
    matrix = None
    if args.matrix or args.clients > 1:
        availability = [classify_kb_availability(s) for s in kb_specs]
        cells = apply_kb_availability(matrix_cells(kb_specs), kb_specs)

        executed = {"cellId": f"kbs1-clients{args.clients}",
                    "kbCount": 1, "clientCount": args.clients,
                    "kbs": [args.alias], "state": "declared", "reason": None}
        cells.append(executed)

        print("\n=== SCALE MATRIX ===")
        print(f"  declared KBs: {len(kb_specs)}  clients this run: {args.clients}")
        for cell in cells:
            print(f"  {cell['cellId']:20s} {cell['state']:12s} {cell['reason'] or ''}")
        matrix = {"cells": cells, "summary": summarize_cells(cells),
                  "declaredKbs": [{"alias": s["alias"],
                                   "availability": classify_kb_availability(s)}
                                  for s in kb_specs],
                  "executedCellId": executed["cellId"]}
        print("  matrix outcome before measurement: "
              + matrix["summary"]["outcome"].upper())

    ops = [o.strip() for o in (args.ops or "").split(",") if o.strip()] if args.ops else list(DEFAULT_OPS)
    unknown = [o for o in ops if o not in ALL_OPS]
    if unknown:
        print(f"FATAL: unknown op(s) {unknown}; catalog: {ALL_OPS}")
        return 2

    global BASE
    BASE = f"http://127.0.0.1:{args.port}/mcp"

    # Handshake — single initialize, capture session id from response headers.
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                       "params": {"protocolVersion": "2025-03-26", "capabilities": {},
                                  "clientInfo": {"name": "bench-live-http", "version": "1.0"}}}).encode()
    t0 = time.perf_counter()
    response = http_post(body, None, 30)
    session_id = response.headers.get("MCP-Session-Id")
    el = (time.perf_counter() - t0) * 1000.0
    if response.status >= 400:
        print("FATAL: initialize returned HTTP " + str(response.status) + ": "
              + response.body[:400].decode("utf-8", errors="replace"))
        return 2
    if not session_id:
        print("FATAL: no MCP-Session-Id in initialize response")
        return 2
    print(f"initialize: {el:.0f}ms session: {session_id}")

    rpc(session_id, "notifications/initialized", {}, is_notification=True)
    time.sleep(1)

    # Issue #358: the client axis of the matrix. Independent sessions, not a shared
    # one - a second session is what makes the read-under-load measurement below a
    # genuine concurrency measurement rather than a read queued behind itself. An
    # extra session that cannot be established makes the cell unavailable, not a pass
    # with fewer clients than declared.
    load_sessions = []
    client_init_ms = [el]
    for client_index in range(1, args.clients):
        try:
            load_session_id, load_init_ms = _open_session(f"load{client_index}")
        except (RuntimeError, OSError) as ex:
            print(f"client {client_index + 1} could not be established: {ex}")
            if matrix is not None:
                for cell in matrix["cells"]:
                    if cell["cellId"] == matrix["executedCellId"]:
                        cell["state"] = "unavailable"
                        cell["reason"] = f"client_{client_index + 1}_unavailable"
                matrix["summary"] = summarize_cells(matrix["cells"])
                print("  matrix outcome: " + matrix["summary"]["outcome"].upper())
            if args.out:
                _write_report({"timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                               "label": args.name or "run", "kb": args.kb,
                               "iterations": args.iterations, "ops": {}, "opsOrder": ops,
                               "matrix": matrix, "unavailableReason": str(ex)},
                              args.out)
            return 2
        load_sessions.append(load_session_id)
        client_init_ms.append(load_init_ms)
        print(f"client {client_index + 1}: initialize {load_init_ms:.0f}ms")
    if args.clients > 1:
        print(f"clients: {args.clients} independent sessions "
              f"(initialize p50 {percentile(client_init_ms, 50):.0f}ms)")

    # Open KB
    el, inner = rpc(session_id, "tools/call", {
        "name": "genexus_kb",
        "arguments": {"action": "open", "path": args.kb, "alias": args.alias},
    }, timeout=240)
    if isinstance(inner, dict) and "__http_error__" in inner:
        print(f"open KB: HTTP {inner['__http_error__']}")
        return 2
    status = (inner or {}).get("status", "?")
    print(f"open KB {args.kb}: {el:.0f}ms status={status}")

    # Wait for index Ready (poll whoami)
    print("waiting for index Ready...", flush=True)
    ready = False
    for _ in range(48):
        el, inner = rpc(session_id, "tools/call", {
            "name": "genexus_whoami",
            "arguments": {"kb": args.alias},
        }, timeout=60)
        if inner:
            idx = inner.get("index") or {}
            st = idx.get("status", "?")
            tot = idx.get("totalObjects", 0)
            print(f"  index status={st} total={tot}", flush=True)
            if st in ("Ready", "LiteReady", "Enriching"):
                ready = True
                break
        time.sleep(4)
    if not ready:
        print("WARN: index not Ready; benchmarking anyway (numbers include cold path)")

    time.sleep(3)

    # Discover real object names for inspect/read targets
    el, inner = rpc(session_id, "tools/call", {
        "name": "genexus_list_objects",
        "arguments": {"kb": args.alias, "limit": 30},
    }, timeout=120)
    listed_items = (inner or {}).get("results") if inner else []
    target_entries = select_read_targets(listed_items)
    if not any(_is_source_backed_type(entry.get("type")) for entry in target_entries):
        # The first page is often entirely folders/modules. Ask the index for
        # source-backed families before falling back to those non-readable rows.
        target_entries = []
        for type_filter in ("Procedure", "Transaction", "WebPanel", "DataProvider", "SDPanel"):
            _, typed_inner = rpc(session_id, "tools/call", {
                "name": "genexus_list_objects",
                "arguments": {"kb": args.alias, "typeFilter": type_filter, "limit": 30},
            }, timeout=120)
            typed = select_read_targets((typed_inner or {}).get("results") if typed_inner else [])
            typed = [entry for entry in typed if _is_source_backed_type(entry.get("type"))]
            if typed:
                target_entries.extend(typed)
                break
    if not target_entries:
        target_entries = [{"name": "TrnGroupProbeBase", "type": "Transaction"}]
    target_entries = prepare_read_targets(session_id, args.alias, target_entries)
    if not target_entries:
        # Keep the failure visible to the gate when a fixture has no readable
        # Source part; never silently convert an empty target population into
        # successful latency samples.
        target_entries = [{"name": "TrnGroupProbeBase", "type": "Transaction"}]
    names = [entry["name"] for entry in target_entries]
    print(f"discovered {len(target_entries)} source-backed object names for read/inspect targets")

    # edit_dryrun needs a real identifier from the target's source so the
    # patch `find` actually matches (a miss would short-circuit to an error
    # path and under-measure the write pipeline). Target a small Transaction:
    # a patch on a big WebForm source exceeds the gateway's ~50s sync cap and
    # leaves a long op running in the STA worker (poisoning every later op).
    # Some Transactions (atomic-created probes) have an EMPTY Source part, so
    # iterate candidates until one yields an identifier.
    edit_args = None
    if "edit_dryrun" in ops:
        # Target small Transactions: a write on a big WebForm source exceeds the
        # gateway's ~50s sync cap and leaves a long op running in the STA worker
        # (poisoning every later op). Some Transactions (atomic-created probes)
        # have an EMPTY Source part, and some objects fail the write-path read
        # even when genexus_read works (the gateway auto-injects type="Table"
        # for a Transaction, resolving to the table object, which exposes no
        # Source). Iterate candidates: read the source, then VERIFY a dryRun
        # write actually succeeds before pinning it.
        edit_candidates = []
        for type_filter in ("Transaction", "Procedure"):
            el, inner = rpc(session_id, "tools/call", {
                "name": "genexus_list_objects",
                "arguments": {"kb": args.alias, "typeFilter": type_filter, "limit": 10},
            }, timeout=120)
            for r in (inner or {}).get("results") or []:
                nm = r.get("name")
                if nm:
                    edit_candidates.append((nm, type_filter))
            if edit_candidates:
                break
        edit_candidates = edit_candidates or [(n, None) for n in names]
        for cand, cand_type in edit_candidates:
            el, inner = rpc(session_id, "tools/call", {
                "name": "genexus_read",
                "arguments": {"kb": args.alias, "name": cand, "part": "Source", "limit": 0},
            }, timeout=120)
            # Never build content from a non-source envelope.
            if not isinstance(inner, dict) or inner.get("isError") or inner.get("error") \
                    or "__http_error__" in inner or "__raw__" in inner:
                continue
            src_text = read_content_text(inner)
            if not src_text or not src_text.strip():
                continue  # empty Source part (atomic-created probe)
            candidate_args = {
                "kb": args.alias,
                "name": cand,
                "part": "Source",
                "mode": "full",
                # Real change (append a marker comment line): exercises the full
                # read -> write -> project -> diff pipeline. mode=full needs no
                # byte-exact context matching (mode=patch NoMatches when the read
                # view differs from the patch view). dryRun: nothing persists.
                "content": src_text.rstrip() + "\n// gxbench-dryrun",
                "dryRun": True,
            }
            if cand_type:
                # Explicit type: without it the gateway auto-injects type="Table"
                # for a Transaction, which resolves to the table object (no Source
                # part) and fails the write read.
                candidate_args["type"] = cand_type
            # Verify the dryRun write succeeds on this object before pinning it —
            # a candidate whose write path fails would measure error envelopes.
            el, inner = rpc(session_id, "tools/call", {
                "name": "genexus_edit",
                "arguments": candidate_args,
            }, timeout=180)
            if envelope_is_ok(inner):
                edit_args = candidate_args
                print(f"edit_dryrun target: {cand} (mode=full +marker, dryRun verified, no persist)")
                break
        if not edit_args:
            print("WARN: no edit target with a verifiable dryRun write; skipping edit_dryrun")

    n = args.iterations
    results = {}
    label = args.name or "baseline"
    population = {
        "fixtureId": args.fixture_id or "",
        "fixtureRevision": args.fixture_revision or "",
        "kbAlias": args.alias,
        "kbPath": args.kb,
        "generator": args.generator or "",
        "cacheMode": args.cache_mode,
        "concurrency": args.concurrency,
        "iterations": n,
        "ops": list(ops),
    }

    def run_op(label, build_args):
        samples = []
        byte_samples = []
        content_byte_samples = []
        structured_byte_samples = []
        token_samples = []
        failed = 0
        for i in range(n):
            args_dict = build_args(i)
            try:
                measurement = rpc(session_id, "tools/call", {
                    "name": args_dict["name"],
                    "arguments": args_dict["arguments"],
                }, timeout=120)
            except (OSError, TimeoutError):
                failed += 1
                continue
            el, envelope = measurement
            if operation_envelope_is_ok(label, envelope):
                samples.append(el)
                response_bytes = getattr(measurement, "response_bytes", 0)
                # A patched/local probe that still returns the historical
                # two-item tuple has no wire-size metadata.  Do not turn that
                # absence into a misleading zero-byte sample; successful HTTP
                # responses always have a non-empty JSON body.
                if isinstance(response_bytes, (int, float)) and response_bytes > 0:
                    byte_samples.append(response_bytes)
                for target, value in ((content_byte_samples, getattr(measurement, "content_bytes", 0)),
                                      (structured_byte_samples, getattr(measurement, "structured_content_bytes", 0)),
                                      (token_samples, getattr(measurement, "estimated_tokens", 0))):
                    if isinstance(value, int) and value >= 0:
                        target.append(value)
            else:
                failed += 1
        agg(label, samples, results, byte_samples)
        for key, values in (("contentBytes", content_byte_samples),
                            ("structuredContentBytes", structured_byte_samples),
                            ("estimatedTokens", token_samples)):
            if values:
                results[label][key] = {
                    "n": len(values),
                    "p50": round(percentile(values, 50), 2),
                    "p95": round(percentile(values, 95), 2),
                    "avg": round(statistics.mean(values), 2),
                    "samples": values,
                }
        results[label].update(attempted=n, succeeded=len(samples), failed=failed, skipped=0)

    if "whoami" in ops:
        run_op("whoami", lambda i: {"name": "genexus_whoami", "arguments": {"kb": args.alias}})
    if "kb_list" in ops:
        run_op("kb_list", lambda i: {"name": "genexus_kb", "arguments": {"action": "list"}})
    if "kb_select" in ops:
        run_op("kb_select", lambda i: {"name": "genexus_kb", "arguments": {
            "action": "select", "alias": args.alias
        }})
    if "list_objects" in ops:
        run_op("list_objects", lambda i: {"name": "genexus_list_objects", "arguments": {"kb": args.alias, "limit": 10}})
    if "query" in ops:
        run_op("query", lambda i: {"name": "genexus_query", "arguments": {"kb": args.alias, "query": "Trn", "limit": 10}})
    if "search_source" in ops:
        # NOTE: genexus_search_source takes `pattern`/`callee` — NOT `query` (that
        # arg yields the MissingCriteria error path, ~15ms flat, and the harness
        # previously measured that instead of the real search).
        run_op("search_source", lambda i: {"name": "genexus_search_source", "arguments": {"kb": args.alias, "pattern": "parm", "maxResults": 10}})
    if "inspect" in ops:
        run_op("inspect", lambda i: {"name": "genexus_inspect", "arguments": {
            "kb": args.alias, **target_entries[i % len(target_entries)]
        }})
    if "read" in ops:
        run_op("read", lambda i: {"name": "genexus_read", "arguments": {
            "kb": args.alias, **target_entries[i % len(target_entries)], "part": "Source", "limit": 0
        }})

    # Issue #358: interactive-read latency under background load. Only meaningful once a
    # second session exists to carry the load, so at --clients 1 it is declared
    # unavailable rather than reported as a healthy zero.
    if load_sessions:
        print("\nmeasuring read latency under background load...", flush=True)
        under_load = measure_read_under_load(
            session_id, load_sessions[0], args.alias, target_entries, results, n)
        if under_load["n"]:
            under_load.update(attempted=n, succeeded=under_load["n"],
                              failed=n - under_load["n"], skipped=0)
            results["read_under_load"] = under_load
        else:
            results["read_under_load"] = {
                "n": 0, "samples": [], "attempted": n, "succeeded": 0,
                "failed": n, "skipped": 0,
                "unavailableReason": "no_valid_read_under_load",
                "responseBytes": {"n": 0, "samples": []}}
    elif args.clients > 1:
        print("read_under_load UNAVAILABLE (no load session)")
    if "edit_dryrun" in ops:
        if edit_args is None:
            print("  edit_dryrun SKIPPED (no edit target prepared)")
            results["edit_dryrun"] = {"n": 0, "samples": [], "attempted": 0,
                                      "succeeded": 0, "failed": 0, "skipped": n,
                                      "responseBytes": {"n": 0, "samples": []}}
        else:
            run_op("edit_dryrun", lambda i: {"name": "genexus_edit", "arguments": dict(edit_args)})
    if "analyze" in ops:
        # mode=summary — mode=impact's caller-walk exceeds the gateway's ~50s
        # sync cap on tracked ops without a progress token, returning 'Gateway
        # timeout' envelopes while the op keeps running in the worker (and
        # serializes everything behind it). summary is the measurable,
        # still-SDK-backed analyze path.
        run_op("analyze", lambda i: {"name": "genexus_analyze", "arguments": {
            "kb": args.alias, **target_entries[i % len(target_entries)], "mode": "summary"
        }})
    if "lifecycle_status" in ops:
        run_op("lifecycle_status", lambda i: {"name": "genexus_lifecycle", "arguments": {"kb": args.alias, "action": "status"}})
    if "graph" in ops:
        run_op("graph", lambda i: {"name": "genexus_doc", "arguments": {
            "action": "visualize", "target": names[i % len(names)]
        }})
    if "design_system" in ops:
        run_op("design_system", lambda i: {"name": "genexus_layout", "arguments": {
            "kb": args.alias, "action": "design_system", "name": names[i % len(names)]
        }})
    if "pattern_diagnose" in ops:
        run_op("pattern_diagnose", lambda i: {"name": "genexus_apply_pattern", "arguments": {
            "kb": args.alias, "name": names[i % len(names)], "pattern": "WorkWithPlus",
            "mode": "diagnose", "dryRun": True
        }})

    requested_failed = (set(results) != set(ops)
                        or any(r["failed"] or r["skipped"] for r in results.values()))

    # Issue #358: resolve the executed cell before writing, so the report on disk says
    # what happened rather than only what was planned. read_under_load is excluded from
    # the failure check: it is an extra measurement, not a requested op, and a fixture
    # with no readable Source target would otherwise fail the whole run.
    if matrix is not None:
        under_load = results.get("read_under_load")
        cell_failed = bool(requested_failed) or (under_load is not None
                                                 and under_load.get("n", 0) == 0)
        for cell in matrix["cells"]:
            if cell["cellId"] != matrix["executedCellId"]:
                continue
            if cell["state"] == "unavailable":
                continue  # already decided by availability or a failed client
            cell["state"] = "fail" if cell_failed else "pass"
            cell["reason"] = None if not cell_failed else "operations_failed"
        matrix["summary"] = summarize_cells(matrix["cells"])
        print("\n=== SCALE MATRIX (final) ===")
        for cell in matrix["cells"]:
            print(f"  {cell['cellId']:20s} {cell['state']:12s} {cell['reason'] or ''}")
        print("  matrix outcome: " + matrix["summary"]["outcome"].upper())

    out = {"timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
           "label": label, "kb": args.kb, "iterations": n, "ops": results,
           "opsOrder": ops, "population": population}
    if matrix is not None:
        # Issue #358: record the matrix axes alongside the population so a comparison
        # against a prior run cannot silently pair a 1-KB baseline with a 3-KB current.
        out["matrix"] = matrix
        out["population"] = dict(population, kbs=len(kb_specs), clients=args.clients)
    if args.out:
        _write_report(out, args.out)
    if requested_failed:
        print("FATAL: requested operations failed or were skipped")
        return 1
    # A matrix run that could not execute its declared cell is unavailable, which the
    # harness reports as exit 2 rather than as a red failure: a missing fixture is not
    # a regression, and conflating them is what makes a gate get ignored.
    if matrix is not None and matrix["summary"]["outcome"] == "unavailable":
        print("FATAL: matrix cell(s) unavailable — this run is not a pass")
        return 2
    if args.compare:
        try:
            with open(args.compare, "r", encoding="utf-8") as f:
                baseline = json.load(f)
        except Exception as ex:
            print(f"\nWARN: could not load baseline {args.compare}: {ex}")
            if args.fail_on_regression:
                return 2
        else:
            regressions = print_comparison(baseline, out, args.max_p50_regression,
                                           args.max_p95_regression, args.max_bytes_regression)
            if args.fail_on_regression:
                if regressions is None:
                    print("FATAL: performance comparison has no overlapping operations")
                    return 2
                if regressions:
                    return 1
    print("\n=== DONE ===")
    return 0


if __name__ == "__main__":
    sys.exit(main())
