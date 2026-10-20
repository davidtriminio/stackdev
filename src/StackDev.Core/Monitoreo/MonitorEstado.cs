using StackDev.Core;
using StackDev.Core.Android;
using StackDev.Core.Config;
using StackDev.Core.Docker;
using StackDev.Core.Servicios;

namespace StackDev.Core.Monitoreo;

/// <summary>Una foto del estado de todo lo que maneja el proyecto.</summary>
public sealed record Instantanea(
    EstadoPostgres? Postgres, EstadoServicio? Api, EstadoServicio? Web, EstadoEmu? Emulador,
    bool App, string Condiciones, string? LogApi, string? LogWeb, DateTime Hora,
    IReadOnlyList<DispositivoAdb>? Dispositivos = null)
{
    /// <summary>Teléfonos y tablets (USB o red) que ve adb, para mostrar junto al emulador. Vacío si no hay.</summary>
    public string Telefonos => Dispositivos is null ? "" : string.Join(", ", Dispositivos.Where(d => !d.EsEmulador).Select(d =>
        (d.Descripcion.Length > 0 ? d.Descripcion : d.Serial) + (d.EsRed ? " (red)" : " (USB)") + (d.Listo ? "" : $" ⚠ {d.Estado}")));

    /// <summary>Consulta todo en paralelo (docker, puertos, adb tardan; juntos tardan lo del más lento).</summary>
    public static Instantanea Tomar(Sesion s)
    {
        var c = s.Ctx;
        Task<T?> En<T>(bool tiene, Func<T> f) where T : class =>
            tiene ? Task.Run(() => { try { return (T?)f(); } catch (Exception) { return null; } }) : Task.FromResult<T?>(null);

        var pg = En(c.Tiene(Componente.Postgres), s.Postgres.Estado);
        var api = En(c.Tiene(Componente.Api), () => s.Servicios.Estado(Componente.Api));
        var web = En(c.Tiene(Componente.Web), () => s.Servicios.Estado(Componente.Web));
        var emu = En(c.Tiene(Componente.Movil) && s.Adb is not null, s.Emulador.Estado);
        var devs = En(c.Tiene(Componente.Movil) && s.Adb is not null, () => s.Adb!.Dispositivos());
        Task.WaitAll(pg, api, web, emu, devs);

        var app = c.Tiene(Componente.Movil) && s.Flutter.AppCorriendo;
        var cond = c.Tiene(Componente.Movil) ? s.Condiciones.Texto() : "";
        string? Log(EstadoServicio? e, string clave) =>
            e is { Codigo: CodigoServicio.Arrancando or CodigoServicio.Error } ? s.Servicios.UltimaLinea(clave) : null;

        return new(pg.Result, api.Result, web.Result, emu.Result, app, cond, Log(api.Result, "api"), Log(web.Result, "web"), DateTime.Now, devs.Result);
    }

    public bool HayAlgoCorriendo =>
        Postgres?.Corriendo == true || Api?.Codigo is CodigoServicio.Corriendo or CodigoServicio.Arrancando ||
        Web?.Codigo is CodigoServicio.Corriendo or CodigoServicio.Arrancando ||
        Emulador is { Codigo: not CodigoEmu.Ninguno } || App;
}

/// <summary>Actualiza la foto de fondo cada par de segundos; se pausa mientras se ejecuta una acción.</summary>
public sealed class MonitorEstado : IDisposable
{
    private readonly Sesion _s;
    private readonly Thread _hilo;
    private readonly ManualResetEventSlim _despertar = new(false);
    private volatile bool _fin;
    private volatile bool _pausa;
    private volatile Instantanea? _ultima;

    public MonitorEstado(Sesion s, TimeSpan? cada = null)
    {
        _s = s;
        var intervalo = cada ?? TimeSpan.FromSeconds(2);
        _hilo = new Thread(() => Bucle(intervalo)) { IsBackground = true, Name = "stackdev-monitor" };
        _hilo.Start();
    }

    public Instantanea? Ultima => _ultima;

    public void Pausar() => _pausa = true;

    public void Reanudar()
    {
        _pausa = false;
        _despertar.Set();
    }

    /// <summary>Pide una foto nueva ya (después de una acción).</summary>
    public void Refrescar() => _despertar.Set();

    private void Bucle(TimeSpan intervalo)
    {
        while (!_fin)
        {
            if (!_pausa)
            {
                try { _ultima = Instantanea.Tomar(_s); }
                catch (Exception) { /* un fallo puntual no debe tumbar el panel */ }
            }
            _despertar.Wait(intervalo);
            _despertar.Reset();
        }
    }

    public void Dispose()
    {
        _fin = true;
        _despertar.Set();
    }
}
