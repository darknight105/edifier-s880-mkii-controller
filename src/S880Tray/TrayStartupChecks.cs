using System;
using System.Collections.Generic;
using System.Linq;

namespace S880Tray;

internal static class TrayStartupChecks
{
    internal static object Run()
    {
        var single = 0; var twice = 0;
        var scheduler = new ManualTrayGestureScheduler();
        using (var gesture = new TrayClickGesture(scheduler, TimeSpan.FromMilliseconds(550), _ => single++, () => twice++))
        {
            gesture.Click(TrayPointerButton.Left); scheduler.FireAll();
        }
        var singleClickOnly = single == 1 && twice == 0;

        single = 0; twice = 0; scheduler = new ManualTrayGestureScheduler();
        using (var gesture = new TrayClickGesture(scheduler, TimeSpan.FromMilliseconds(550), _ => single++, () => twice++))
        {
            gesture.Click(TrayPointerButton.Left);
            gesture.Click(TrayPointerButton.Left);
            gesture.DoubleClick(TrayPointerButton.Left);
            scheduler.FireAll();
        }
        var doubleCancelsSingle = single == 0 && twice == 1;

        single = 0; twice = 0; scheduler = new ManualTrayGestureScheduler();
        using (var gesture = new TrayClickGesture(scheduler, TimeSpan.FromMilliseconds(550), _ => single++, () => twice++))
        {
            gesture.Click(TrayPointerButton.Left);
            gesture.Click(TrayPointerButton.Left);
            gesture.Click(TrayPointerButton.Left);
            scheduler.FireAll();
            gesture.Click(TrayPointerButton.Right);
            gesture.DoubleClick(TrayPointerButton.Right);
            scheduler.FireAll();
        }
        var rapidClicksCoalesce = single == 1 && twice == 0;
        var rightClickNoAction = single == 1 && twice == 0;

        single = 0; twice = 0; scheduler = new ManualTrayGestureScheduler();
        var disposedGesture = new TrayClickGesture(scheduler, TimeSpan.FromMilliseconds(550), _ => single++, () => twice++);
        disposedGesture.Click(TrayPointerButton.Left); disposedGesture.Dispose(); scheduler.FireAll();
        disposedGesture.Click(TrayPointerButton.Left); disposedGesture.DoubleClick(TrayPointerButton.Left);
        var disposeCancels = single == 0 && twice == 0;

        single = 0; twice = 0; scheduler = new ManualTrayGestureScheduler();
        var queuedUi = new List<Action>();
        TrayClickGesture? queuedGesture = null;
        queuedGesture = new TrayClickGesture(scheduler, TimeSpan.FromMilliseconds(550), generation =>
            queuedUi.Add(() => { if (queuedGesture!.IsCurrent(generation)) single++; }), () => twice++);
        queuedGesture.Click(TrayPointerButton.Left);
        scheduler.FireAll();
        queuedGesture.DoubleClick(TrayPointerButton.Left);
        foreach (var action in queuedUi) action();
        var doubleCancelsAlreadyQueuedSingle = single == 0 && twice == 1;
        queuedGesture.Dispose();

        var registry = new FakeStartupRegistry();
        registry.Values["AnotherApp"] = "leave-me-alone";
        var startup = new StartupRegistration(registry, () => @"C:\Program Files\S880 Controller\S880Controller.exe");
        var initiallyOff = startup.ReadState() is { Enabled: false, Owned: false, Error: null };
        var enabled = startup.Update(true);
        var exactStartupCommand = enabled is { Enabled: true, Owned: true, Error: null } &&
            string.Equals((string)registry.Values[StartupRegistration.ValueName], "\"C:\\Program Files\\S880 Controller\\S880Controller.exe\" --start-in-tray", StringComparison.Ordinal);
        var noOtherValueTouched = string.Equals((string)registry.Values["AnotherApp"], "leave-me-alone", StringComparison.Ordinal);
        registry.Values[StartupRegistration.ValueName] = "\"C:\\Old\\S880Controller.exe\" --start-in-tray";
        var staleDetected = startup.ReadState() is { Enabled: false, Owned: true, Error: not null };
        var disabled = startup.Update(false);
        var disabledVerified = disabled is { Enabled: false, Owned: false, Error: null } && !registry.Values.ContainsKey(StartupRegistration.ValueName);

        var unreadableRegistry = new FakeStartupRegistry { ReadError = new System.IO.IOException("read denied") };
        var readFailureReported = new StartupRegistration(unreadableRegistry, () => @"C:\S880Controller.exe").ReadState().Error?.Contains("could not be read", StringComparison.Ordinal) == true;
        var unwritableRegistry = new FakeStartupRegistry { WriteError = new System.IO.IOException("write denied") };
        var writeFailureReported = new StartupRegistration(unwritableRegistry, () => @"C:\S880Controller.exe").Update(true).Error?.Contains("could not be changed", StringComparison.Ordinal) == true;

        var passed = singleClickOnly && doubleCancelsSingle && doubleCancelsAlreadyQueuedSingle && rapidClicksCoalesce && rightClickNoAction && disposeCancels && initiallyOff && exactStartupCommand && noOtherValueTouched && staleDetected && disabledVerified && readFailureReported && writeFailureReported;
        return new { passed, hardwareAccess = false, registryAccess = "fake-only", singleClickOnly, doubleCancelsSingle, doubleCancelsAlreadyQueuedSingle, rapidClicksCoalesce, rightClickNoAction, disposeCancels, initiallyOff, exactStartupCommand, noOtherValueTouched, staleDetected, disabledVerified, readFailureReported, writeFailureReported };
    }

    private sealed class ManualTrayGestureScheduler : ITrayGestureScheduler
    {
        private readonly List<Scheduled> _scheduled = new();
        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            var item = new Scheduled(callback); _scheduled.Add(item); return item;
        }
        internal void FireAll()
        {
            foreach (var item in _scheduled.ToArray()) item.Fire();
            _scheduled.RemoveAll(item => item.Completed);
        }
        private sealed class Scheduled(Action callback) : IDisposable
        {
            private bool _canceled;
            internal bool Completed { get; private set; }
            internal void Fire() { if (Completed) return; Completed = true; if (!_canceled) callback(); }
            public void Dispose() { _canceled = true; Completed = true; }
        }
    }

    private sealed class FakeStartupRegistry : IStartupRegistryAdapter
    {
        internal Dictionary<string, object> Values { get; } = new(StringComparer.Ordinal);
        internal Exception? ReadError { get; init; }
        internal Exception? WriteError { get; init; }
        public object? Read(string valueName)
        {
            if (ReadError is not null) throw ReadError;
            return Values.TryGetValue(valueName, out var value) ? value : null;
        }
        public void Write(string valueName, string value)
        {
            if (WriteError is not null) throw WriteError;
            Values[valueName] = value;
        }
        public void Delete(string valueName) => Values.Remove(valueName);
    }
}
