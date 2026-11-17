// Explicit notification/logger doubles for the actual background adapter.
using System;
using System.Collections.Generic;

namespace Playnite.SDK
{
    public enum NotificationType { Info, Error }
    public sealed class NotificationMessage
    {
        public readonly string Id;
        public readonly Action Action;
        public NotificationMessage(string id, string text, NotificationType type, Action action)
        { Id = id; Action = action; }
    }
}

namespace PersonalCloudLibrarySource
{
    public sealed class TestApi { public readonly TestNotifications Notifications = new TestNotifications(); }
    public sealed class TestNotifications
    {
        private readonly Dictionary<string, Playnite.SDK.NotificationMessage> values = new Dictionary<string, Playnite.SDK.NotificationMessage>();
        public bool ThrowAdd;
        public int Count => values.Count;
        public Playnite.SDK.NotificationMessage Last { get; private set; }
        public void Add(Playnite.SDK.NotificationMessage value)
        {
            if (ThrowAdd) throw new InvalidOperationException("Labelled notification fixture failure.");
            values[value.Id] = value; Last = value;
        }
        public void Remove(string id) => values.Remove(id);
    }
    public sealed class TestLogger { public void Warn(Exception error, string message) { } }
}
