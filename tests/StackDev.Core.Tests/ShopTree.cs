using StackDev.Core.Config;

namespace StackDev.Core.Tests;

/// <summary>Árbol de carpetas falso con la estructura de "shop" en una carpeta temporal.</summary>
public sealed class ShopTree : IDisposable
{
    public string Base { get; }
    public string Root { get; }

    public ShopTree(bool withSnapshotInOtherProject = true, string manager = "pnpm-lock.yaml")
    {
        Base = Path.Combine(Path.GetTempPath(), "stackdev-tests-" + Guid.NewGuid().ToString("N"));
        Root = Path.Combine(Base, "shop");
        Directory.CreateDirectory(Path.Combine(Root, ".git"));

        // API
        Write("api/Shop.sln", "");
        Write("api/src/Shop.Api/Shop.Api.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="9.0.0" />
                <PackageReference Include="Scalar.AspNetCore" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """);
        Write("api/src/Shop.Api/Properties/launchSettings.json", """
            { "profiles": { "https": { "applicationUrl": "https://localhost:7109;http://localhost:5109" } } }
            """);
        Write("api/src/Shop.Infrastructure/Shop.Infrastructure.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("api/src/Shop.Infrastructure/Db.cs", "var c = config.GetConnectionString(\"Shop\");");
        if (withSnapshotInOtherProject)
            Write("api/src/Shop.Infrastructure/Migrations/ShopDbContextModelSnapshot.cs", "// snapshot");
        Write("api/tests/Shop.Api.Tests/Shop.Api.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        // basura que debe ignorarse
        Write("api/src/Shop.Api/bin/Debug/Otro.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");

        // Móvil
        Write("mobile/pubspec.yaml", """
            name: shop
            dependencies:
              flutter:
                sdk: flutter
              drift: ^2.0.0
            dev_dependencies:
              build_runner: ^2.4.0
            """);
        Directory.CreateDirectory(Path.Combine(Root, "mobile", "test"));
        Write("mobile/android/app/pubspec.yaml", "dependencies:\n  flutter:\n    sdk: flutter\n"); // en 'android': se salta

        // Web
        Write("web/package.json", "{ \"dependencies\": { \"@angular/core\": \"^20.0.0\" }, \"scripts\": { \"start\": \"ng serve\" } }");
        Write("web/" + manager, "");
        Write("web/node_modules/otro/package.json", "{ \"dependencies\": { \"@angular/core\": \"1\" } }");
    }

    public string FilePath(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Write(string relative, string content)
    {
        var path = FilePath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        try { Directory.Delete(Base, recursive: true); } catch (IOException) { }
    }

    /// <summary>dev-menu.json tal como está hoy en shop (copiado del enunciado).</summary>
    public const string CurrentConfig = """
        {
          "name": "shop",
          "postgres": {
            "container": "shop-pg",
            "image": "postgres:17-alpine",
            "port": 5440,
            "user": "shop",
            "password": "dev",
            "database": "shop",
            "volume": ""
          },
          "api": {
            "folder": "api",
            "url": "http://localhost:5109",
            "open": "/scalar",
            "env": { "ConnectionStrings__Shop": "Host=127.0.0.1;Port=5440;Database=shop;Username=shop;Password=dev" },
            "start": "dotnet run --project src/Shop.Api",
            "test": "dotnet test --artifacts-path .art",
            "build": "dotnet build --artifacts-path .art"
          },
          "web": {
            "folder": "web",
            "url": "http://localhost:4200",
            "start": "pnpm start",
            "test": "pnpm test --watch=false",
            "build": "pnpm build",
            "install": "pnpm install"
          },
          "mobile": {
            "folder": "mobile",
            "androidSdk": "C:\\Android\\Sdk",
            "avd": "",
            "argsRun": "",
            "parse": "flutter analyze",
            "test": "flutter test",
            "format": "dart format lib test",
            "generate": "dart run build_runner build --delete-conflicting-outputs"
          },
          "startAll": ["postgres", "api", "web", "emulador"],
          "tasks": [
            { "name": "EF: aplicar migraciones", "folder": "api", "command": "dotnet ef database update --project src/Shop.Infrastructure --startup-project src/Shop.Api" },
            { "name": "EF: nueva migracion", "folder": "api", "command": "dotnet ef migrations add {?Nombre de la migracion} --project src/Shop.Infrastructure --startup-project src/Shop.Api" },
            { "name": "Flutter: compilar APK (release)", "folder": "mobile", "command": "flutter build apk --release" }
          ]
        }
        """;
}
