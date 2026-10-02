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

        private readonly object _lock = new object();
        private readonly List<Guid> _guids = new List<Guid>();
        private object _source;
        private EventInfo _event;
        private Delegate _handler;

        public DeletedObjectCapture(object objectManager)
        {
            if (objectManager == null) return;
            try
            {
                var ev = objectManager.GetType().GetEvent(EventName, BindingFlags.Public | BindingFlags.Instance);
                if (ev == null || ev.EventHandlerType == null)
                {
                    Logger.Debug("DeletedObjectCapture: event " + EventName + " not found on " + objectManager.GetType().FullName);
                    return;
                }

                // (object sender, TArgs e) => OnRaised(e), built for the event's exact delegate type.
                var invoke = ev.EventHandlerType.GetMethod("Invoke");
                var ps = invoke.GetParameters();
                if (ps.Length != 2) return;
                var sender = Expression.Parameter(ps[0].ParameterType, "sender");
                var args = Expression.Parameter(ps[1].ParameterType, "e");
                var call = Expression.Call(
                    Expression.Constant(this),
                    typeof(DeletedObjectCapture).GetMethod("OnRaised", BindingFlags.NonPublic | BindingFlags.Instance),
                    Expression.Convert(args, typeof(object)));
                var handler = Expression.Lambda(ev.EventHandlerType, call, sender, args).Compile();

                ev.AddEventHandler(objectManager, handler);
                _source = objectManager;
                _event = ev;
                _handler = handler;
            }
            catch (Exception ex)
            {
                Logger.Debug("DeletedObjectCapture: subscription failed: " + ex.Message);
            }
        }

        public bool IsActive { get { return _handler != null; } }

        public IReadOnlyList<Guid> Guids
        {
            get { lock (_lock) return _guids.ToArray(); }
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
            var handler = _handler;
            if (handler == null) return;
            _handler = null;
            try { _event.RemoveEventHandler(_source, handler); }
            catch (Exception ex) { Logger.Debug("DeletedObjectCapture: unsubscribe failed: " + ex.Message); }
        }
    }
}
