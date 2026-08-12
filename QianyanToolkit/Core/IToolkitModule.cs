using System;

namespace QToolKit.Core;

internal interface IToolkitModule : IDisposable
{
    string Id { get; }

    string DisplayName { get; }

    string Version { get; }

    string Description { get; }

    string CommandHelp { get; }

    bool IsRunning { get; }

    void Start();

    void Stop();

    void DrawSettings();
}
