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

        public sealed class FakeManager
        {
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
            }
            finally { idx.DeleteOnDiskSnapshot(); }
        }
    }
}
