using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Collects the Guid of every object the SDK effectively deletes while it is alive.
    /// KBObject.Delete() cascades (Transaction -> Table -> Index/attributes), and the
    /// manager raises AfterDeleteKBObject once per object in that cascade set. The event
    /// lives on the internal runtime manager, not on the IKBObjectManager interface, so
    /// it is subscribed by reflection. Missing event or failed subscription leaves the
    /// capture inactive; it never breaks the delete.
    /// </summary>
    internal sealed class DeletedObjectCapture : IDisposable
    {
        private const string EventName = "AfterDeleteKBObject";
        private const string BeforeEventName = "BeforeDeleteKBObject";

        private readonly object _lock = new object();
        private readonly List<Guid> _guids = new List<Guid>();
        private readonly List<Guid> _candidates = new List<Guid>();
        private readonly Func<object, IEnumerable<Guid>> _dependentsOf;
        private readonly List<KeyValuePair<EventInfo, Delegate>> _subscriptions = new List<KeyValuePair<EventInfo, Delegate>>();
        private readonly object _source;

        /// <param name="dependentsOf">
        /// Optional: given the object about to be deleted (BeforeDeleteKBObject), returns the guids of
        /// objects the SDK deletes with it WITHOUT raising their own event (e.g. a Table's Indexes).
        /// They are only candidates; the caller must confirm they are gone after the commit.
        /// </param>
        public DeletedObjectCapture(object objectManager, Func<object, IEnumerable<Guid>> dependentsOf = null)
        {
            _dependentsOf = dependentsOf;
            _source = objectManager;
            if (objectManager == null) return;
            Subscribe(EventName, "OnRaised");
            if (dependentsOf != null) Subscribe(BeforeEventName, "OnBefore");
        }

        private void Subscribe(string eventName, string methodName)
        {
            try
            {
                var ev = _source.GetType().GetEvent(eventName, BindingFlags.Public | BindingFlags.Instance);
                if (ev == null || ev.EventHandlerType == null)
                {
                    Logger.Debug("DeletedObjectCapture: event " + eventName + " not found on " + _source.GetType().FullName);
                    return;
                }

                // (object sender, TArgs e) => method(e), built for the event's exact delegate type.
                var ps = ev.EventHandlerType.GetMethod("Invoke").GetParameters();
                if (ps.Length != 2) return;
                var sender = Expression.Parameter(ps[0].ParameterType, "sender");
                var args = Expression.Parameter(ps[1].ParameterType, "e");
                var call = Expression.Call(
                    Expression.Constant(this),
                    typeof(DeletedObjectCapture).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance),
                    Expression.Convert(args, typeof(object)));
                var handler = Expression.Lambda(ev.EventHandlerType, call, sender, args).Compile();

                ev.AddEventHandler(_source, handler);
                _subscriptions.Add(new KeyValuePair<EventInfo, Delegate>(ev, handler));
            }
            catch (Exception ex)
            {
                Logger.Debug("DeletedObjectCapture: subscription to " + eventName + " failed: " + ex.Message);
            }
        }

        public bool IsActive { get { return _subscriptions.Count > 0; } }

        /// <summary>Dependents of objects seen in BeforeDeleteKBObject; unconfirmed candidates.</summary>
        public IReadOnlyList<Guid> Candidates
        {
            get { lock (_lock) return _candidates.ToArray(); }
        }

        public IReadOnlyList<Guid> Guids
        {
            get { lock (_lock) return _guids.ToArray(); }
        }

        private void OnBefore(object args)
        {
            try
            {
                if (args == null) return;
                var prop = args.GetType().GetProperty("KBObject", BindingFlags.Public | BindingFlags.Instance);
                var obj = prop == null ? null : prop.GetValue(args, null);
                if (obj == null) return;
                var deps = _dependentsOf(obj);
                if (deps == null) return;
                lock (_lock)
                    foreach (var g in deps)
                        if (g != Guid.Empty) _candidates.Add(g);
            }
            catch (Exception ex)
            {
                Logger.Debug("DeletedObjectCapture: before-handler failed: " + ex.Message);
            }
        }

        private void OnRaised(object args)
        {
            try
            {
                if (args == null) return;
                var prop = args.GetType().GetProperty("Guid", BindingFlags.Public | BindingFlags.Instance);
                if (prop == null || prop.PropertyType != typeof(Guid)) return;
                var guid = (Guid)prop.GetValue(args, null);
                if (guid == Guid.Empty) return;
                lock (_lock) _guids.Add(guid);
            }
            catch (Exception ex)
            {
                Logger.Debug("DeletedObjectCapture: handler failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            var subs = _subscriptions.ToArray();
            _subscriptions.Clear();
            foreach (var sub in subs)
            {
                try { sub.Key.RemoveEventHandler(_source, sub.Value); }
                catch (Exception ex) { Logger.Debug("DeletedObjectCapture: unsubscribe failed: " + ex.Message); }
            }
        }
    }
}
