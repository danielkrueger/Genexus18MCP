using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxMcp.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// The KB-selection policy has one implementation, so the probe an agent runs first
    /// (<c>genexus_whoami</c>) and the resolution the next call performs
    /// (<c>KbResolver.Resolve</c>) cannot disagree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These tests drive <b>both</b> callers over the same state — whoami through
    /// <c>ConfigureRouteStateForTest</c> (so it reads the same config and pool the
    /// resolver is handed) and the resolver directly — then compare. Asserting each in
    /// isolation would be worthless here: the defect being guarded is precisely that two
    /// implementations each produced a plausible answer of their own.
    /// </para>
    /// <para>
    /// The two divergences this closes, both observed before the change:
    /// a <c>known</c> alias overwriting an <c>open</c> one in whoami's alias map (last
    /// write wins, while the resolver prefers <c>open</c>), and whoami reporting
    /// <c>selectionState=valid</c> in legacy mode for a configured default that cannot
    /// resolve, where the next call raises <c>KB_NOT_FOUND</c>.
    /// </para>
    /// </remarks>
    [Collection("Gateway route state")]
    public sealed class KbSelectionAgreementTests
    {
        // ---------------------------------------------------------------------
        // 1. The agreement table.
        // ---------------------------------------------------------------------

        /// <summary>
        /// Every reachable selection state, checked from both sides at once.
        /// </summary>
        /// <remarks>
        /// Each row names the state the next call will produce, and asserts the whoami
        /// projection carries the same <c>selectionSource</c>, the same
        /// <c>selectionState</c>, and the same resolved alias the resolver returns — or,
        /// when the resolver throws, that whoami reports it unresolved with the same
        /// error code. Reintroducing a second decision tree in whoami fails the row whose
        /// state the two copies answer differently.
        /// </remarks>
        [Fact]
        public void WhoamiAndResolveAgreeOnEveryReachableState()
        {
            var states = new[]
            {
                // ---- explicit argument wins over everything ----
                new State(
                    "explicit-arg beats a session selection",
                    policy: "strict", startupDefault: "customer",
                    declared: Kbs(("customer", "C:/KB/Customer"), ("orders", "C:/KB/Orders")),
                    open: Kbs(("orders", "C:/KB/Orders")),
                    sessionSelection: "customer",
                    kbArg: "customer",
                    expectResolved: true, expectSource: "explicit-arg", expectState: "valid",
                    expectAlias: "customer"),

                new State(
                    "an unknown explicit argument is reported, not swallowed",
                    policy: "strict", startupDefault: "customer",
                    declared: Kbs(("customer", "C:/KB/Customer")),
                    open: Kbs(("customer", "C:/KB/Customer")),
                    sessionSelection: null,
                    kbArg: "nope",
                    expectResolved: false, expectSource: "explicit-arg", expectState: "invalid",
                    expectAlias: null, expectErrorCode: "KB_NOT_FOUND"),

                // ---- MCP-session selection ----
                new State(
                    "a valid session selection wins over the policy",
                    policy: "strict", startupDefault: "customer",
                    declared: Kbs(("customer", "C:/KB/Customer"), ("orders", "C:/KB/Orders")),
                    open: Kbs(("customer", "C:/KB/Customer")),
                    sessionSelection: "orders",
                    kbArg: null,
                    expectResolved: true, expectSource: "session-select", expectState: "valid",
                    expectAlias: "orders"),

                new State(
                    "an invalid session selection is invalid, with no fallback",
                    policy: "strict", startupDefault: "customer",
                    declared: Kbs(("customer", "C:/KB/Customer")),
                    open: Kbs(("customer", "C:/KB/Customer")),
                    sessionSelection: "ghost",
                    kbArg: null,
                    expectResolved: false, expectSource: "session-select", expectState: "invalid",
                    expectAlias: "ghost", expectErrorCode: "KB_SELECTION_INVALID"),

                // ---- strict policy ----
                new State(
                    "strict with nothing open requires context",
                    policy: "strict", startupDefault: null,
                    declared: Kbs(("customer", "C:/KB/Customer")),
                    open: null,
                    sessionSelection: null,
                    kbArg: null,
                    expectResolved: false, expectSource: "none", expectState: "absent",
                    expectAlias: null, expectErrorCode: "KB_CONTEXT_REQUIRED"),

                new State(
                    "strict with one open KB selects it",
                    policy: "strict", startupDefault: null,
                    declared: Kbs(("customer", "C:/KB/Customer"), ("orders", "C:/KB/Orders")),
                    open: Kbs(("orders", "C:/KB/Orders")),
                    sessionSelection: null,
                    kbArg: null,
                    expectResolved: true, expectSource: "single-open", expectState: "valid",
                    expectAlias: "orders"),

                new State(
                    "strict with two open KBs is ambiguous",
                    policy: "strict", startupDefault: null,
                    declared: Kbs(("customer", "C:/KB/Customer"), ("orders", "C:/KB/Orders")),
                    open: Kbs(("customer", "C:/KB/Customer"), ("orders", "C:/KB/Orders")),
                    sessionSelection: null,
                    kbArg: null,
                    expectResolved: false, expectSource: "none", expectState: "absent",
                    expectAlias: null, expectErrorCode: "KB_AMBIGUOUS"),

                new State(
                    "strict, one open KB conflicting with the startup default is conflicting",
                    policy: "strict", startupDefault: "customer",
                    declared: Kbs(("customer", "C:/KB/Customer"), ("orders", "C:/KB/Orders")),
                    open: Kbs(("orders", "C:/KB/Orders")),
                    sessionSelection: null,
                    kbArg: null,
                    expectResolved: false, expectSource: "none", expectState: "conflicting",
                    expectAlias: null, expectErrorCode: "KB_CONTEXT_REQUIRED"),

                new State(
                    "strict, one open KB matching the startup default still selects it",
                    policy: "strict", startupDefault: "orders",
                    declared: Kbs(("customer", "C:/KB/Customer"), ("orders", "C:/KB/Orders")),
                    open: Kbs(("orders", "C:/KB/Orders")),
                    sessionSelection: null,
                    kbArg: null,
                    expectResolved: true, expectSource: "single-open", expectState: "valid",
                    expectAlias: "orders"),

                // ---- legacy policy ----
                new State(
                    // Two KBs open, so the single-open shortcut does not apply and the
                    // configured default is consulted on its own terms. Pinned because a
                    // default that is *not* open and exactly one KB *is* open is a
                    // different answer (single-open) — see the row below.
                    "legacy with a resolvable default selects the default",
                    policy: "legacy", startupDefault: "orders",
                    declared: Kbs(("customer", "C:/KB/Customer"), ("orders", "C:/KB/Orders")),
                    open: Kbs(("customer", "C:/KB/Customer"), ("orders", "C:/KB/Orders")),
                    sessionSelection: null,
                    kbArg: null,
                    expectResolved: true, expectSource: "config-default", expectState: "valid",
                    expectAlias: "orders"),

                new State(
                    // A default that is not open, with exactly one other KB open, takes the
                    // single-open shortcut instead — the resolver's documented order, and
                    // a case the two implementations could have read differently.
                    "legacy with an unopen default and one open KB takes single-open",
                    policy: "legacy", startupDefault: "orders",
                    declared: Kbs(("customer", "C:/KB/Customer"), ("orders", "C:/KB/Orders")),
                    open: Kbs(("customer", "C:/KB/Customer")),
                    sessionSelection: null,
                    kbArg: null,
                    expectResolved: true, expectSource: "single-open", expectState: "valid",
                    expectAlias: "customer"),

                new State(
                    "legacy with one open KB falls back to single-open",
                    policy: "legacy", startupDefault: null,
                    declared: Kbs(("customer", "C:/KB/Customer"), ("orders", "C:/KB/Orders")),
                    open: Kbs(("orders", "C:/KB/Orders")),
                    sessionSelection: null,
                    kbArg: null,
                    expectResolved: true, expectSource: "single-open", expectState: "valid",
                    expectAlias: "orders"),

                new State(
                    "legacy with nothing open falls back to the first declared KB",
                    policy: "legacy", startupDefault: null,
                    declared: Kbs(("customer", "C:/KB/Customer"), ("orders", "C:/KB/Orders")),
                    open: null,
                    sessionSelection: null,
                    kbArg: null,
                    expectResolved: true, expectSource: "declared-first", expectState: "valid",
                    expectAlias: "customer"),

                // ---- the state that used to diverge ----
                new State(
                    "legacy with an UNRESOLVABLE default reports absent, not a substitute KB",
                    policy: "legacy", startupDefault: "vanished",
                    declared: Kbs(("customer", "C:/KB/Customer"), ("orders", "C:/KB/Orders")),
                    open: null,
                    sessionSelection: null,
                    kbArg: null,
                    expectResolved: false, expectSource: "none", expectState: "absent",
                    expectAlias: null, expectErrorCode: "KB_NOT_FOUND"),

                new State(
                    // The resolver consults the single open KB before the declared and
                    // known lookups, so an unresolvable default with exactly one KB open
                    // resolves to that KB rather than failing. whoami's own order differed
                    // here too, which is why the row exists.
                    "legacy with an unresolvable default and one open KB takes single-open",
                    policy: "legacy", startupDefault: "vanished",
                    declared: Kbs(("customer", "C:/KB/Customer")),
                    open: Kbs(("customer", "C:/KB/Customer")),
                    sessionSelection: null,
                    kbArg: null,
                    expectResolved: true, expectSource: "single-open", expectState: "valid",
                    expectAlias: "customer"),

                new State(
                    // Same unresolvable default, but two KBs open so the single-open
                    // shortcut cannot apply: now nothing satisfies the default and the
                    // call fails. This is the row that used to be reported as `valid`.
                    "legacy with an unresolvable default and two open KBs is not found",
                    policy: "legacy", startupDefault: "vanished",
                    declared: Kbs(("customer", "C:/KB/Customer")),
                    open: Kbs(("customer", "C:/KB/Customer"), ("orders", "C:/KB/Orders")),
                    sessionSelection: null,
                    kbArg: null,
                    expectResolved: false, expectSource: "none", expectState: "absent",
                    expectAlias: null, expectErrorCode: "KB_NOT_FOUND"),

                new State(
                    // A default whose alias is both open and known, carrying different
                    // handles. The open one wins, so the answer names a path, not just an
                    // alias — which is what makes the map-ordering regression visible here
                    // as well as in the dedicated test.
                    "legacy default that is both open and known resolves the open handle",
                    policy: "legacy", startupDefault: "shared",
                    declared: Kbs(("customer", "C:/KB/Customer")),
                    open: Kbs(("shared", "C:/KB/Live")),
                    knownShadow: Kbs(("shared", "C:/KB/Stale")),
                    sessionSelection: null,
                    kbArg: null,
                    expectResolved: true, expectSource: "config-default", expectState: "valid",
                    expectAlias: "shared", expectPath: "C:/KB/Live"),

                new State(
                    "legacy with no default, nothing open and nothing declared is ambiguous",
                    policy: "legacy", startupDefault: null,
                    declared: null,
                    open: null,
                    sessionSelection: null,
                    kbArg: null,
                    expectResolved: false, expectSource: "none", expectState: "absent",
                    expectAlias: null, expectErrorCode: "KB_AMBIGUOUS"),
            };

            foreach (var state in states)
            {
                using var fixture = SelectionFixture.Create(state);
                string because = $" [{state.Name}]";

                // (a) The two resolver surfaces agree with each other: the non-throwing
                //     describe is what the throwing path is expressed in terms of, so a
                //     caller reporting it cannot describe a different decision than the
                //     one a call will enforce.
                var described = fixture.Describe(state.KbArg, state.SessionSelection);
                var resolved = fixture.ResolveOrNull(state.KbArg, state.SessionSelection);

                Assert.Equal(state.ExpectedResolved, resolved != null);
                Assert.Equal(state.ExpectedSource, described.SelectionSource);
                if (resolved != null)
                {
                    Assert.Equal("valid", described.SelectionState);
                    Assert.Null(described.ErrorCode);
                    Assert.False(described.ContextRequired);
                    Assert.Equal(state.ExpectedAlias, resolved.Alias);
                    Assert.Equal(resolved.Alias, described.Handle!.Alias);
                    Assert.Equal(resolved.Path, described.Handle!.Path);
                    if (state.ExpectedPath != null)
                        Assert.Equal(state.ExpectedPath, resolved.Path);
                }
                else
                {
                    Assert.Equal(state.ExpectedState, described.SelectionState);
                    Assert.Equal(state.ExpectedErrorCode, described.ErrorCode);
                    Assert.True(described.ContextRequired);
                }

                // (b) whoami reports that same decision. whoami has no `kb` argument of
                //     its own — the next call's explicit alias is invisible to a probe —
                //     so those rows compare the resolver surfaces only.
                if (state.KbArg != null) continue;

                var kb = fixture.WhoamiKb();
                Assert.Equal(described.SelectionSource, Text(kb, "selectionSource"));
                Assert.Equal(described.SelectionState, Text(kb, "selectionState"));
                Assert.Equal(described.ErrorCode, Text(kb, "selectionErrorCode"));
                // An unresolved session selection stays visible as `active`: it is the
                // alias that has to be corrected, so whoami names it rather than
                // reporting nothing.
                Assert.Equal(resolved?.Alias ?? state.SessionSelection, Text(kb, "active"));
                Assert.Equal(resolved?.Path, Text(kb, "path"));
                // whoami may tighten contextRequired (a resolved session selection still
                // needs an active lease) but must never loosen the resolver's verdict.
                if (described.ContextRequired)
                {
                    Assert.True(
                        kb["contextRequired"]?.ToObject<bool>() == true,
                        "whoami reported no context required for an unresolved selection" + because);
                }
            }
        }

        // ---------------------------------------------------------------------
        // 2. The map-ordering divergence.
        // ---------------------------------------------------------------------

        /// <summary>
        /// An alias that is both <c>known</c> and <c>open</c> resolves to the
        /// <c>open</c> handle in both callers, on both routes that used to read the
        /// shadowed map.
        /// </summary>
        /// <remarks>
        /// whoami used to build an alias map declared → open → known and let the last
        /// write win, so a durable <c>known</c> record — which survives a worker recycle
        /// and can carry a stale path — silently replaced the live one. Only the two
        /// branches that read that map were affected: the session selection, and the
        /// legacy configured default. Both are exercised here, with deliberately
        /// different paths so the assertion can tell the two handles apart.
        /// </remarks>
        [Fact]
        public void AKnownAliasDoesNotShadowAnOpenOne()
        {
            const string alias = "shared";

            // (a) legacy configured default: the default is open, and its `known` record
            //     points somewhere else.
            using (var viaDefault = SelectionFixture.Create(new State(
                "legacy default that is both open and known",
                policy: "legacy", startupDefault: alias,
                declared: null,
                open: null,
                sessionSelection: null,
                kbArg: null,
                expectResolved: false, expectSource: "none", expectState: "absent",
                expectAlias: null)))
            {
                OpenThenShadow(viaDefault, alias);

                var resolved = new KbResolver(viaDefault.Config)
                    .Resolve(null, viaDefault.Pool.ListOpen(), viaDefault.Pool.ListKnown());
                Assert.Equal("C:/KB/Live", resolved.Path);

                var kb = viaDefault.WhoamiKb();
                Assert.Equal("config-default", Text(kb, "selectionSource"));
                Assert.Equal(alias, Text(kb, "active"));
                Assert.Equal("C:/KB/Live", Text(kb, "path"));
            }

            // (b) MCP-session selection: the selected alias is open, and its `known`
            //     record points somewhere else.
            using (var viaSession = SelectionFixture.Create(new State(
                "session selection that is both open and known",
                policy: "strict", startupDefault: null,
                declared: null,
                open: null,
                sessionSelection: alias,
                kbArg: null,
                expectResolved: false, expectSource: "none", expectState: "absent",
                expectAlias: null)))
            {
                OpenThenShadow(viaSession, alias);

                var resolved = new KbResolver(viaSession.Config)
                    .Resolve(null, viaSession.Pool.ListOpen(), viaSession.Pool.ListKnown(),
                        viaSession.SessionSelection);
                Assert.Equal("C:/KB/Live", resolved.Path);

                var kb = viaSession.WhoamiKb();
                Assert.Equal("session-select", Text(kb, "selectionSource"));
                Assert.Equal(alias, Text(kb, "active"));
                Assert.Equal("C:/KB/Live", Text(kb, "path"));
            }
        }

        /// <summary>
        /// Registers <paramref name="alias"/> as open with a live worker at
        /// <c>C:/KB/Live</c>, then overwrites its durable <c>known</c> record with a
        /// stale handle at <c>C:/KB/Stale</c> — the state a worker recycle leaves.
        /// </summary>
        private static void OpenThenShadow(SelectionFixture fixture, string alias)
        {
            var open = new KbHandle(alias, "C:/KB/Live");
            fixture.Pool.RegisterForTest(open, worker: new WorkerProcess(fixture.Config, open));
            fixture.Pool.RegisterKnown(new KbHandle(alias, "C:/KB/Stale"));

            Assert.Single(fixture.Pool.ListOpen());
            Assert.Equal("C:/KB/Stale", fixture.Pool.ListKnown().Single().Path);
        }

        // ---------------------------------------------------------------------
        // 3. The silent fall-through.
        // ---------------------------------------------------------------------

        /// <summary>
        /// A configured default that cannot resolve is reported as unresolved — not
        /// replaced by the single open KB or the first declared one.
        /// </summary>
        /// <remarks>
        /// Before the change whoami fell through to <c>single-open</c> /
        /// <c>declared-first</c> here and answered <c>selectionState=valid</c>, so the
        /// probe said "fine" and the next call raised <c>KB_NOT_FOUND</c>. The state is
        /// <c>absent</c> — nothing is selected — using the existing published
        /// vocabulary rather than a new value, with the code surfaced in-band.
        /// </remarks>
        [Fact]
        public void AnUnresolvableLegacyDefaultIsReportedRatherThanFallingThrough()
        {
            var state = new State(
                "legacy default points at a KB that is not declared, open, or known",
                policy: "legacy", startupDefault: "vanished",
                declared: Kbs(("customer", "C:/KB/Customer"), ("orders", "C:/KB/Orders")),
                open: null,
                sessionSelection: null,
                kbArg: null,
                expectResolved: false, expectSource: "none", expectState: "absent",
                expectAlias: null, expectErrorCode: "KB_NOT_FOUND");

            using var fixture = SelectionFixture.Create(state);

            // The next call, verbatim.
            var ex = Assert.Throws<KbResolutionException>(
                () => new KbResolver(fixture.Config).Resolve(null, fixture.Pool.ListOpen(), fixture.Pool.ListKnown()));
            Assert.Equal("KB_NOT_FOUND", ex.Code);

            var kb = fixture.WhoamiKb();
            Assert.NotEqual("valid", Text(kb, "selectionState"));
            Assert.Equal("absent", Text(kb, "selectionState"));
            Assert.Equal("KB_NOT_FOUND", Text(kb, "selectionErrorCode"));
            Assert.True(kb["contextRequired"]?.ToObject<bool>());
            Assert.Null(Text(kb, "active"));
            Assert.Equal("vanished", Text(kb, "startupDefault"));
        }

        // ---------------------------------------------------------------------
        // 4. The published vocabulary.
        // ---------------------------------------------------------------------

        /// <summary>
        /// The <c>selectionSource</c> / <c>selectionState</c> literals and the
        /// structured error codes are still exactly these, in code (comments are
        /// blanked), and whoami no longer carries a decision tree of its own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every needle includes its quotes so it matches a string literal, not a word in
        /// a message or a comment — <c>"valid"</c> would otherwise also match inside
        /// <c>"invalid"</c> and inside prose, and a guard that cannot fail is not a
        /// guard. Comments are blanked for the same reason: this file's own narrative
        /// mentions these values on purpose.
        /// </para>
        /// <para>
        /// The counts are asymmetric on purpose. Each vocabulary literal must appear at
        /// least once in the resolver (the decision exists), and the decision literals
        /// that whoami used to hard-code must now appear <b>zero</b> times there — that
        /// absence is the actual guard, and it is what a re-duplicated tree would
        /// restore. <c>"none"</c> and <c>"absent"</c> are excluded from the whoami
        /// zero-check because they are still legitimately present as the
        /// pre-resolution defaults this method falls back to when it cannot ask at all;
        /// what must not come back is the branching that assigns them.
        /// </para>
        /// </remarks>
        [Fact]
        public void ThePublishedVocabularyIsUnchanged()
        {
            string resolverCode = SourceAssert.NormaliseNewlines(
                RepoSource.WithoutComments("src", "GxMcp.Gateway", "KbResolver.cs"));
            string whoamiCode = SourceAssert.NormaliseNewlines(
                RepoSource.WithoutComments("src", "GxMcp.Gateway", "Program.Whoami.cs"));

            // Every published selectionSource survives, in the decision itself.
            foreach (var source in new[]
            {
                "\"explicit-arg\"", "\"session-select\"", "\"single-open\"",
                "\"config-default\"", "\"declared-first\"", "\"none\"",
            })
            {
                Assert.True(
                    SourceAssert.Count(resolverCode, source) >= 1,
                    $"selectionSource {source} must still be produced by KbResolver");
            }

            // Every published selectionState survives, likewise.
            foreach (var state in new[] { "\"valid\"", "\"absent\"", "\"invalid\"", "\"conflicting\"" })
            {
                Assert.True(
                    SourceAssert.Count(resolverCode, state) >= 1,
                    $"selectionState {state} must still be produced by KbResolver");
            }

            // The frozen error codes are still the ones raised.
            foreach (var code in new[]
            {
                "\"KB_NOT_FOUND\"", "\"KB_AMBIGUOUS\"", "\"KB_CONTEXT_REQUIRED\"",
                "\"KB_SELECTION_INVALID\"", "DefaultConflict",
            })
            {
                Assert.True(
                    SourceAssert.Count(resolverCode, code) >= 1,
                    $"error code {code} must still be produced by KbResolver");
            }

            // whoami projects; it does not decide. Each of these was assigned by whoami's
            // own inline tree before, and none may be assigned there again.
            foreach (var decided in new[]
            {
                "\"explicit-arg\"", "\"session-select\"", "\"single-open\"",
                "\"config-default\"", "\"declared-first\"", "\"conflicting\"", "\"valid\"",
            })
            {
                Assert.True(
                    SourceAssert.Count(whoamiCode, decided) == 0,
                    $"whoami must not produce {decided} itself; it must project the resolver's decision");
            }

            // whoami asks the resolver exactly once, and reads the decision off it.
            Assert.Equal(1, SourceAssert.Count(whoamiCode, ".Describe("));
            Assert.Equal(1, SourceAssert.Count(whoamiCode, "selection.SelectionSource"));
            Assert.Equal(1, SourceAssert.Count(whoamiCode, "selection.SelectionState"));
        }

        // ---------------------------------------------------------------------
        // Harness
        // ---------------------------------------------------------------------

        /// <summary>
        /// Alias/path pairs, in the array shape a <see cref="State"/> row declares.
        /// </summary>
        /// <remarks>
        /// A helper rather than inline literals because C# takes a bare
        /// <c>(alias, path), (alias, path)</c> at an argument position as one tuple —
        /// a compile error at each row, not something to work around fifteen times.
        /// </remarks>
        private static (string alias, string path)[] Kbs(params (string alias, string path)[] kbs) => kbs;

        /// <summary>
        /// A payload field read as a C# string, with an absent value read as
        /// <c>null</c>.
        /// </summary>
        /// <remarks>
        /// Assigning a null string into a <see cref="JObject"/> does not remove the
        /// key: it stores a <see cref="JValue"/> whose <c>Value</c> is null, and its
        /// <c>ToString()</c> is the empty string. So a plain <c>token?.ToString()</c>
        /// turns "reported as absent" into <c>""</c>, and the resulting mismatch reads
        /// like a wrong value rather than like a null. Reading <c>Value</c> collapses
        /// both shapes (a missing key, and a value that is null) onto one null.
        /// </remarks>
        private static string? Text(JObject payload, string field)
            => payload[field] is JValue value && value.Value == null
                ? null
                : payload[field]?.ToString();

        /// <summary>One reachable selection state, as inputs plus the answer to expect.</summary>
        private sealed class State
        {
            internal State(
                string name,
                string policy,
                string? startupDefault,
                (string alias, string path)[]? declared,
                (string alias, string path)[]? open,
                string? sessionSelection,
                string? kbArg,
                bool expectResolved,
                string expectSource,
                string expectState,
                string? expectAlias,
                string? expectErrorCode = null,
                (string alias, string path)[]? knownShadow = null,
                string? expectPath = null)
            {
                Name = name;
                Policy = policy;
                StartupDefault = startupDefault;
                Declared = declared ?? Array.Empty<(string, string)>();
                Open = open ?? Array.Empty<(string, string)>();
                KnownShadow = knownShadow ?? Array.Empty<(string, string)>();
                SessionSelection = sessionSelection;
                KbArg = kbArg;
                ExpectedResolved = expectResolved;
                ExpectedSource = expectSource;
                ExpectedState = expectState;
                ExpectedAlias = expectAlias;
                ExpectedErrorCode = expectErrorCode;
                ExpectedPath = expectPath;
            }

            internal string Name { get; }
            internal string Policy { get; }
            internal string? StartupDefault { get; }
            internal (string alias, string path)[] Declared { get; }
            internal (string alias, string path)[] Open { get; }

            /// <summary>
            /// Aliases registered as <c>known</c> <em>after</em> the open handles, so an
            /// alias in both sets carries two different paths.
            /// </summary>
            internal (string alias, string path)[] KnownShadow { get; }

            internal string? SessionSelection { get; }
            internal string? KbArg { get; }
            internal bool ExpectedResolved { get; }
            internal string ExpectedSource { get; }
            internal string ExpectedState { get; }
            internal string? ExpectedAlias { get; }
            internal string? ExpectedErrorCode { get; }
            internal string? ExpectedPath { get; }
        }

        /// <summary>
        /// Drives whoami and the resolver from one config and one pool, so the two
        /// callers see identical state. whoami reads the process-wide config and worker
        /// pool, so the fixture installs both and restores them on dispose.
        /// </summary>
        private sealed class SelectionFixture : IDisposable
        {
            private readonly IDisposable _state;
            private readonly string _directory;
            private readonly string? _sessionId;

            private SelectionFixture(
                IDisposable state, string directory, Configuration config, string? sessionId)
            {
                _state = state;
                _directory = directory;
                Config = config;
                Pool = CurrentPool();
                _sessionId = sessionId;
            }

            internal Configuration Config { get; }
            internal WorkerPool Pool { get; }

            /// <summary>The MCP session's selected alias, when this fixture set one.</summary>
            internal string? SessionSelection => _sessionId == null ? null : Program.GetSessionSelectedKb(_sessionId);

            internal static SelectionFixture Create(State state)
            {
                string directory = Path.Combine(
                    Path.GetTempPath(), "gxmcp-selection-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);

                var config = new Configuration
                {
                    Environment = new EnvironmentConfig
                    {
                        ResolutionPolicy = state.Policy,
                        DefaultKb = state.StartupDefault,
                    }
                };
                foreach (var (alias, path) in state.Declared)
                    config.Environment.KBs.Add(new KbEntry { Alias = alias, Path = path });

                var installed = Program.ConfigureRouteStateForTest(
                    config, Path.Combine(directory, "config.json"));
                var pool = CurrentPool();
                foreach (var (alias, path) in state.Open)
                {
                    var handle = new KbHandle(alias, path);
                    // A worker is required for ListOpen to report the entry as open.
                    pool.RegisterForTest(handle, worker: new WorkerProcess(config, handle));
                }
                // Registered after, so an alias in both sets ends up known with a
                // different path — the state a worker recycle leaves behind.
                foreach (var (alias, path) in state.KnownShadow)
                    pool.RegisterKnown(new KbHandle(alias, path));

                string? sessionId = null;
                if (state.SessionSelection != null)
                {
                    sessionId = "selection-agreement-" + Guid.NewGuid().ToString("N");
                    Program.SetSessionSelectedKb(sessionId, state.SessionSelection);
                }

                return new SelectionFixture(installed, directory, config, sessionId);
            }

            /// <summary>The resolver's non-throwing view of the same state.</summary>
            internal KbSelectionResult Describe(string? kbArg, string? sessionSelection)
                => new KbResolver(Config).Describe(
                    kbArg, Pool.ListOpen(), Pool.ListKnown(), sessionSelection);

            /// <summary>
            /// The next call's answer: the handle it resolves to, or null when it raises.
            /// </summary>
            internal KbHandle? ResolveOrNull(string? kbArg, string? sessionSelection)
            {
                try
                {
                    return new KbResolver(Config).Resolve(
                        kbArg, Pool.ListOpen(), Pool.ListKnown(), sessionSelection);
                }
                catch (KbResolutionException)
                {
                    return null;
                }
            }

            /// <summary>The <c>kb</c> block of the payload an agent reads first.</summary>
            internal JObject WhoamiKb()
                => (JObject)Program.BuildWhoamiPayload(false, _sessionId)["kb"]!;

            public void Dispose()
            {
                if (_sessionId != null) Program.ClearSessionSelectedKb(_sessionId);
                Pool.StopAll(WorkerStopReason.GatewayShutdown);
                _state.Dispose();
                try { Directory.Delete(_directory, true); } catch { }
            }

            private static WorkerPool CurrentPool()
            {
                var pool = Program.GetWorkerPool();
                Assert.NotNull(pool);
                return pool!;
            }
        }
    }
}