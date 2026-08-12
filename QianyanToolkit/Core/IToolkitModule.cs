using System;

namespace QToolKit.Core;

internal interface IToolkitModule : IDisposable
{
    string Id { get; }

    string DisplayName { get; }

    string Description { get; }

    bool IsRunning { get; }

    void Start();

    void Stop();

    void DrawSettings();
}
