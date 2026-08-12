using System;
using System.Collections.Generic;

namespace QToolKit.Core;

internal sealed class ModuleHost : IDisposable
{
    private readonly List<IToolkitModule> modules = [];

    public IReadOnlyList<IToolkitModule> Modules => this.modules;

    public void Register(IToolkitModule module)
        => this.modules.Add(module);

    public void Dispose()
    {
        for (var index = this.modules.Count - 1; index >= 0; index--)
        {
            var module = this.modules[index];
            try
            {
                module.Stop();
            }
            finally
            {
                module.Dispose();
            }
        }

        this.modules.Clear();
    }
}
