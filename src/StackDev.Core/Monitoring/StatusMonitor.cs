using StackDev.Core;
using StackDev.Core.Android;
using StackDev.Core.Config;
using StackDev.Core.Docker;
using StackDev.Core.Services;

namespace StackDev.Core.Monitoring;

/// <summary>Una foto del estado de todo lo que maneja el proyecto.</summary>
public sealed record Snapshot(
    PostgresState? Postgres, ServiceState? Api, ServiceState? Web, EmuState? Emulator,
    bool App, string Conditions, string? LogApi, string? LogWeb, DateTime Time,
    IReadOnlyList<AdbDevice>? Devices = null)
{
    /// <summary>Teléfonos y tablets (USB o red) que ve adb, para mostrar junto al emulador. Vacío si no hay.</summary>
    public string Phones => Devices is null ? "" : string.Join(", ", Devices.Where(d => !d.IsEmulator).Select(d =>
        (d.Description.Length > 0 ? d.Description : d.Serial) + (d.IsNetwork ? " (red)" : " (USB)") + (d.Ready ? "" : $" ⚠ {d.State}")));

    /// <summary>Consulta todo en paralelo (docker, puertos, adb tardan; juntos tardan lo del más lento).</summary>
    public static Snapshot Take(Session s)
    {
        var c = s.Ctx;
        Task<T?> In<T>(bool has, Func<T> f) where T : class =>
            has ? Task.Run(() => { try { return (T?)f(); } catch (Exception) { return null; } }) : Task.FromResult<T?>(null);

        var pg = In(c.Has(ProjectComponent.Postgres), s.Postgres.State);
        var api = In(c.Has(ProjectComponent.Api), () => s.Services.State(ProjectComponent.Api));
        var web = In(c.Has(ProjectComponent.Web), () => s.Services.State(ProjectComponent.Web));
        var emu = In(c.Has(ProjectComponent.Mobile) && s.Adb is not null, s.Emulator.State);
        var devs = In(c.Has(ProjectComponent.Mobile) && s.Adb is not null, () => s.Adb!.Devices());
        Task.WaitAll(pg, api, web, emu, devs);

        var app = c.Has(ProjectComponent.Mobile) && s.Flutter.AppRunning;
        var cond = c.Has(ProjectComponent.Mobile) ? s.Conditions.Text() : "";
        string? Log(ServiceState? e, string key) =>
            e is { Code: ServiceCode.Starting or ServiceCode.Error } ? s.Services.LastLine(key) : null;

        return new(pg.Result, api.Result, web.Result, emu.Result, app, cond, Log(api.Result, "api"), Log(web.Result, "web"), DateTime.Now, devs.Result);
    }

    public bool AnythingRunning =>
        Postgres?.Running == true || Api?.Code is ServiceCode.Running or ServiceCode.Starting ||
        Web?.Code is ServiceCode.Running or ServiceCode.Starting ||
        Emulator is { Code: not EmuCode.None } || App;
}

/// <summary>Actualiza la foto de fondo cada par de segundos; se pausa mientras se ejecuta una acción.</summary>
public sealed class StatusMonitor : IDisposable
{
    private readonly Session _s;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _wake = new(false);
    private volatile bool _end;
    private volatile bool _pause;
    private volatile Snapshot? _last;

    public StatusMonitor(Session s, TimeSpan? each = null)
    {
        _s = s;
        var interval = each ?? TimeSpan.FromSeconds(2);
        _thread = new Thread(() => Loop(interval)) { IsBackground = true, Name = "stackdev-monitor" };
        _thread.Start();
    }

    public Snapshot? Last => _last;

    public void Pause() => _pause = true;

    public void Resume()
    {
        _pause = false;
        _wake.Set();
    }

    /// <summary>Pide una foto nueva ya (después de una acción).</summary>
    public void Refresh() => _wake.Set();

    private void Loop(TimeSpan interval)
    {
        while (!_end)
        {
            if (!_pause)
            {
                try { _last = Snapshot.Take(_s); }
                catch (Exception) { /* un fallo puntual no debe tumbar el panel */ }
            }
            _wake.Wait(interval);
            _wake.Reset();
        }
    }

    public void Dispose()
    {
        _end = true;
        _wake.Set();
    }
}
