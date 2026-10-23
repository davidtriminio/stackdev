using System.Text.RegularExpressions;

namespace StackDev.Core.Deploy;

/// <summary>
/// The remote script speaks English (it is easier to test and to read in a server log). What the user sees is Spanish:
/// this turns each known message into its Spanish text. An unknown message is returned as it came.
/// </summary>
public static class RemoteText
{
    private static string Item(string name) => name switch
    {
        "web-apache" => "Apache",
        "web-nginx" => "Nginx",
        "certbot" => "certbot (certificados HTTPS)",
        "rsync" => "rsync",
        "postgres" => "PostgreSQL",
        "runtime" => "el runtime de ASP.NET Core",
        _ => name,
    };

    private static readonly (Regex Pattern, Func<Match, string> To)[] Rules = Build();

    private static (Regex, Func<Match, string>)[] Build()
    {
        var rules = new List<(Regex, Func<Match, string>)>();
        void R(string pattern, string spanish) => rules.Add((new Regex("^" + pattern + "$", RegexOptions.Singleline), m => Expand(spanish, m)));
        void E(string english, string spanish) => R(Regex.Escape(english), spanish);

        // ---- steps
        E("Backing up the current file", "Guardando una copia del archivo actual");
        E("Backing up the running version", "Guardando una copia de la versión actual");
        E("Checking the new file", "Revisando el archivo nuevo");
        E("Checking the server", "Revisando el servidor");
        E("Checking the upload and the server", "Revisando lo subido y el servidor");
        R(@"Configuring the web server \((.+)\)", "Configurando el servidor web ($1)");
        E("Creating the service user and folders", "Creando el usuario del servicio y las carpetas");
        E("Creating the systemd service", "Creando el servicio systemd");
        E("Done", "Listo");
        R(@"Going back to the previous version \((.+)\)", "Volviendo a la versión anterior ($1)");
        E("HTTPS certificate", "Certificado HTTPS");
        rules.Add((new Regex(@"^Installing (web-apache|web-nginx|certbot|rsync|postgres|runtime)$"), m => "Instalando " + Item(m.Groups[1].Value)));
        E("Installing the new file", "Instalando el archivo nuevo");
        E("Installing the secrets file", "Instalando el archivo de secretos");
        E("Replacing the files", "Reemplazando los archivos");
        R(@"Restarting (.+) to apply it", "Reiniciando $1 para aplicar los cambios");
        R(@"Restoring (.+)", "Restaurando $1");
        E("SELinux and firewall", "SELinux y firewall");
        E("Saving the current version before going back", "Guardando la versión actual antes de volver");
        E("Setting permissions", "Ajustando permisos");
        R(@"Starting (.+) and checking it is healthy", "Arrancando $1 y comprobando que funciona");
        R(@"Stopping (.+)", "Deteniendo $1");

        // ---- info
        rules.Add((new Regex(@"^(web-apache|web-nginx|certbot|rsync|postgres|runtime) installed$"), m => Item(m.Groups[1].Value) + " instalado"));
        R(@"Backup (.+) saved", "Copia $1 guardada");
        E("Certificate issued and HTTP now redirects to HTTPS", "Certificado emitido; HTTP ahora redirige a HTTPS");
        E("HTTPS skipped", "HTTPS omitido");
        E("certbot is not installed", "certbot no está instalado");
        E("The certificate could not be issued", "No se pudo emitir el certificado");
        E("Checksum OK", "Suma de verificación correcta");
        R(@"Kestrel answers on (\S+) \(HTTP (\S+)\)", "La API responde en $1 (HTTP $2)");
        E("No local firewall detected", "No se detectó un firewall local");
        E("SELinux: the web server may reach the API", "SELinux: el servidor web puede llegar a la API");
        R(@"Secrets installed in (.+)", "Secretos instalados en $1");
        E("Service enabled (it starts with the first deployment)", "Servicio habilitado (arranca con la primera subida)");
        R(@"Site for (\S+) written to (.+)", "Sitio de $1 escrito en $2");
        E("Updating the package lists", "Actualizando las listas de paquetes");
        E("firewalld: http and https allowed", "firewalld: http y https permitidos");
        E("ufw: ports 80 and 443 allowed", "ufw: puertos 80 y 443 permitidos");

        E("The values were saved, but the service still does not start.", "Se guardaron los valores, pero el servicio todavía no arranca.");
        E("Trying the new version with the current settings before replacing anything", "Probando la versión nueva con la configuración actual antes de reemplazar nada");
        E("Trying the app with its current settings", "Probando la API con su configuración actual");
        E("The new version does not start with the current settings", "La versión nueva no arranca con la configuración actual");
        E("The application does not start with the current settings", "La API no arranca con la configuración actual");
        E("The new version does not start with the current settings. Nothing was changed; the current version keeps running.", "La versión nueva no arranca con la configuración actual. No se cambió nada; la versión actual sigue en marcha.");
        E("The application does not start with the current settings.", "La API no arranca con la configuración actual.");
        R(@"There is no application in (.+) yet\. Upload it first\.", "Todavía no hay una API en $1. Súbela primero.");
        E("Invalid content hash", "La huella del contenido no es válida");
        E("Checking PostgreSQL", "Revisando PostgreSQL");
        R(@"Creating the user (\S+)", "Creando el usuario de base de datos $1");
        R(@"Creating the database (\S+)", "Creando la base de datos $1");
        E("PostgreSQL is not installed; installing it", "PostgreSQL no está instalado; lo instalo");
        R(@"The user (\S+) already existed; its password was renewed", "El usuario $1 ya existía; le puse una clave nueva");
        E("Could not install PostgreSQL", "No se pudo instalar PostgreSQL");
        E("Could not set the database password", "No se pudo poner la clave del usuario de base de datos");
        E("Could not create the database user", "No se pudo crear el usuario de base de datos");
        E("Could not create the database", "No se pudo crear la base de datos");
        E("Invalid database password", "La clave de la base de datos no es válida");

        // ---- warnings
        R(@"ASP\.NET Core runtime (\d+) is not installed\. Install it before the first deploy: (.+)", "El runtime de ASP.NET Core $1 no está instalado. Instálalo antes de la primera subida: $2");
        rules.Add((new Regex(@"^Could not install (web-apache|web-nginx|certbot|rsync|postgres|runtime)$"), m => "No se pudo instalar " + Item(m.Groups[1].Value)));
        E("Could not reload firewalld", "No se pudo recargar firewalld");
        E("Could not save the current version", "No se pudo guardar la versión actual");
        E("Could not set httpd_can_network_connect", "No se pudo activar httpd_can_network_connect");
        R(@"Deployment failed: (.+)", "La subida falló: $1");
        E("In your cloud (Oracle, AWS...) the network rules must also allow ports 80 and 443.", "En tu nube (Oracle, AWS…) las reglas de red también deben permitir los puertos 80 y 443.");
        E("The service is not healthy with the new values", "El servicio no funciona bien con los valores nuevos");
        E("dotnet was not found on the server", "No encontré dotnet en el servidor");

        // ---- failures
        E("--svc is required", "Falta el nombre del servicio");
        E("Bundle outside the run directory", "El paquete está fuera de la carpeta de trabajo");
        E("File outside the run directory", "El archivo está fuera de la carpeta de trabajo");
        R(@"Invalid (--db|--https|--install|--keep|--runtime|--wait)", "Valor no válido en $1");
        E("Invalid backup id", "Identificador de copia no válido");
        E("Invalid checksum", "Suma de verificación no válida");
        E("Invalid dll name", "Nombre de .dll no válido");
        E("Invalid domain", "Dominio no válido");
        E("Invalid email", "Correo no válido");
        E("Invalid port", "Puerto no válido");
        E("Invalid run directory", "Carpeta de trabajo no válida");
        E("Invalid service name", "Nombre de servicio no válido");
        E("Invalid web server", "Servidor web no válido");
        E("Missing arguments", "Faltan datos para la operación");
        E("Nothing to install", "No hay nada que instalar");
        R(@"Unknown argument: (.+)", "Argumento desconocido: $1");
        R(@"Unknown command: (.+)", "Operación desconocida: $1");
        R(@"(\S+) does not exist\. Provision the API first\.", "$1 no existe. Crea la API primero.");
        R(@"(\S+) does not exist", "$1 no existe");
        R(@"(\S+) does not exist; provision the API first", "$1 no existe; crea la API primero");
        R(@"(\S+) already exists \(another site\?\)", "$1 ya existe (¿otro sitio?)");
        R(@"ASP\.NET Core runtime (\d+) is not installed on the server\. Install it with: (.+)", "El servidor no tiene el runtime de ASP.NET Core $1. Instálalo con: $2");
        E("Apache is not installed", "Apache no está instalado");
        E("Nginx is not installed", "Nginx no está instalado");
        E("Backup failed; nothing was changed", "Falló la copia de seguridad; no se cambió nada");
        E("Cannot create the backup folder", "No se pudo crear la carpeta de copias");
        E("Could not stop the service; nothing was changed", "No se pudo detener el servicio; no se cambió nada");
        E("Must run as root (use sudo)", "Debe ejecutarse como administrador (sudo)");
        R(@"Not enough disk space \(need about (\d+) MB, (\d+) MB free\)", "No hay espacio suficiente en disco (hacen falta unos $1 MB y hay $2 MB libres)");
        R(@"Port (\d+) is already in use or assigned to another site", "El puerto $1 ya está en uso o asignado a otro sitio");
        R(@"The service '(.+)' already exists\. Deploy to it, or provision again with overwrite\.", "El servicio «$1» ya existe. Sube a él, o créalo de nuevo reemplazándolo.");
        R(@"The service '(.+)' does not exist on this server\. Provision it first\.", "El servicio «$1» no existe en este servidor. Aprovisiónalo primero.");
        E("There is no backup to go back to", "No hay una copia a la que volver");
        R(@"(.+) is not at the root of the upload", "$1 no está en la raíz de lo subido");
        E("The archive could not be extracted", "No se pudo descomprimir lo subido");
        E("The upload is corrupt (checksum does not match). Nothing was changed.", "Lo subido está dañado (la suma de verificación no coincide). No se cambió nada.");
        E("The uploaded env file is not there", "No está el archivo de variables subido");
        E("The uploaded file is not there", "No está el archivo subido");
        E("The new values broke the service; the previous file is back and the service runs again.", "Los valores nuevos rompieron el servicio; se restauró el archivo anterior y el servicio vuelve a funcionar.");
        R(@"First upload: the files are on the server but the service did not start \((.+)\)\. It is stopped; there was no previous version to go back to\.", "Es la primera subida: los archivos ya están en el servidor pero el servicio no arrancó ($1). Quedó detenido; no había una versión anterior a la que volver.");
        E("the service did not start", "el servicio no arrancó");
        E("copying the files failed", "falló la copia de archivos");
        E("setting permissions failed", "falló el ajuste de permisos");
        R(@"the service is not healthy after (\d+)s", "el servicio no responde bien tras $1 s");
        R(@"The new version failed \((.+)\)\. The previous version is running again\.", "La versión nueva falló ($1). La versión anterior vuelve a estar en marcha.");
        R(@"The new values broke the service and it is still down with the previous file\. Backup: (.+)", "Los valores nuevos rompieron el servicio y sigue caído con el archivo anterior. Copia: $1");
        R(@"The new version failed \((.+)\) AND the automatic rollback failed\. Backup (\S+) is in (.+)", "La versión nueva falló ($1) y la vuelta atrás automática también. La copia $2 está en $3");
        R(@"The restored version is not healthy either\. Backups are in (.+)", "La versión restaurada tampoco funciona bien. Las copias están en $1");
        E("Apache rejected the configuration; the new site was removed", "Apache rechazó la configuración; se quitó el sitio nuevo");
        E("Could not create the folders", "No se pudieron crear las carpetas");
        R(@"Could not create the user (.+)", "No se pudo crear el usuario $1");
        E("Could not enable the service", "No se pudo habilitar el servicio");
        E("Could not install the env file", "No se pudo instalar el archivo de variables");
        E("Could not reload Apache", "No se pudo recargar Apache");
        E("Could not reload Nginx", "No se pudo recargar Nginx");
        E("Could not set permissions on the data folder", "No se pudieron ajustar los permisos de la carpeta de datos");
        E("Could not write the Apache site", "No se pudo escribir el sitio de Apache");
        E("Could not write the Nginx site", "No se pudo escribir el sitio de Nginx");
        E("Could not write the unit", "No se pudo escribir el servicio systemd");
        E("Nginx rejected the configuration; the new site was removed", "Nginx rechazó la configuración; se quitó el sitio nuevo");
        E("daemon-reload failed", "Falló la recarga de systemd");
        R(@"Could not install: (.+)", "No se pudo instalar: $1");
        E("No supported package manager (dnf, yum or apt-get) was found", "No encontré un gestor de paquetes compatible (dnf, yum o apt-get)");
        E("Cannot back up the current file", "No se pudo guardar una copia del archivo actual");
        E("Could not install the file", "No se pudo instalar el archivo");
        R(@"Line not valid in an environment file: (.+)", "Línea no válida en un archivo de variables: $1");
        return rules.ToArray();
    }

    private static string Expand(string template, Match m) =>
        Regex.Replace(template, @"\$(\d)", d => Translate(m.Groups[int.Parse(d.Groups[1].Value)].Value));

    /// <summary>The Spanish text for a message of the remote script, or the same text when there is no translation.</summary>
    public static string Translate(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var (pattern, to) in Rules)
            if (pattern.Match(text) is { Success: true } m) return to(m);
        return text;
    }
}
