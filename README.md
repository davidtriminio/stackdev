# stackdev

Herramienta de desarrollo local para proyectos con **API .NET + web Angular + app Flutter + Postgres en Docker**.
Reemplaza a `flutter.ps1` (que sigue funcionando, no se borra) con una herramienta en C# que se instala como *dotnet global tool*.

> **Estado: las 7 fases escritas.** Falta la prueba real en Windows (el código se compiló y probó en Linux; el panel, Docker, emulador y ventanas no se han podido ejecutar fuera de tu máquina).

## Requisitos

- Windows 10/11 · .NET SDK 10 (`dotnet --list-sdks` debe mostrar `10.x`)
- Para el resto: Docker Desktop, Android SDK, Flutter, pnpm/npm

## Compilar y probar

```powershell
cd V:\dev\tools\fullstack-tools\stackdev
dotnet test                                   # tests del núcleo
dotnet run --project src/StackDev.Cli -- config V:\ruta\a\pulperia
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
| `stackdev detectar [ruta] [--guardar]` | Detecta y (opcional) crea `dev-menu.json` |
| `stackdev config [ruta]` | Muestra cómo se lee `dev-menu.json` |
| `stackdev estado [--sincronizar]` | Estado personal y migración desde `flutter-dev` |

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
publish\gui\stackdev-gui.exe V:\ruta\al\proyecto        # la ruta es opcional
```

Sin ruta abre la carpeta actual si parece un proyecto, o el último usado, o muestra el selector. Comparte con la versión de consola el estado (`%APPDATA%\stackdev`) y `dev-menu.json`.

## Estado personal

Va en `%APPDATA%\stackdev\` (no en el repo). La primera vez se **copia** lo que haya en `%APPDATA%\flutter-dev\` (último proyecto, último AVD, historial, condiciones, respaldos); el origen no se toca, así `flutter.ps1` sigue funcionando. Mientras uses los dos, `stackdev estado --sincronizar` trae lo más nuevo.

## `dev-menu.json`

Está en la raíz del repo. Es el mismo formato de siempre; se pueden agregar campos nuevos (se conservan al guardar). Cada sección es opcional: si falta, esa parte no aparece.

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
