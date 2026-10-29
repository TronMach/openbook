using System.Text.Json;
using OpenBook.Application;
using OpenBook.Application.Companies;
using OpenBook.Application.Plugins;
using OpenBook.Domain.Auditing;
using OpenBook.Web.Plugins;

namespace OpenBook.Web.Endpoints;

/// <summary>Turning add-ins on and off (admin): a <c>disabled</c> file in the add-in's folder, read at the next start.</summary>
public static class PluginEndpoints
{
    public static IEndpointRouteBuilder MapPluginEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/settings/plugins/{folder}/{action:regex(^(enable|disable)$)}", async (string folder, string action, PluginRegistry plugins,
            ICompanyStore companies, IClock clock, ICurrentUser user, CancellationToken ct) =>
        {
            var root = plugins.Folder;
            var path = root is null ? null : Path.GetFullPath(Path.Combine(root, folder));
            // Only a direct subfolder of the plugins folder, by its plain name.
            if (path is null || folder.IndexOfAny(['/', '\\', '.']) >= 0 || !string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(root!), StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(path))
                return Results.Redirect("/settings/plugins?error=" + Uri.EscapeDataString("There's no such add-in folder."));

            var marker = Path.Combine(path, PluginLoader.DisabledMarker);
            var disable = action == "disable";
            try
            {
                if (disable)
                    await File.WriteAllTextAsync(marker, "Turned off from Settings › Add-ins.", ct);
                else
                    File.Delete(marker);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.Redirect("/settings/plugins?error=" + Uri.EscapeDataString($"OpenBook can't change the add-in folder: {ex.Message}"));
            }

            var company = await companies.GetRequiredCompanyAsync(ct);
            companies.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, "company", company.Id,
                JsonSerializer.Serialize(new { AddIn = folder, On = disable }), JsonSerializer.Serialize(new { AddIn = folder, On = !disable })));
            await companies.SaveChangesAsync(ct);
            return Results.Redirect("/settings/plugins?done=" + Uri.EscapeDataString($"{folder} is turned {(disable ? "off" : "on")}."));
        }).RequireAuthorization();

        return app;
    }
}
