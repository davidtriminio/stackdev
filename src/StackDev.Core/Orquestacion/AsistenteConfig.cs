using System.Globalization;
using StackDev.Core.Config;
using StackDev.Core.Detection;
using StackDev.Core.Ui;
using StackDev.Core.Util;

namespace StackDev.Core.Orquestacion;

/// <summary>Primera vez en un proyecto: detecta los componentes y pregunta lo que no se puede saber (Postgres, Android SDK).</summary>
public sealed class AsistenteConfig
{
    private readonly Sesion _s;

    public AsistenteConfig(Sesion s) { _s = s; }

    public ProyectoConfig Crear()
    {
        var nombre = Rutas.Slug(Rutas.NombreCarpeta(_s.Raiz));
        _s.Ui.Escribir($"\nBuscando componentes en {_s.Raiz} ...", Nivel.Tenue);
        var d = Detector.Detectar(_s.Raiz);
        foreach (var h in d.Hallazgos) _s.Ui.Escribir("  " + h, d.EncontroAlgo ? Nivel.Ok : Nivel.Aviso);

        if (d.Config.Api is not null)
        {
            var quiere = d.ApiUsaNpgsql;
            if (quiere) _s.Ui.Escribir("\nLa API usa PostgreSQL.", Nivel.Info);
            else if (_s.Docker.Instalado) quiere = _s.Ui.Confirmar("¿La API usa una base de datos Postgres en Docker?", false);
            if (quiere)
            {
                var pg = LeerConfigPostgres(nombre);
                if (pg is not null)
                {
                    var nc = d.NombreConexion ?? _s.Ui.Preguntar("Nombre de la cadena de conexión (ConnectionStrings:...)",
                        CultureInfo.CurrentCulture.TextInfo.ToTitleCase(nombre));
                    Detector.AplicarPostgres(d.Config, pg, nc ?? "Default");
                    _s.Ui.Escribir($"  Postgres:          {pg.Contenedor} ({pg.Imagen}) puerto {pg.Puerto}", Nivel.Ok);
                    _s.Ui.Escribir($"  Cadena de conexión para la API: ConnectionStrings__{nc}", Nivel.Ok);
                }
            }
        }
        if (d.Config.Movil is not null) d.Config.Movil.AndroidSdk = BuscarSdk();
        return d.Config;
    }

    private string BuscarSdk()
    {
        string? flutter = null;
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrEmpty(appData))
        {
            try { var f = Path.Combine(appData, ".flutter_settings"); if (File.Exists(f)) flutter = File.ReadAllText(f); } catch (IOException) { }
        }
        var sdk = AndroidSdkLocator.Buscar(null, Environment.GetEnvironmentVariable, flutter, Environment.GetEnvironmentVariable("LOCALAPPDATA"));
        if (sdk is not null) return sdk;
        while (true)
        {
            var p = _s.Ui.Preguntar(@"Ruta del Android SDK (ej: V:\Programas\Android\Sdk, Enter para omitir)")?.Trim().Trim('"');
            if (string.IsNullOrEmpty(p)) return "";
            if (AndroidSdkLocator.EsValido(p)) return p;
            _s.Ui.Escribir(@"En esa carpeta no está emulator\emulator.exe.", Nivel.Error);
            if (!_s.Ui.Interactiva) return "";
        }
    }

    /// <summary>Lista los contenedores de Postgres/PostGIS, lee la configuración del que elijas con docker inspect, o pregunta los datos.</summary>
    private PostgresConfig? LeerConfigPostgres(string nombre)
    {
        if (_s.Ui.Interactiva && _s.Docker.Instalado && !_s.Docker.Vivo() &&
            _s.Ui.Confirmar("Docker no está corriendo. ¿Abrirlo para buscar tu contenedor?", true))
            _s.Docker.IniciarDesktop();

        if (_s.Docker.Instalado && _s.Docker.Vivo())
        {
            var r = _s.Docker.Run(new[] { "ps", "-a", "--format", "{{.Names}}|{{.Image}}" }, 15);
            var lista = PostgresDocker.FiltrarContenedores(r.Salida);
            if (lista.Count > 0)
            {
                var opciones = lista.Select(c => $"{c.Nombre}  ({c.Imagen})").ToList();
                opciones.Add("Configurar uno nuevo");
                var i = _s.Ui.Elegir("\nContenedores de Postgres encontrados (Enter/cancelar = sin Postgres):", opciones, -1);
                if (i < 0) return null;
                if (i < lista.Count)
                {
                    var insp = _s.Docker.Run(new[] { "inspect", lista[i].Nombre, "--format", "{{json .}}" }, 15);
                    var pg = insp.Ok ? PostgresDocker.DesdeInspect(lista[i].Nombre, insp.Salida) : null;
                    if (pg is not null) return pg;
                    _s.Ui.Escribir("No pude leer ese contenedor; ingresa los datos.", Nivel.Aviso);
                }
            }
        }

        _s.Ui.Escribir("\nDatos del contenedor de Postgres:", Nivel.Info);
        var cont = _s.Ui.Preguntar("Nombre del contenedor", $"{nombre}-pg") ?? $"{nombre}-pg";
        var nuevo = new PostgresConfig
        {
            Contenedor = cont,
            Imagen = _s.Ui.Preguntar("Imagen", "postgres:17-alpine"),
            Puerto = PedirPuerto("Puerto en tu PC", 5432),
            Usuario = _s.Ui.Preguntar("Usuario", nombre),
            Password = _s.Ui.Preguntar("Contraseña", "dev"),
            BaseDatos = _s.Ui.Preguntar("Base de datos", nombre),
            Volumen = "",
        };
        if (_s.Ui.Confirmar("¿Guardar los datos en un volumen para no perderlos al recrear el contenedor?", true))
            nuevo.Volumen = $"{cont}-data";
        return nuevo;
    }

    private int PedirPuerto(string texto, int defecto)
    {
        while (true)
        {
            var v = _s.Ui.Preguntar(texto, defecto.ToString());
            if (int.TryParse(v, out var p) && p is >= 1 and <= 65535) return p;
            _s.Ui.Escribir("Escribe un número de puerto (1-65535).", Nivel.Error);
            if (!_s.Ui.Interactiva) return defecto;
        }
    }
}
