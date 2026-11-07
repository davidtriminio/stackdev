using StackDev.Core.Config;
using StackDev.Core.Util;

namespace StackDev.Core.State;

/// <summary>
/// What is saved about a project is keyed by its folder path, so renaming or moving the folder leaves it behind.
/// This moves that saved state to the new path, or copies just the configuration to another project.
/// </summary>
public static class ProjectRelocation
{
    public sealed record Result(bool Ok, string Message);

    /// <summary>Does the new folder already have its own saved configuration (so moving would overwrite it)?</summary>
    public static bool HasConfig(StateStore state, string root) =>
        new ConfigStore(root, state.ConfigFolder(StateStore.Key(root))).Exists;

    /// <summary>
    /// Moves everything saved for <paramref name="oldRoot"/> (configuration, logs, backups, history, devices...) to
    /// <paramref name="newRoot"/> and updates the project list, the favourite and the last project.
    /// </summary>
    public static Result Relocate(StateStore state, string oldRoot, string newRoot)
    {
        oldRoot = Paths.Normalize(oldRoot);
        newRoot = Paths.Normalize(newRoot);
        if (string.Equals(oldRoot, newRoot, Paths.Comparison)) return new(false, "Es la misma carpeta.");
        if (!Directory.Exists(newRoot)) return new(false, "La carpeta nueva no existe.");
        var oldKey = StateStore.Key(oldRoot);
        var newKey = StateStore.Key(newRoot);
        try
        {
            foreach (var group in new[] { "config", "logs", "respaldos" })
            {
                var from = Path.Combine(state.Dir, group, oldKey);
                if (Directory.Exists(from)) MoveFolder(from, Path.Combine(state.Dir, group, newKey));
            }
            if (Directory.Exists(state.Dir))
                foreach (var file in Directory.GetFiles(state.Dir, $"*-{oldKey}.*"))
                {
                    var name = Path.GetFileName(file);
                    if (name.StartsWith("ventanas-", StringComparison.Ordinal)) continue;   // PIDs of processes of the old path
                    var target = Path.Combine(state.Dir, name.Replace($"-{oldKey}.", $"-{newKey}.", StringComparison.Ordinal));
                    File.Move(file, target, overwrite: true);
                }

            // The name saved in the configuration usually is the folder's: follow the rename.
            var store = new ConfigStore(newRoot, state.ConfigFolder(newKey));
            if (store.Exists)
            {
                store.Load();
                if (store.Error is null && string.Equals(store.Config.Name, Paths.FolderName(oldRoot), StringComparison.OrdinalIgnoreCase))
                {
                    store.Config.Name = Paths.FolderName(newRoot);
                    store.Save(store.Config);
                }
            }
            state.RelocateProject(oldRoot, newRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, "No pude mover los datos: " + ex.Message);
        }
        return new(true, $"«{Paths.FolderName(newRoot)}» ahora usa la configuración y el historial de «{Paths.FolderName(oldRoot)}».");
    }

    /// <summary>Copies the configuration of one project to another (the source folder does not have to exist any more).</summary>
    public static Result CopyConfig(StateStore state, string fromRoot, string toRoot, bool overwrite)
    {
        fromRoot = Paths.Normalize(fromRoot);
        toRoot = Paths.Normalize(toRoot);
        if (string.Equals(fromRoot, toRoot, Paths.Comparison)) return new(false, "Es el mismo proyecto.");
        if (!Directory.Exists(toRoot)) return new(false, "La carpeta de destino no existe.");
        var source = new ConfigStore(fromRoot, state.ConfigFolder(StateStore.Key(fromRoot)));
        if (!source.Exists) return new(false, $"«{Paths.FolderName(fromRoot)}» no tiene configuración guardada.");
        source.Load();
        if (source.Error is not null) return new(false, "La configuración de origen no se puede leer: " + source.Error);

        var target = new ConfigStore(toRoot, state.ConfigFolder(StateStore.Key(toRoot)));
        if (target.Exists && !overwrite) return new(false, "El destino ya tiene configuración.");
        var copy = ConfigJson.Read(ConfigJson.Write(source.Config));
        copy.Name = Paths.FolderName(toRoot);
        try
        {
            if (target.Exists) target.Replace(copy); else target.Save(copy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, "No pude guardar la configuración: " + ex.Message);
        }
        return new(true, $"Copié la configuración de «{Paths.FolderName(fromRoot)}» a «{Paths.FolderName(toRoot)}». Revisa las carpetas y los puertos.");
    }

    /// <summary>Moves the contents of a folder into another one, overwriting files with the same name.</summary>
    private static void MoveFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target, overwrite: true);
        }
        Directory.Delete(from, recursive: true);
    }
}
