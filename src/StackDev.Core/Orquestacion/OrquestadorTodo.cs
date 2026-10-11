using StackDev.Core.Android;
using StackDev.Core.Config;
using StackDev.Core.Docker;
using StackDev.Core.Ui;
using StackDev.Core.Util;

namespace StackDev.Core.Orquestacion;

public sealed record ResultadoPaso(string Paso, int Codigo, int Segundos);

/// <summary>Levantar todo, detener todo y verificar todo.</summary>
public sealed class OrquestadorTodo
{
    private readonly Sesion _s;

    public OrquestadorTodo(Sesion s) { _s = s; }

    private static string Nombre(string c) => c switch
    {
        "api" => "API", "web" => "Web", "emulador" => "Emulador", "postgres" => "Postgres", _ => c,
    };

    /// <summary>
    /// Espera en paralelo a que estén listos "api", "web" y/o "emulador", mostrando cada uno cuando termina.
    /// Devuelve false si alguno falló, se agotó el plazo o se canceló (lo que arrancó sigue abierto).
    /// </summary>
    public bool Esperar(IEnumerable<string> comps, int segundos = 180)
    {
        var pend = comps.ToDictionary(c => c, _ => new EsperaEmu());
        var todoOk = true;
        _s.Ui.EscribirInline($"Esperando: {string.Join(", ", pend.Keys.Select(Nombre))} (C o Ctrl+C para dejar de esperar)");
        var limite = Environment.TickCount64 + segundos * 1000L;

        while (pend.Count > 0)
        {
            foreach (var c in pend.Keys.ToList())
            {
                var res = c == "emulador"
                    ? _s.Emulador.TestListo(pend[c])
                    : _s.Servicios.TestListo(c == "api" ? Componente.Api : Componente.Web);
                if (res == "OK")
                {
                    var det = c == "emulador" ? $"{_s.Avd} ({pend[c].Serial})" : _s.Ctx.Url(c == "api" ? Componente.Api : Componente.Web);
                    _s.Ui.Escribir("");
                    _s.Ui.Escribir($"  {Nombre(c)} listo: {det}", Nivel.Ok);
                    if (c == "emulador") _s.Condiciones.TrasArranque();
                    pend.Remove(c);
                }
                else if (res.Length > 0)
                {
                    _s.Ui.Escribir("");
                    _s.Ui.Escribir($"  {Nombre(c)}: {res}", Nivel.Error);
                    pend.Remove(c);
                    todoOk = false;
                }
            }
            if (pend.Count == 0) break;
            if (Environment.TickCount64 > limite)
            {
                _s.Ui.Escribir("");
                _s.Ui.Escribir($"Siguen arrancando: {string.Join(", ", pend.Keys.Select(Nombre))}", Nivel.Aviso);
                return false;
            }
            if (_s.Ct.IsCancellationRequested || Teclado.PulsoCancelar())
            {
                _s.Ui.Escribir("");
                _s.Ui.Escribir("Espera cancelada (lo que arrancó sigue abierto).", Nivel.Aviso);
                return false;
            }
            _s.Ui.EscribirInline(".");
            _s.Ct.WaitHandle.WaitOne(2000);
        }
        return todoOk;
    }

    /// <summary>
    /// 1) Postgres, esperando a que esté listo. 2) Lanza la API, la web y el emulador sin esperar entre ellos.
    /// 3) Espera a los tres en paralelo.
    /// </summary>
    public bool Iniciar()
    {
        var lista = _s.Ctx.LevantarTodo;
        var esperar = new List<string>();
        var ok = true;

        if (lista.Contains("postgres") && _s.Ctx.Tiene(Componente.Postgres))
        {
            if (!_s.Postgres.Iniciar())
            {
                ok = false;
                if (!_s.Ui.Confirmar("Postgres no arrancó. ¿Seguir con lo demás?", true)) return false;
            }
        }
        if (lista.Contains("api") && _s.Ctx.Tiene(Componente.Api))
        {
            if (_s.Servicios.IniciarApi(esperar: false, sinPostgres: true)) esperar.Add("api"); else ok = false;
        }
        if (lista.Contains("web") && _s.Ctx.Tiene(Componente.Web))
        {
            if (_s.Servicios.IniciarWeb(esperar: false)) esperar.Add("web"); else ok = false;
        }
        if (lista.Contains("emulador") && _s.Ctx.Tiene(Componente.Movil) && _s.EmulatorExe is not null)
        {
            var e = _s.Emulador.Estado();
            if (e.Codigo == CodigoEmu.Ok) _s.Ui.Escribir("El emulador ya está corriendo.", Nivel.Tenue);
            else if (e.Codigo == CodigoEmu.NoResponde)
            {
                _s.Ui.Escribir("Hay un emulador que no responde; fuerza su cierre.", Nivel.Aviso);
                ok = false;
            }
            else
            {
                var avd = _s.Avd;
                if (avd is not null && !_s.Emulador.ListaAvds().Contains(avd))
                {
                    _s.Ui.Escribir($"El último emulador usado ({avd}) ya no existe.", Nivel.Aviso);
                    avd = null;
                }
                avd ??= _s.Emulador.ElegirAvd();
                if (avd is not null && _s.Emulador.IniciarProceso(avd, false)) esperar.Add("emulador"); else ok = false;
            }
        }
        if (esperar.Count > 0 && !Esperar(esperar)) ok = false;
        return ok;
    }

    /// <summary>En este orden: app → web → API → Postgres → emulador (guardando su estado).</summary>
    public bool Detener()
    {
        _s.Ui.Escribir("\nDeteniendo todo...", Nivel.Info);
        if (_s.Ctx.Tiene(Componente.Movil) && _s.Servicios.DetenerApps() > 0) _s.Ui.Escribir("App detenida.", Nivel.Ok);
        _s.Servicios.Detener(Componente.Web, silencioso: true);
        _s.Servicios.Detener(Componente.Api, silencioso: true);
        if (_s.Ctx.Tiene(Componente.Postgres) && _s.Postgres.Estado().Corriendo) _s.Postgres.Detener();
        if (_s.Ctx.Tiene(Componente.Movil) && _s.Emulador.HayProceso()) _s.Emulador.Detener();
        _s.Ui.Escribir("Todo detenido.", Nivel.Ok);
        return true;
    }

    /// <summary>Tests de la API y de la web, flutter analyze y flutter test, con un resumen OK/FALLO y la duración de cada uno.</summary>
    public List<ResultadoPaso> Verificar()
    {
        var pasos = new List<(string Nombre, Componente C, string Clave)>();
        if (_s.Comando(Componente.Api, "test") is not null) pasos.Add(("API: tests", Componente.Api, "test"));
        if (_s.Comando(Componente.Web, "test") is not null) pasos.Add(("Web: tests", Componente.Web, "test"));
        if (_s.Comando(Componente.Movil, "analizar") is not null) pasos.Add(("Flutter: analyze", Componente.Movil, "analizar"));
        if (_s.Comando(Componente.Movil, "test") is not null) pasos.Add(("Flutter: test", Componente.Movil, "test"));
        if (pasos.Count == 0) { _s.Ui.Escribir("No hay nada que verificar en la configuración.", Nivel.Aviso); return new(); }

        var res = new List<ResultadoPaso>();
        foreach (var p in pasos)
        {
            if (_s.Ct.IsCancellationRequested) break;
            var t0 = DateTime.Now;
            var c = _s.Ejecutor.Paso(p.C, p.Clave);
            res.Add(new ResultadoPaso(p.Nombre, c, (int)(DateTime.Now - t0).TotalSeconds));
        }

        _s.Ui.Escribir("\n---------- Resultado ----------", Nivel.Info);
        foreach (var r in res)
        {
            if (r.Codigo == 0) _s.Ui.Escribir($"  OK     {r.Paso,-20} {r.Segundos}s", Nivel.Ok);
            else _s.Ui.Escribir($"  FALLO  {r.Paso,-20} {r.Segundos}s (código {r.Codigo})", Nivel.Error);
        }
        var fallos = res.Count(r => r.Codigo != 0);
        if (fallos > 0) _s.Ui.Escribir($"{fallos} de {res.Count} fallaron.", Nivel.Error);
        else _s.Ui.Escribir("Todo en verde.", Nivel.Ok);
        return res;
    }
}
