using StackDev.Core.Config;

namespace StackDev.Core.Tests;

/// <summary>Árbol de carpetas falso con la estructura de "pulperia" en una carpeta temporal.</summary>
public sealed class ArbolPulperia : IDisposable
{
    public string Base { get; }
    public string Raiz { get; }

    public ArbolPulperia(bool conSnapshotEnOtroProyecto = true, string gestor = "pnpm-lock.yaml")
    {
        Base = Path.Combine(Path.GetTempPath(), "stackdev-tests-" + Guid.NewGuid().ToString("N"));
        Raiz = Path.Combine(Base, "pulperia");
        Directory.CreateDirectory(Path.Combine(Raiz, ".git"));

        // API
        Escribir("api/Pulperia.sln", "");
        Escribir("api/src/Pulperia.Api/Pulperia.Api.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="9.0.0" />
                <PackageReference Include="Scalar.AspNetCore" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """);
        Escribir("api/src/Pulperia.Api/Properties/launchSettings.json", """
            { "profiles": { "https": { "applicationUrl": "https://localhost:7109;http://localhost:5109" } } }
            """);
        Escribir("api/src/Pulperia.Infrastructure/Pulperia.Infrastructure.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Escribir("api/src/Pulperia.Infrastructure/Db.cs", "var c = config.GetConnectionString(\"Pulperia\");");
        if (conSnapshotEnOtroProyecto)
            Escribir("api/src/Pulperia.Infrastructure/Migrations/PulperiaDbContextModelSnapshot.cs", "// snapshot");
        Escribir("api/tests/Pulperia.Api.Tests/Pulperia.Api.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        // basura que debe ignorarse
        Escribir("api/src/Pulperia.Api/bin/Debug/Otro.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");

        // Móvil
        Escribir("mobile/pubspec.yaml", """
            name: pulperia
            dependencies:
              flutter:
                sdk: flutter
              drift: ^2.0.0
            dev_dependencies:
              build_runner: ^2.4.0
            """);
        Directory.CreateDirectory(Path.Combine(Raiz, "mobile", "test"));
        Escribir("mobile/android/app/pubspec.yaml", "dependencies:\n  flutter:\n    sdk: flutter\n"); // en 'android': se salta

        // Web
        Escribir("web/package.json", "{ \"dependencies\": { \"@angular/core\": \"^20.0.0\" }, \"scripts\": { \"start\": \"ng serve\" } }");
        Escribir("web/" + gestor, "");
        Escribir("web/node_modules/otro/package.json", "{ \"dependencies\": { \"@angular/core\": \"1\" } }");
    }

    public string Ruta(string relativa) => Path.Combine(Raiz, relativa.Replace('/', Path.DirectorySeparatorChar));

    public void Escribir(string relativa, string contenido)
    {
        var ruta = Ruta(relativa);
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        File.WriteAllText(ruta, contenido);
    }

    public void Dispose()
    {
        try { Directory.Delete(Base, recursive: true); } catch (IOException) { }
    }

    /// <summary>dev-menu.json tal como está hoy en pulperia (copiado del enunciado).</summary>
    public const string ConfigActual = """
        {
          "nombre": "pulperia",
          "postgres": {
            "contenedor": "pulperia-pg",
            "imagen": "postgres:17-alpine",
            "puerto": 5440,
            "usuario": "pulperia",
            "password": "dev",
            "baseDatos": "pulperia",
            "volumen": ""
          },
          "api": {
            "carpeta": "api",
            "url": "http://localhost:5109",
            "abrir": "/scalar",
            "env": { "ConnectionStrings__Pulperia": "Host=127.0.0.1;Port=5440;Database=pulperia;Username=pulperia;Password=dev" },
            "iniciar": "dotnet run --project src/Pulperia.Api",
            "test": "dotnet test --artifacts-path .art",
            "build": "dotnet build --artifacts-path .art"
          },
          "web": {
            "carpeta": "web",
            "url": "http://localhost:4200",
            "iniciar": "pnpm start",
            "test": "pnpm test --watch=false",
            "build": "pnpm build",
            "instalar": "pnpm install"
          },
          "movil": {
            "carpeta": "mobile",
            "androidSdk": "V:\\Programas\\Android\\Sdk",
            "avd": "",
            "argsRun": "",
            "analizar": "flutter analyze",
            "test": "flutter test",
            "formato": "dart format lib test",
            "generar": "dart run build_runner build --delete-conflicting-outputs"
          },
          "levantarTodo": ["postgres", "api", "web", "emulador"],
          "tareas": [
            { "nombre": "EF: aplicar migraciones", "carpeta": "api", "comando": "dotnet ef database update --project src/Pulperia.Infrastructure --startup-project src/Pulperia.Api" },
            { "nombre": "EF: nueva migracion", "carpeta": "api", "comando": "dotnet ef migrations add {?Nombre de la migracion} --project src/Pulperia.Infrastructure --startup-project src/Pulperia.Api" },
            { "nombre": "Flutter: compilar APK (release)", "carpeta": "mobile", "comando": "flutter build apk --release" }
          ]
        }
        """;
}
