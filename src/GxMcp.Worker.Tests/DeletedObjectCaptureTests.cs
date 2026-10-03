using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class DeletedObjectCaptureTests
    {
        public sealed class FakeArgs : EventArgs
        {
            public FakeArgs(Guid guid) { Guid = guid; }
            public Guid Guid { get; }
        }

        public sealed class FakeCancelArgs : EventArgs
        {
            public FakeCancelArgs(object o) { KBObject = o; }
            public object KBObject { get; }
        }

        public sealed class FakeManager
        {
            public event EventHandler<FakeCancelArgs> BeforeDeleteKBObject;
            public int BeforeCount => BeforeDeleteKBObject?.GetInvocationList().Length ?? 0;
            public void RaiseBefore(object o) => BeforeDeleteKBObject?.Invoke(this, new FakeCancelArgs(o));

            public event EventHandler<FakeArgs> AfterDeleteKBObject;
            public int HandlerCount => AfterDeleteKBObject?.GetInvocationList().Length ?? 0;
            public void Raise(Guid g) => AfterDeleteKBObject?.Invoke(this, new FakeArgs(g));
        }

        [Fact]
        public void Collects_Guids_While_Alive_And_Ignores_Empty()
        {
            var mgr = new FakeManager();
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            using (var cap = new DeletedObjectCapture(mgr))
            {
                Assert.True(cap.IsActive);
                mgr.Raise(a);
                mgr.Raise(Guid.Empty);
                mgr.Raise(b);
                Assert.Equal(new[] { a, b }, cap.Guids.ToArray());
            }
        }

        [Fact]
        public void Before_Event_Collects_Dependents_As_Candidates_And_Dispose_Unsubscribes()
        {
            var mgr = new FakeManager();
            var dep = Guid.NewGuid();
            var seen = new List<object>();
            var cap = new DeletedObjectCapture(mgr, o => { seen.Add(o); return o is string ? new[] { dep, Guid.Empty } : new Guid[0]; });
            mgr.RaiseBefore("table");
            mgr.RaiseBefore(42);
            Assert.Equal(new object[] { "table", 42 }, seen.ToArray());
            Assert.Equal(new[] { dep }, cap.Candidates.ToArray());
            Assert.Empty(cap.Guids);
            cap.Dispose();
            Assert.Equal(0, mgr.BeforeCount);
            mgr.RaiseBefore("table");
            Assert.Single(cap.Candidates);
        }

        [Fact]
        public void Before_Event_Not_Subscribed_Without_Dependents_Function()
        {
            var mgr = new FakeManager();
            using (new DeletedObjectCapture(mgr)) Assert.Equal(0, mgr.BeforeCount);
        }

        [Fact]
        public void Dispose_Unsubscribes()
        {
            var mgr = new FakeManager();
            var cap = new DeletedObjectCapture(mgr);
            Assert.Equal(1, mgr.HandlerCount);
            cap.Dispose();
            Assert.Equal(0, mgr.HandlerCount);
            mgr.Raise(Guid.NewGuid());
            Assert.Empty(cap.Guids);
        }

        [Fact]
        public void Source_Without_Event_Or_Null_Is_Inactive_And_Does_Not_Throw()
        {
            foreach (var src in new object[] { new object(), null })
            {
                using (var cap = new DeletedObjectCapture(src))
                {
                    Assert.False(cap.IsActive);
                    Assert.Empty(cap.Guids);
                }
            }
        }

        [Fact]
        public void RemoveCascadedEntries_Removes_Cascade_Skips_Target_And_Reports_Them()
        {
            var target = Guid.NewGuid();
            var table = Guid.NewGuid();
            var unknown = Guid.NewGuid();
            var idx = new IndexCacheService();
            idx.Initialize(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gxmcp-cascade-" + Guid.NewGuid().ToString("N")));
            try
            {
            idx.ReplaceAll(new[]
            {
                new SearchIndex.IndexEntry { Guid = target.ToString(), Name = "SampleTrn", Type = "Transaction" },
                new SearchIndex.IndexEntry { Guid = table.ToString(), Name = "SampleTable", Type = "Table" }
            });

            var also = ObjectService.RemoveCascadedEntries(idx, new[] { target, table, table, unknown, Guid.Empty }, target);

            Assert.Single(also);
            Assert.Equal("SampleTable", (string)also[0]["name"]);
            Assert.Equal("Table", (string)also[0]["type"]);
            Assert.Null(idx.GetIndex().FindByGuid(table.ToString()));
            // Target is removed by the existing target-only path, not by the cascade step.
            Assert.NotNull(idx.GetIndex().FindByGuid(target.ToString()));
            Assert.Empty(ObjectService.RemoveCascadedEntries(idx, new Guid[0], target));

            // Candidates (e.g. a Table's index): removed only when the resolver reports them absent.
            var gone = Guid.NewGuid();
            var alive = Guid.NewGuid();
            idx.ReplaceAll(new[]
            {
                new SearchIndex.IndexEntry { Guid = gone.ToString(), Name = "ISampleTable", Type = "Index" },
                new SearchIndex.IndexEntry { Guid = alive.ToString(), Name = "ISampleOther", Type = "Index" }
            });
            var viaCandidates = ObjectService.RemoveCascadedEntries(idx, new Guid[0], target,
                new[] { gone, alive }, g => g == gone);
            Assert.Single(viaCandidates);
            Assert.Equal("ISampleTable", (string)viaCandidates[0]["name"]);
            Assert.Null(idx.GetIndex().FindByGuid(gone.ToString()));
            Assert.NotNull(idx.GetIndex().FindByGuid(alive.ToString()));
            }
            finally { idx.DeleteOnDiskSnapshot(); }
        }
    }
}
