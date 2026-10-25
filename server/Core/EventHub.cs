using System;
using System.Collections.Generic;

namespace McvsOffline.Server.Core
{
    /// <summary>
    /// In-process publish/subscribe between modules that must not reference each other's files, e.g. Messaging
    /// publishing "a message was stored for account X" so Alerts can push {"type":"message","transport":"secured"}
    /// to that account's stream. Topics and payload shapes are documented in docs/MODULES.md. Handlers run
    /// synchronously on the publishing thread; a throwing handler is logged and does not affect the others.
    /// </summary>
    public sealed class EventHub
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, List<Action<object>>> handlers =
            new Dictionary<string, List<Action<object>>>(StringComparer.Ordinal);
        private readonly Log log;

        public EventHub(Log log)
        {
            this.log = log ?? Log.Null;
        }

        /// <summary>Subscribes to a topic; dispose the result to unsubscribe.</summary>
        public IDisposable Subscribe(string topic, Action<object> handler)
        {
            if (topic == null) throw new ArgumentNullException("topic");
            if (handler == null) throw new ArgumentNullException("handler");
            lock (gate)
            {
                List<Action<object>> list;
                if (!handlers.TryGetValue(topic, out list))
                {
                    list = new List<Action<object>>();
                    handlers[topic] = list;
                }
                list.Add(handler);
            }
            return new Subscription(this, topic, handler);
        }

        /// <summary>Delivers <paramref name="payload"/> to every subscriber of <paramref name="topic"/>; returns how many.</summary>
        public int Publish(string topic, object payload)
        {
            Action<object>[] current;
            lock (gate)
            {
                List<Action<object>> list;
                if (!handlers.TryGetValue(topic, out list) || list.Count == 0) return 0;
                current = list.ToArray();
            }
            foreach (var handler in current)
            {
                try
                {
                    handler(payload);
                }
                catch (Exception ex)
                {
                    log.Error("event handler for '" + topic + "' failed", ex);
                }
            }
            return current.Length;
        }

        private void Unsubscribe(string topic, Action<object> handler)
        {
            lock (gate)
            {
                List<Action<object>> list;
                if (handlers.TryGetValue(topic, out list)) list.Remove(handler);
            }
        }

        private sealed class Subscription : IDisposable
        {
            private readonly EventHub hub;
            private readonly string topic;
            private Action<object> handler;

            public Subscription(EventHub hub, string topic, Action<object> handler)
            {
                this.hub = hub;
                this.topic = topic;
                this.handler = handler;
            }

            public void Dispose()
            {
                var h = handler;
                handler = null;
                if (h != null) hub.Unsubscribe(topic, h);
            }
        }
    }

    /// <summary>
    /// Typed registry for services that one module provides and others consume (e.g. a content catalogue).
    /// Define the interface in the providing module's folder; consumers only read that file.
    /// </summary>
    public sealed class FeatureRegistry
    {
        private readonly object gate = new object();
        private readonly Dictionary<Type, object> features = new Dictionary<Type, object>();

        public void Set<T>(T feature) where T : class
        {
            if (feature == null) throw new ArgumentNullException("feature");
            lock (gate) features[typeof(T)] = feature;
        }

        public T Get<T>() where T : class
        {
            T feature;
            return TryGet(out feature) ? feature : null;
        }

        public bool TryGet<T>(out T feature) where T : class
        {
            object value;
            lock (gate)
            {
                if (features.TryGetValue(typeof(T), out value))
                {
                    feature = (T)value;
                    return true;
                }
            }
            feature = null;
            return false;
        }
    }
}
