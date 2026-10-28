# stackdev

**Español** · [English](README.md)

Una sola herramienta para levantar un proyecto full-stack en local (base de datos, API, web y emulador) y publicar su API en tu propio servidor por SSH, sin abrir una terminal. Nació para **API .NET + web Angular + app Flutter + Postgres en Docker** y crece hacia otros lenguajes.
Se usa como ventana (WPF, Windows) o como línea de comandos que se instala como *herramienta global de .NET*.

> **Estado:** en uso diario en Windows. Hay una primera ventana multiplataforma (`dotnet run --project src/StackDev.App`, Avalonia) que corre en Windows, Linux y macOS y por ahora solo maneja la API y la web locales. La ruta completa está en el [plan](README.md#roadmap) (en inglés).

## Requisitos

- Windows 10/11 · .NET SDK 10 (`dotnet --list-sdks` debe mostrar `10.x`)
- Para el resto: Docker Desktop, Android SDK, Flutter, pnpm/npm

## Compilar y probar

```powershell
git clone https://github.com/davidtriminio/stackdev
cd stackdev
dotnet test                                   # tests del núcleo
dotnet run --project src/StackDev.Cli -- config C:\ruta\al\proyecto
```

## Instalar como herramienta (`stackdev`)

```powershell
dotnet pack src/StackDev.Cli -c Release                        # genera .\nupkg\stackdev.0.1.0.nupkg
dotnet tool install  -g stackdev --add-source .\nupkg          # primera vez
dotnet tool update   -g stackdev --add-source .\nupkg          # después de cambios
dotnet tool uninstall -g stackdev                              # quitarla
```

## Uso

`stackdev` (sin argumentos) abre el **panel en vivo**: estado de Postgres, API, web, emulador y app, que se actualiza solo.
Ahí puedes:

- escribir un **código** y Enter (`a1`, `w1`, `d5`, `1` = levantar todo…; varios separados por espacio: `d1 a1 w1`),
- pulsar **Enter o ↓** para el **menú con flechas** (agrupado por sección),
- `?` lista de códigos · `c` condiciones del dispositivo (selección múltiple) · `k` consola de comandos · `g` configuración · `p` cambiar de proyecto · `q` salir (pregunta si detener todo).

**Ctrl+C** cancela solo lo que esté corriendo y vuelve al panel.

Opciones comunes: `-p <ruta>` (proyecto), `--avd <nombre>`, `--modo debug|profile|release`, `--args-extra "..."`, `--misma-ventana`, `--no-centrar`, `-y` (contesta sí a todo).

### Comandos sin interfaz (scripts)

| Comando | Qué hace |
|---|---|
| `stackdev up` / `down` / `check` | Levantar todo / detener todo / tests y análisis (código de salida ≠ 0 si algo falla) |
| `stackdev api start\|stop\|restart\|test\|build\|open\|logs\|logs-tab` | API |
| `stackdev web start\|stop\|test\|build\|open\|install\|logs\|logs-tab` | Web |
| `stackdev db start\|stop\|psql\|logs\|backup\|restore [--archivo X]\|recreate` | Postgres |
| `stackdev emu start [--avd X] [--cold]\|stop\|switch\|kill\|cold` | Emulador |
| `stackdev app run\|stop\|analyze\|test\|format\|gen\|pub-get\|clean\|mode` | Flutter |
| `stackdev run "<comando>" --in api\|web\|movil\|raiz\|<carpeta>` | Cualquier comando en una carpeta (con las variables de la API si corresponde) |
| `stackdev tarea ["nombre"]` | Tarea de `dev-menu.json` (sin nombre: lista) |
| `stackdev cond [3 13 17]` | Condiciones del dispositivo (`0` restablece; sin números lista) |
| `stackdev status` | Estado una vez, sin panel |
| `stackdev logs api\|web\|db [-n 40] [-f]` | Últimas líneas del log |
| `stackdev detectar [ruta] [--guardar]` | Detecta y (opcional) crea `dev-menu.json`. Reconoce .NET, Angular y Flutter, y con perfiles JSON Node (Nest, Express, Fastify, Next, Nuxt, SvelteKit, Vite…) y Python (Django, FastAPI, Flask); los perfiles propios van en `%APPDATA%\stackdev\profiles\` (ver `CONTRIBUTING.md`) |
| `stackdev config [ruta]` | Muestra cómo se lee `dev-menu.json` |
| `stackdev estado` | Muestra dónde se guarda el estado personal, el último proyecto y el último emulador |

Códigos de salida: 0 bien · 1 falló · 2 uso incorrecto · 130 cancelado.

### Logs

API y web corren ocultas y escriben en `%APPDATA%\stackdev\logs\<proyecto>\api.log` / `web.log`. Se ven en el panel (última línea), con la acción «Ver logs aquí» (`a7`/`w7`, Ctrl+C para volver) o en una pestaña de Windows Terminal que sigue el log (`a8`/`w8`). `flutter run` conserva su propia pestaña.
Dónde se abren las pestañas lo decide `"terminal"` en `dev-menu.json`: `"auto"` (Windows Terminal si existe), `"wt"` o `"ventana"`.

Sin `[ruta]`: la carpeta actual si parece un proyecto (`.git`, `dev-menu.json`, `*.sln`, `pubspec.yaml`…), si no el último proyecto usado (pregunta), si no pide la ruta.

## Versión con ventana (`stackdev-gui`)

La misma lógica con interfaz gráfica (WPF, tema oscuro): tarjetas por servicio con su estado en vivo y sus botones, **Levantar todo / Verificar / Detener todo**, salida en un panel con colores, condiciones del dispositivo como opciones marcables, consola de comandos y configuración. «Cancelar acción» detiene lo que esté corriendo.

```powershell
dotnet run --project src/StackDev.Gui                     # probar
dotnet publish src/StackDev.Gui -c Release -r win-x64 --self-contained false -o publish\gui
publish\gui\stackdev-gui.exe C:\ruta\al\proyecto        # la ruta es opcional
```

Sin ruta abre la carpeta actual si parece un proyecto, o el último usado, o muestra el selector. Comparte con la versión de consola el estado (`%APPDATA%\stackdev`) y `dev-menu.json`.

## Estado personal

Va en `%APPDATA%\stackdev\` (no en el repo): último proyecto, último emulador, historial de comandos, condiciones del dispositivo y respaldos de la base de datos.

## `dev-menu.json`

Va en la raíz de cada proyecto; hay un ejemplo completo en [`examples/dev-menu.json`](examples/dev-menu.json). Se pueden agregar campos nuevos (se conservan al guardar). Cada sección es opcional: si falta, esa parte no aparece.

| Sección | Campos |
|---|---|
| `name` | Nombre del proyecto |
| `postgres` | `container`, `image`, `port`, `user`, `password`, `database`, `volume` (vacío = sin volumen con nombre) |
| `api` | `folder`, `url`, `open` (ruta que se abre en el navegador, p. ej. `/scalar`), `env`, `start`, `test`, `build` |
| `web` | `folder`, `url`, `start`, `test`, `build`, `install` |
| `mobile` | `folder`, `androidSdk`, `avd`, `argsRun`, `parse`, `test`, `format`, `generate` |
| `startAll` | Orden de "levantar todo": `postgres`, `api`, `web`, `emulador` |
| `tasks` | Lista de `{ name, folder, command }` |

- Las rutas son relativas a la raíz del repo; los comandos corren en la `folder` de su componente.
- `api.env` aplica a la API **y a cualquier comando dentro de la carpeta de la API** (así `dotnet ef` funciona sin preparar nada).
- `{?Texto}` dentro de un comando pide un valor al ejecutar la tarea; si se deja vacío, se cancela.
- Si editas el archivo, la herramienta lo recarga sola.

## Estructura

```
src/StackDev.Core   lógica (sin interfaz): configuración, detección, procesos, Docker, emulador, orquestación
src/StackDev.Cli    comandos y pantalla de consola (Spectre.Console)
src/StackDev.Gui    ventana (WPF)
tests/StackDev.Core.Tests   xUnit
```

## Servidores y despliegue de la API

Sección **Servidor** de la ventana. Todo corre por SSH desde tu PC y no depende de scripts instalados en el servidor.

| Acción | Qué hace |
|---|---|
| **Verificar y probar** | La primera vez muestra la huella SSH del servidor para que la compares; desde ahí solo se conecta si coincide (`known_hosts` propio, `StrictHostKeyChecking=yes`). |
| **Subir API** | Publica el proyecto web que detecta en la solución, revisa que no lleve archivos sensibles (`.env`, `appsettings.Development.json`, `.pdb`, claves, bases locales), pide confirmación escribiendo el nombre del servicio, sube un paquete con SHA-256, guarda una copia de la versión actual, reemplaza, espera a que el servicio esté sano y, si no lo está, vuelve solo a la versión anterior. |
| **Aprovisionar API nueva** | Crea usuario, carpetas, `.env` con secretos generados, servicio systemd, sitio de Apache o Nginx, firewall, SELinux y certificado HTTPS. |
| **Variables (.env)** | Edita el `.env` del servidor con los secretos ocultos; guarda copia y, si el servicio no arranca con los valores nuevos, restaura el anterior. |
| **Versiones anteriores** | Vuelve a cualquiera de las últimas versiones guardadas (se conservan 5). |

Requisitos en el servidor: el usuario SSH con `sudo` sin contraseña, `rsync` (recomendado) y el runtime de ASP.NET Core de la versión del proyecto.

En `dev-menu.json` el proyecto se enlaza con una sección `deployment`:

```json
"deployment": { "server": "a1b2c3d4", "service": "mi-api", "dll": "Mi.Api.dll", "domain": "mi-api.midominio.com", "project": "src/Mi.Api/Mi.Api.csproj" }
```

La lista de servidores se guarda cifrada (DPAPI) en `%APPDATA%\stackdev`, nunca en el repositorio. Los secretos no se escriben en ningún registro.

Prueba del script remoto (Linux, WSL o Git Bash): `bash tests/remote/remote-script.test.sh`.

### Archivos de versiones anteriores

Los servidores, las credenciales de prueba y los `dev-menu.json` guardados con claves en español (`nombre`, `usuario`, `clave`, `despliegue`…) se leen igual; al guardar se reescriben con las claves actuales en inglés.

### Asistente «Subir al servidor»

Elige el servidor, confirma su huella la primera vez, detecta la API del proyecto y revisa qué APIs ya hay instaladas en el servidor. Si hay alguna, la eliges (★ marca la que parece ser esta); si no hay ninguna, te guía para crear una nueva (proxy Apache/Nginx, HTTPS y servicio) y luego la sube.

### Crear una API nueva sin saber qué falta

Antes de crear nada, el asistente revisa el servidor y muestra una lista: servidor web (Apache/Nginx), runtime de ASP.NET Core que necesita tu proyecto, certbot, DNS del dominio, espacio en disco y si el nombre ya existe. Lo que se puede instalar con el gestor de paquetes del servidor (dnf, yum o apt) se instala con un botón; lo demás se explica con el comando para hacerlo a mano. El formulario de creación pide solo servicio, dominio, base de datos y HTTPS; el resto está en «Opciones avanzadas».

Si el servicio de tu proyecto aún no existe, «Subir API» lo detecta antes de compilar y ofrece crearlo y seguir con la subida. Los archivos de desarrollo del publish (`appsettings.Development.json`, `.pdb`) se dejan fuera sin preguntar; llaves, `.env` y bases de datos sí piden confirmación.

Para no depender de la consola: la dirección pública viene resuelta con una dirección gratuita (`servicio.IP.sslip.io`) que ya apunta al servidor, el correo del certificado es opcional, la base de datos se elige según los paquetes y la cadena de conexión de tu proyecto, y se comprueba que los puertos 80 y 443 respondan desde internet. Si el certificado queda pendiente, la app explica el motivo (DNS, proxy o reglas de red de la nube) y ofrece «reintentar» cuando lo resuelvas.

### Claves y base de datos de cada API

- Con PostgreSQL en el mismo servidor, deja vacía la cadena de conexión: StackDev instala PostgreSQL si falta y crea un usuario (`<api>_app`) y una base de datos propios de esa API, con una clave aleatoria.
- Todas las claves que genera (base de datos, administrador, clave JWT, variables que faltaban) se guardan cifradas en la sección «Credenciales» del proyecto, con el nombre «`<api> @ <servidor>` · …». En el servidor solo viven en el `.env` de la API.
- Si la API no arranca por variables que faltan, StackDev las detecta en el registro del servicio, crea las que son claves, y para el resto (correo, pagos…) te da los pasos para ponerlas en «Variables (.env)».

### Comprobar y poner en marcha

El botón principal de la tarjeta del servidor abre **una sola ventana** (la pantalla de atrás se desenfoca) con los pasos: servidor, configuración de la API, arranque y comprobación final. Si falta algo, las preguntas salen dentro de la misma ventana: las claves y la base de datos se crean solas, y los valores que solo tú sabes se escriben ahí mismo. Al terminar muestra dónde corre la API (dirección y servidor) y la tarjeta lo deja visible. «Subir API» usa la misma ventana. El resto de acciones (variables, versiones anteriores, detener…) están en «Más acciones».

### Direcciones y pruebas

El botón «Direcciones y pruebas» muestra en una sola ventana todas las direcciones del proyecto: la API y la web locales (las de `dev-menu.json`, con su estado) y la API del servidor (`https://…`). Cada una tiene **Abrir** (elige entre las páginas que existen: documentación, salud, principal), **Probar** (qué responde, con código y tiempo) y **Copiar dirección**.
