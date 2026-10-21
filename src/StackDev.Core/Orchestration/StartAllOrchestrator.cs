using StackDev.Core.Android;
using StackDev.Core.Config;
using StackDev.Core.Docker;
using StackDev.Core.Ui;
using StackDev.Core.Util;

namespace StackDev.Core.Orchestration;

public sealed record StepResult(string Step, int Code, int Seconds);

/// <summary>Levantar todo, detener todo y verificar todo.</summary>
public sealed class StartAllOrchestrator
{
    private readonly Session _s;

    public StartAllOrchestrator(Session s) { _s = s; }

    private static string Name(string c) => c switch
    {
        "api" => "API", "web" => "Web", "emulador" => "Emulador", "postgres" => "Postgres", _ => c,
    };

    /// <summary>
    /// Espera en paralelo a que estén listos "api", "web" y/o "emulador", mostrando cada uno cuando termina.
    /// Devuelve false si alguno falló, se agotó el plazo o se canceló (lo que arrancó sigue abierto).
    /// </summary>
    public bool Wait(IEnumerable<string> comps, int seconds = 180)
    {
        var pending = comps.ToDictionary(c => c, _ => new EmuWait());
        var allOk = true;
        _s.Ui.WriteInline($"Esperando: {string.Join(", ", pending.Keys.Select(Name))} (C o Ctrl+C para dejar de esperar)");
        var limit = Environment.TickCount64 + seconds * 1000L;

        while (pending.Count > 0)
        {
            foreach (var c in pending.Keys.ToList())
            {
                var res = c == "emulador"
                    ? _s.Emulator.TestReady(pending[c])
                    : _s.Services.TestReady(c == "api" ? ProjectComponent.Api : ProjectComponent.Web);
                if (res == "OK")
                {
                    var det = c == "emulador" ? $"{_s.Avd} ({pending[c].Serial})" : _s.Ctx.Url(c == "api" ? ProjectComponent.Api : ProjectComponent.Web);
                    _s.Ui.Write("");
                    _s.Ui.Write($"  {Name(c)} listo: {det}", Level.Ok);
                    if (c == "emulador") _s.Conditions.AfterStartup();
                    pending.Remove(c);
                }
                else if (res.Length > 0)
                {
                    _s.Ui.Write("");
                    _s.Ui.Write($"  {Name(c)}: {res}", Level.Error);
                    pending.Remove(c);
                    allOk = false;
                }
            }
            if (pending.Count == 0) break;
            if (Environment.TickCount64 > limit)
            {
                _s.Ui.Write("");
                _s.Ui.Write($"Siguen arrancando: {string.Join(", ", pending.Keys.Select(Name))}", Level.Warning);
                return false;
            }
            if (_s.Ct.IsCancellationRequested || ConsoleKeys.PressCancel())
            {
                _s.Ui.Write("");
                _s.Ui.Write("Espera cancelada (lo que arrancó sigue abierto).", Level.Warning);
                return false;
            }
            _s.Ui.WriteInline(".");
            _s.Ct.WaitHandle.WaitOne(2000);
        }
        return allOk;
    }

    /// <summary>
    /// 1) Postgres, esperando a que esté listo. 2) Lanza la API, la web y el emulador sin esperar entre ellos.
    /// 3) Espera a los tres en paralelo.
    /// </summary>
    public bool Start()
    {
        var list = _s.Ctx.StartAll;
        var wait = new List<string>();
        var ok = true;

        if (list.Contains("postgres") && _s.Ctx.Has(ProjectComponent.Postgres))
        {
            if (!_s.Postgres.Start())
            {
                ok = false;
                if (!_s.Ui.Confirm("Postgres no arrancó. ¿Seguir con lo demás?", true)) return false;
            }
        }
        if (list.Contains("api") && _s.Ctx.Has(ProjectComponent.Api))
        {
            if (_s.Services.StartApi(wait: false, withoutPostgres: true)) wait.Add("api"); else ok = false;
        }
        if (list.Contains("web") && _s.Ctx.Has(ProjectComponent.Web))
        {
            if (_s.Services.StartWeb(wait: false)) wait.Add("web"); else ok = false;
        }
        if (list.Contains("emulador") && _s.Ctx.Has(ProjectComponent.Mobile) && _s.EmulatorExe is not null)
        {
            var e = _s.Emulator.State();
            if (e.Code == EmuCode.Ok) _s.Ui.Write("El emulador ya está corriendo.", Level.Muted);
            else if (e.Code == EmuCode.NotResponding)
            {
                _s.Ui.Write("Hay un emulador que no responde; fuerza su cierre.", Level.Warning);
                ok = false;
            }
            else
            {
                var avd = _s.Avd;
                if (avd is not null && !_s.Emulator.AvdList().Contains(avd))
                {
                    _s.Ui.Write($"El último emulador usado ({avd}) ya no existe.", Level.Warning);
                    avd = null;
                }
                avd ??= _s.Emulator.ChooseAvd();
                if (avd is not null && _s.Emulator.StartProcess(avd, false)) wait.Add("emulador"); else ok = false;
            }
        }
        if (wait.Count > 0 && !Wait(wait)) ok = false;
        return ok;
    }

    /// <summary>En este orden: app → web → API → Postgres → emulador (guardando su estado).</summary>
    public bool Stop()
    {
        _s.Ui.Write("\nDeteniendo todo...", Level.Info);
        if (_s.Ctx.Has(ProjectComponent.Mobile) && _s.Services.StopApps() > 0) _s.Ui.Write("App detenida.", Level.Ok);
        _s.Services.Stop(ProjectComponent.Web, silent: true);
        _s.Services.Stop(ProjectComponent.Api, silent: true);
        if (_s.Ctx.Has(ProjectComponent.Postgres) && _s.Postgres.State().Running) _s.Postgres.Stop();
        if (_s.Ctx.Has(ProjectComponent.Mobile) && _s.Emulator.HasProcess()) _s.Emulator.Stop();
        _s.Ui.Write("Todo detenido.", Level.Ok);
        return true;
    }

    /// <summary>Tests de la API y de la web, flutter analyze y flutter test, con un resumen OK/FALLO y la duración de cada uno.</summary>
    public List<StepResult> Verify()
    {
        var steps = new List<(string Name, ProjectComponent C, string Key)>();
        if (_s.Command(ProjectComponent.Api, "test") is not null) steps.Add(("API: tests", ProjectComponent.Api, "test"));
        if (_s.Command(ProjectComponent.Web, "test") is not null) steps.Add(("Web: tests", ProjectComponent.Web, "test"));
        if (_s.Command(ProjectComponent.Mobile, "analizar") is not null) steps.Add(("Flutter: analyze", ProjectComponent.Mobile, "analizar"));
        if (_s.Command(ProjectComponent.Mobile, "test") is not null) steps.Add(("Flutter: test", ProjectComponent.Mobile, "test"));
        if (steps.Count == 0) { _s.Ui.Write("No hay nada que verificar en la configuración.", Level.Warning); return new(); }

        var res = new List<StepResult>();
        foreach (var p in steps)
        {
            if (_s.Ct.IsCancellationRequested) break;
            var t0 = DateTime.Now;
            var c = _s.Executor.Step(p.C, p.Key);
            res.Add(new StepResult(p.Name, c, (int)(DateTime.Now - t0).TotalSeconds));
        }

        _s.Ui.Write("\n---------- Resultado ----------", Level.Info);
        foreach (var r in res)
        {
            if (r.Code == 0) _s.Ui.Write($"  OK     {r.Step,-20} {r.Seconds}s", Level.Ok);
            else _s.Ui.Write($"  FALLO  {r.Step,-20} {r.Seconds}s (código {r.Code})", Level.Error);
        }
        var failures = res.Count(r => r.Code != 0);
        if (failures > 0) _s.Ui.Write($"{failures} de {res.Count} fallaron.", Level.Error);
        else _s.Ui.Write("Todo en verde.", Level.Ok);
        return res;
    }
}
