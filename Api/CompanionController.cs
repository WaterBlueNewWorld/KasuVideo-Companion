// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuVideoCompanion.Api;

/// <summary>
/// What KasuVideo asks the companion (README.md, "What it answers").
/// </summary>
/// <remarks>
/// Jellyfin lists plugins' pages to administrators only, so without the companion a regular
/// user's KasuVideo can't find the manifests plugins publish. Jellyfin also never tells a client
/// which of its interfaces a plugin implements: the companion reads that from the loaded
/// assemblies, for administrators.
/// </remarks>
[ApiController]
[Route("KasuVideo/Companion")]
public class CompanionController : ControllerBase
{
    /// <summary>The manifest contract this companion speaks.</summary>
    public const int Contract = 1;

    /// <summary>KasuVideo's manifests are plugin pages with names ending in this.</summary>
    public const string ManifestSuffix = ".kasuvideo.json";

    /// <summary>
    /// Where this version's source code is, offered to everyone who uses the companion over the
    /// network (AGPL-3.0, section 13). A modified version must point this at its own source.
    /// </summary>
    public const string SourceUrl = "https://github.com/WaterBlueNewWorld/KasuVideo-Companion";

    private readonly IPluginManager _plugins;
    private readonly ILogger<CompanionController> _logger;

    /// <summary>Creates the controller.</summary>
    /// <param name="plugins">The server's plugins.</param>
    /// <param name="logger">Where problems with a plugin's manifest are logged.</param>
    public CompanionController(IPluginManager plugins, ILogger<CompanionController> logger)
    {
        _plugins = plugins;
        _logger = logger;
    }

    /// <summary>
    /// What this companion can do besides the manifests, by name, so KasuVideo offers only what
    /// the installed version has: its settings and room settings, and guest passes.
    /// </summary>
    public static readonly IReadOnlyList<string> Capabilities = ["manifests", "implementations", "settings", "roomSettings", "guestPasses"];

    /// <summary>The companion's version and contract, so KasuVideo knows it is there.</summary>
    /// <returns>Its name, version, the contract it speaks, where its source is, and what it can do.</returns>
    [HttpGet("Info")]
    [Authorize]
    public ActionResult<CompanionInfo> Info() =>
        new CompanionInfo(
            "KasuVideo Companion",
            typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            Contract,
            SourceUrl,
            Capabilities);

    /// <summary>
    /// The manifests of the active plugins, for any signed-in user: each plugin page named
    /// "*.kasuvideo.json", read from the plugin's own resources. A page that isn't JSON is left out
    /// (and logged, without its content).
    /// </summary>
    /// <returns>The manifests, with the plugin each comes from.</returns>
    [HttpGet("Manifests")]
    [Authorize]
    public ActionResult<ManifestList> Manifests()
    {
        var found = new List<PluginManifestEntry>();
        foreach (var plugin in _plugins.Plugins)
        {
            if (plugin.Manifest.Status != PluginStatus.Active || plugin.Instance is not IHasWebPages pages)
            {
                continue;
            }

            IEnumerable<PluginPageInfo> list;
            try
            {
                list = pages.GetPages().ToList();
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Couldn't list {Plugin}'s pages", plugin.Name);
                continue;
            }

            foreach (var page in list)
            {
                if (page.Name is null || !page.Name.EndsWith(ManifestSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var manifest = Read(plugin, page);
                if (manifest is not null)
                {
                    found.Add(new PluginManifestEntry(
                        plugin.Id.ToString("N"),
                        plugin.Name,
                        plugin.Version.ToString(),
                        page.Name,
                        manifest.Value));
                }
            }
        }

        return new ManifestList(found);
    }

    /// <summary>
    /// For administrators: every plugin with the Jellyfin interfaces its types implement (metadata
    /// and image providers, scheduled tasks, services, pages...) and the API routes its controllers
    /// add. Found by reflection over the plugin's loaded assemblies; a plugin that isn't loaded
    /// (disabled, failed) lists none.
    /// </summary>
    /// <returns>The plugins and what each implements.</returns>
    [HttpGet("Implementations")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<ImplementationList> Implementations()
    {
        // Jellyfin's bundled plugins (TMDb, OMDb...) share one assembly with the server's own
        // providers: there, a plugin's types are those under its Plugin class's namespace.
        var shared = _plugins.Plugins
            .Where(p => p.Instance is not null)
            .GroupBy(p => p.Instance!.GetType().Assembly)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();
        var result = new List<PluginImplementations>();
        foreach (var plugin in _plugins.Plugins)
        {
            var types = plugin.Instance is null ? [] : TypesOf(plugin, shared);
            result.Add(new PluginImplementations(
                plugin.Id.ToString("N"),
                plugin.Name,
                plugin.Version.ToString(),
                plugin.Manifest.Status.ToString(),
                Interfaces(types),
                Routes(types)));
        }

        return new ImplementationList(result);
    }

    private JsonElement? Read(LocalPlugin plugin, PluginPageInfo page)
    {
        try
        {
            using var stream = plugin.Instance!.GetType().Assembly.GetManifestResourceStream(page.EmbeddedResourcePath);
            if (stream is null)
            {
                _logger.LogWarning("{Plugin}'s page {Page} has no resource", plugin.Name, page.Name);
                return null;
            }

            using var document = JsonDocument.Parse(stream);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            _logger.LogWarning("{Plugin}'s page {Page} isn't JSON", plugin.Name, page.Name);
            return null;
        }
    }

    /// <summary>
    /// The public types of the plugin's own assemblies (its DLLs), as loaded; in an assembly
    /// several plugins share (<paramref name="shared"/>), only those under the namespace of the
    /// plugin's Plugin class.
    /// </summary>
    private static List<Type> TypesOf(LocalPlugin plugin, HashSet<Assembly> shared)
    {
        var main = plugin.Instance!.GetType();
        var scope = main.Namespace ?? string.Empty;
        var files = new HashSet<string>(plugin.DllFiles.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var assemblies = AssemblyLoadContext.All
            .SelectMany(context => context.Assemblies)
            .Where(a => !a.IsDynamic && a.Location.Length > 0 && files.Contains(Path.GetFullPath(a.Location)))
            .Append(main.Assembly)
            .Distinct();
        var types = new List<Type>();
        foreach (var assembly in assemblies)
        {
            try
            {
                types.AddRange(assembly.GetExportedTypes());
            }
            catch (ReflectionTypeLoadException e)
            {
                types.AddRange(e.Types.OfType<Type>().Where(t => t.IsPublic));
            }
            catch (Exception)
            {
                // An assembly that can't be read adds nothing.
            }
        }

        return types
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => !shared.Contains(t.Assembly)
                || (t.Namespace ?? string.Empty) == scope
                || (t.Namespace ?? string.Empty).StartsWith(scope + ".", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>Jellyfin's interfaces (and the host's IHostedService) the types implement.</summary>
    private static List<ImplementedInterface> Interfaces(List<Type> types)
    {
        var byInterface = new SortedDictionary<string, (string Name, SortedSet<string> Types)>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            Type[] interfaces;
            try
            {
                interfaces = type.GetInterfaces();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var i in interfaces)
            {
                if (!IsJellyfins(i))
                {
                    continue;
                }

                var definition = i.IsGenericType ? i.GetGenericTypeDefinition() : i;
                var key = definition.FullName ?? definition.Name;
                if (!byInterface.TryGetValue(key, out var entry))
                {
                    entry = (Display(definition), new SortedSet<string>(StringComparer.Ordinal));
                    byInterface[key] = entry;
                }

                entry.Types.Add(Display(i) == entry.Name ? type.Name : $"{type.Name} ({Display(i)})");
            }
        }

        return byInterface.Select(p => new ImplementedInterface(p.Key, p.Value.Name, p.Value.Types.ToList())).ToList();
    }

    /// <summary>What every plugin's Plugin class implements (through BasePlugin): no news.</summary>
    private static readonly HashSet<string> Plumbing = new(StringComparer.Ordinal)
    {
        "MediaBrowser.Common.Plugins.IPlugin",
        "MediaBrowser.Common.Plugins.IHasPluginConfiguration",
        "MediaBrowser.Common.Plugins.IPluginAssembly",
    };

    private static bool IsJellyfins(Type i)
    {
        if (i.FullName is { } name && Plumbing.Contains(name))
        {
            return false;
        }

        var ns = i.Namespace ?? string.Empty;
        return ns.StartsWith("MediaBrowser.", StringComparison.Ordinal)
            || ns.StartsWith("Jellyfin.", StringComparison.Ordinal)
            || i.FullName == "Microsoft.Extensions.Hosting.IHostedService";
    }

    /// <summary>A type's name as C# writes it: IRemoteMetadataProvider&lt;Movie, MovieInfo&gt;.</summary>
    private static string Display(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(Display))}>";
    }

    /// <summary>The routes the plugin's controllers add, as "GET Path/{id}".</summary>
    private static List<string> Routes(List<Type> types)
    {
        var routes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in types.Where(t => typeof(ControllerBase).IsAssignableFrom(t)))
        {
            // ASP.NET's [controller] token is the class's name without "Controller".
            var controller = type.Name.EndsWith("Controller", StringComparison.Ordinal)
                ? type.Name[..^"Controller".Length]
                : type.Name;
            var prefix = (type.GetCustomAttributes<RouteAttribute>().FirstOrDefault()?.Template ?? string.Empty)
                .Replace("[controller]", controller, StringComparison.Ordinal);
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var http in method.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>())
                {
                    var path = string.Join('/', new[] { prefix, http.Template ?? string.Empty }.Where(s => s.Length > 0));
                    foreach (var verb in http.HttpMethods)
                    {
                        routes.Add($"{verb} /{path}");
                    }
                }
            }
        }

        return routes.ToList();
    }
}

/// <summary>What <c>GET /KasuVideo/Companion/Info</c> answers.</summary>
/// <param name="Name">The companion's name.</param>
/// <param name="Version">Its version.</param>
/// <param name="Contract">The manifest contract it speaks.</param>
/// <param name="Source">Where its source code is (<see cref="CompanionController.SourceUrl"/>).</param>
/// <param name="Capabilities">What it can do (<see cref="CompanionController.Capabilities"/>); missing before 0.2.0.</param>
public record CompanionInfo(string Name, string Version, int Contract, string Source, IReadOnlyList<string> Capabilities);

/// <summary>What <c>GET /KasuVideo/Companion/Manifests</c> answers.</summary>
/// <param name="Manifests">The manifests found.</param>
public record ManifestList(List<PluginManifestEntry> Manifests);

/// <summary>One plugin's manifest.</summary>
/// <param name="PluginId">The plugin's id, without dashes (as <c>/Plugins</c> writes it).</param>
/// <param name="PluginName">Its name.</param>
/// <param name="PluginVersion">Its version.</param>
/// <param name="Page">The page the manifest is (<c>*.kasuvideo.json</c>).</param>
/// <param name="Manifest">The manifest, as the plugin wrote it.</param>
public record PluginManifestEntry(string PluginId, string PluginName, string PluginVersion, string Page, JsonElement Manifest);

/// <summary>What <c>GET /KasuVideo/Companion/Implementations</c> answers.</summary>
/// <param name="Plugins">Every plugin.</param>
public record ImplementationList(List<PluginImplementations> Plugins);

/// <summary>What one plugin implements.</summary>
/// <param name="Id">Its id, without dashes.</param>
/// <param name="Name">Its name.</param>
/// <param name="Version">Its version.</param>
/// <param name="Status">Its status (<c>Active</c>, <c>Disabled</c>...).</param>
/// <param name="Interfaces">The Jellyfin interfaces its types implement.</param>
/// <param name="Routes">The API routes its controllers add.</param>
public record PluginImplementations(
    string Id,
    string Name,
    string Version,
    string Status,
    List<ImplementedInterface> Interfaces,
    List<string> Routes);

/// <summary>One interface and the plugin's types that implement it.</summary>
/// <param name="Interface">Its full name (generic definitions as <c>Name`2</c>), to group by.</param>
/// <param name="Name">Its name as C# writes it.</param>
/// <param name="Types">The plugin's types that implement it, with the type arguments where they
/// differ from the definition.</param>
public record ImplementedInterface(string Interface, string Name, List<string> Types);
