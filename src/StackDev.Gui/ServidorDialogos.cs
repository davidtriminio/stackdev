using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StackDev.Core.Config;
using StackDev.Core.Servidores;

namespace StackDev.Gui;

/// <summary>Ventanas para agregar o editar un servidor y para decir a qué servidor y servicio se sube un proyecto.</summary>
public static class ServidorDialogos
{
    private static Brush P(string clave) => (Brush)Application.Current.FindResource(clave);
    private static Style Estilo(string clave) => (Style)Application.Current.FindResource(clave);

    private static Window Base(Window? owner, string titulo, double ancho)
    {
        var w = new Window
        {
            Title = titulo,
            Owner = owner,
            Width = ancho,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        BarraTitulo.Aplicar(w);
        return w;
    }

    private static TextBox Campo(Panel panel, string etiqueta, string valor, string? ayuda = null)
    {
        panel.Children.Add(new TextBlock { Text = etiqueta, FontSize = 11, Foreground = P("MutedBrush"), Margin = new Thickness(0, 0, 0, 3) });
        var caja = new TextBox { Text = valor, Margin = new Thickness(0, 0, 0, ayuda is null ? 12 : 2) };
        panel.Children.Add(caja);
        if (ayuda is not null)
            panel.Children.Add(new TextBlock { Text = ayuda, FontSize = 11, Foreground = P("MutedBrush"), Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap });
        return caja;
    }

    private static Button Boton(string texto, bool primario)
    {
        var b = new Button { Content = texto, MinWidth = 90, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(8, 0, 0, 0) };
        if (primario) { b.Style = Estilo("BtnPrimary"); b.IsDefault = true; } else b.IsCancel = true;
        return b;
    }

    private static StackPanel FilaBotones(params Button[] botones)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var b in botones) p.Children.Add(b);
        return p;
    }

    private static TextBlock Error() => new()
    {
        Foreground = P("ErrBrush"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4), Visibility = Visibility.Collapsed,
    };

    /// <summary>Agrega (actual = null) o edita un servidor. Devuelve null si se cancela.</summary>
    public static Servidor? EditarServidor(Window? owner, Servidor? actual)
    {
        var s = actual is null
            ? new Servidor()
            : new Servidor { Id = actual.Id, Nombre = actual.Nombre, Host = actual.Host, Usuario = actual.Usuario, Puerto = actual.Puerto, Clave = actual.Clave, Web = actual.Web };
        var w = Base(owner, actual is null ? "Agregar servidor" : "Editar servidor", 560);
        var raiz = new StackPanel { Margin = new Thickness(22) };

        var nombre = Campo(raiz, "Nombre (para reconocerlo)", s.Nombre, "Por ejemplo: Oracle producción, VPS clientes.");
        var host = Campo(raiz, "IP o dominio del servidor", s.Host, "Lo ves en la consola de tu proveedor (Oracle, AWS, etc.) > Instancias > IP pública.");
        var usuario = Campo(raiz, "Usuario SSH", s.Usuario, "Típicos: opc (Oracle Linux), ubuntu (Ubuntu), ec2-user (Amazon Linux), root.");
        var puerto = Campo(raiz, "Puerto SSH", s.Puerto.ToString());

        raiz.Children.Add(new TextBlock { Text = "Clave SSH (el archivo privado, no el .pub)", FontSize = 11, Foreground = P("MutedBrush"), Margin = new Thickness(0, 0, 0, 3) });
        var filaClave = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        filaClave.ColumnDefinitions.Add(new ColumnDefinition());
        filaClave.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var clave = new TextBox { Text = s.Clave };
        var examinar = new Button { Content = "Examinar…", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 7, 14, 7) };
        Grid.SetColumn(examinar, 1);
        filaClave.Children.Add(clave);
        filaClave.Children.Add(examinar);
        raiz.Children.Add(filaClave);
        examinar.Click += (_, _) =>
        {
            var inicio = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Elige la clave SSH privada", InitialDirectory = Directory.Exists(inicio) ? inicio : null };
            if (dlg.ShowDialog(w) == true) clave.Text = dlg.FileName;
        };

        raiz.Children.Add(new TextBlock { Text = "Servidor web (proxy hacia tus APIs)", FontSize = 11, Foreground = P("MutedBrush"), Margin = new Thickness(0, 0, 0, 3) });
        var nginx = string.Equals(s.Web, "nginx", StringComparison.OrdinalIgnoreCase);
        var web = new Button { HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left, MinWidth = 160, Margin = new Thickness(0, 0, 0, 6) };
        void PonerWeb() => web.Content = (nginx ? "Nginx" : "Apache") + "   ▾";
        PonerWeb();
        web.Click += (_, _) => { nginx = !nginx; PonerWeb(); };
        raiz.Children.Add(web);
        raiz.Children.Add(new TextBlock { Text = "Clic para alternar entre Apache y Nginx.", FontSize = 11, Foreground = P("MutedBrush") });

        var error = Error();
        raiz.Children.Add(error);
        var ok = Boton("Guardar", true);
        var cancelar = Boton("Cancelar", false);
        Servidor? resultado = null;
        ok.Click += (_, _) =>
        {
            if (nombre.Text.Trim().Length == 0 || host.Text.Trim().Length == 0 || usuario.Text.Trim().Length == 0)
            { error.Text = "Escribe el nombre, el host y el usuario."; error.Visibility = Visibility.Visible; return; }
            if (!int.TryParse(puerto.Text.Trim(), out var pto) || pto is < 1 or > 65535)
            { error.Text = "El puerto debe ser un número entre 1 y 65535."; error.Visibility = Visibility.Visible; return; }
            s.Nombre = nombre.Text.Trim();
            s.Host = host.Text.Trim();
            s.Usuario = usuario.Text.Trim();
            s.Puerto = pto;
            s.Clave = clave.Text.Trim().Trim('"');
            s.Web = nginx ? "nginx" : "apache";
            resultado = s;
            w.Close();
        };
        cancelar.Click += (_, _) => w.Close();
        raiz.Children.Add(FilaBotones(cancelar, ok));
        w.Content = raiz;
        w.Loaded += (_, _) => nombre.Focus();
        w.ShowDialog();
        return resultado;
    }

    /// <summary>Elige a qué servidor y servicio se sube la API de un proyecto. Devuelve null si se cancela.</summary>
    public static DespliegueConfig? EditarDespliegue(Window? owner, IReadOnlyList<Servidor> servidores, DespliegueConfig? actual, string sugerenciaServicio, string sugerenciaDll)
    {
        if (servidores.Count == 0)
        {
            Dialogos.Confirmar(owner, "Primero agrega un servidor (botón «Agregar servidor»).", true);
            return null;
        }
        var w = Base(owner, "Despliegue de este proyecto", 560);
        var raiz = new StackPanel { Margin = new Thickness(22) };

        var indice = Math.Max(0, servidores.ToList().FindIndex(x => x.Id == actual?.Servidor));
        raiz.Children.Add(new TextBlock { Text = "Servidor", FontSize = 11, Foreground = P("MutedBrush"), Margin = new Thickness(0, 0, 0, 3) });
        var botonServidor = new Button { HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left, MinWidth = 260, Margin = new Thickness(0, 0, 0, 12) };
        void PonerServidor() => botonServidor.Content = servidores[indice].Nombre + "   ▾";
        PonerServidor();
        botonServidor.Click += (_, _) =>
        {
            var i = Dialogos.Elegir(w, "¿A qué servidor se sube?", servidores.Select(x => $"{x.Nombre}   ·   {x.Destino}").ToList(), indice);
            if (i >= 0) { indice = i; PonerServidor(); }
        };
        raiz.Children.Add(botonServidor);

        var servicio = Campo(raiz, "Nombre del servicio en el servidor", actual?.Servicio ?? sugerenciaServicio,
            "El que usaste en provision-api.sh: minúsculas, números y guiones. Es el servicio systemd y la carpeta en /var/www.");
        var dll = Campo(raiz, "Archivo .dll que arranca la API", actual?.Dll ?? sugerenciaDll);
        var dominio = Campo(raiz, "Dominio público de la API (opcional)", actual?.Dominio ?? "",
            "Sin https://. Sirve para comprobar que responde y cuándo vence el certificado.");

        var error = Error();
        raiz.Children.Add(error);
        var ok = Boton("Guardar", true);
        var cancelar = Boton("Cancelar", false);
        DespliegueConfig? resultado = null;
        ok.Click += (_, _) =>
        {
            var svc = servicio.Text.Trim();
            if (!ServidorService.NombreServicio.IsMatch(svc))
            { error.Text = "El servicio debe empezar con una letra minúscula y usar solo minúsculas, números y guiones (ej. mi-api)."; error.Visibility = Visibility.Visible; return; }
            resultado = new DespliegueConfig
            {
                Servidor = servidores[indice].Id,
                Servicio = svc,
                Dll = dll.Text.Trim().Length == 0 ? null : dll.Text.Trim(),
                Dominio = ServidorService.LimpiarDominio(dominio.Text),
                Extra = actual?.Extra,
            };
            w.Close();
        };
        cancelar.Click += (_, _) => w.Close();
        raiz.Children.Add(FilaBotones(cancelar, ok));
        w.Content = raiz;
        w.ShowDialog();
        return resultado;
    }
}
