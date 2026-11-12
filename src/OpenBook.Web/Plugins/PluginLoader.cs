using System.Reflection;
using System.Runtime.Loader;
using OpenBook.Application.Plugins;

namespace OpenBook.Web.Plugins;

/// <summary>
/// Loads add-ins at startup: each subfolder of the plugins folder (<c>OpenBook:Plugins:Path</c>, default
/// <c>plugins</c> beside the app) holds one add-in's DLLs. A folder with a file named <c>disabled</c> is skipped, and
/// <c>OpenBook:Plugins:SafeMode=true</c> (or <c>--safe-mode</c>) skips them all. Each add-in gets its own load
/// context, sharing OpenBook's and the framework's assemblies so its types match the host's.
/// </summary>
public static class PluginLoader
{
    public const string DisabledMarker = "disabled";

    public static PluginRegistry Load(string folder, bool safeMode)
    {
        var registry = new PluginRegistry { SafeMode = safeMode, Folder = folder };
        if (safeMode || !Directory.Exists(folder))
            return registry;

        foreach (var directory in Directory.GetDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(directory);
            if (File.Exists(Path.Combine(directory, DisabledMarker)))
            {
                registry.Skip(name, PluginState.Disabled);
                continue;
            }

            try
            {
                var plugins = Discover(directory);
                if (plugins.Count == 0)
                {
                    registry.Skip(name, PluginState.Failed, "No class implementing IOpenBookPlugin was found in the folder's DLLs.");
                    continue;
                }

                foreach (var plugin in plugins)
                    registry.Load(name, plugin);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // An add-in's own code, whatever it throws, stops that add-in and not OpenBook.
                registry.Skip(name, PluginState.Failed, PluginApi.Explain(ex));
            }
        }

        return registry;
    }

    private static List<IOpenBookPlugin> Discover(string directory)
    {
        var result = new List<IOpenBookPlugin>();
        Exception? failed = null;
        foreach (var dll in Directory.GetFiles(directory, "*.dll").Order(StringComparer.OrdinalIgnoreCase))
        {
            // Beside the add-in there may be native libraries, and helpers with optional parts missing: neither is the add-in.
            if (!IsManaged(dll))
                continue;
            try
            {
                var context = new PluginContext(dll);
                var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(dll));
                foreach (var type in assembly.GetExportedTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IOpenBookPlugin).IsAssignableFrom(t)))
                    result.Add((IOpenBookPlugin)Activator.CreateInstance(type)!);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or ReflectionTypeLoadException or TypeLoadException)
            {
                failed ??= ex;
            }
        }

        // With nothing found, the reason is worth showing.
        return result.Count == 0 && failed is not null ? throw failed : result;
    }

    private static bool IsManaged(string dll)
    {
        try
        {
            AssemblyName.GetAssemblyName(dll);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>Resolves an add-in's own dependencies from its folder; OpenBook and framework assemblies come from the host.</summary>
    private sealed class PluginContext(string mainAssembly) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(mainAssembly))
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssembly);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (Default.Assemblies.Any(a => AssemblyName.ReferenceMatchesDefinition(a.GetName(), assemblyName))
                || assemblyName.Name?.StartsWith("OpenBook.", StringComparison.Ordinal) == true)
                return null;
            return _resolver.ResolveAssemblyToPath(assemblyName) is { } path ? LoadFromAssemblyPath(path) : null;
        }
    }
}
