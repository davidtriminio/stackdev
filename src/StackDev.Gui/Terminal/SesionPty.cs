using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using StackDev.Core.Proc;

namespace StackDev.Gui.Terminal;

/// <summary>
/// Un programa (normalmente un shell) corriendo dentro de una pseudoconsola de Windows (ConPTY, Windows 10 1809 o superior).
/// Lo que escribe llega por <see cref="Salida"/> con las secuencias de escape tal cual, listas para un emulador como xterm.js.
/// </summary>
public sealed class SesionPty : IDisposable
{
    // ------------------------------------------------------------ Windows

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private static readonly IntPtr AtributoPseudoConsola = (IntPtr)0x00020016;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle lee, out SafeFileHandle escribe, IntPtr atributos, uint tamano);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(COORD tamano, SafeFileHandle entrada, SafeFileHandle salida, uint flags, out IntPtr hPC);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD tamano);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lista, int cantidad, int flags, ref IntPtr tamano);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lista, uint flags, IntPtr atributo, IntPtr valor, IntPtr tamano, IntPtr previo, IntPtr retorno);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lista);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? aplicacion, StringBuilder lineaComando, IntPtr atribProceso, IntPtr atribHilo,
        bool heredar, uint flags, IntPtr entorno, string? carpeta, ref STARTUPINFOEX inicio, out PROCESS_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr h, uint ms);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr h, out uint codigo);

    // ------------------------------------------------------------ Estado

    private readonly IntPtr _hPC;
    private readonly IntPtr _hProceso;
    private readonly FileStream _entrada;
    private readonly FileStream _salida;
    private readonly object _bloqueoEscritura = new();
    private volatile bool _cerrado;

    public int Pid { get; }

    /// <summary>Texto que produce el programa. Llega desde un hilo de fondo.</summary>
    public event Action<string>? Salida;

    /// <summary>El programa terminó (con su código de salida). Llega desde un hilo de fondo; no se dispara tras <see cref="Dispose"/>.</summary>
    public event Action<int>? Terminado;

    private SesionPty(IntPtr hPC, IntPtr hProceso, int pid, FileStream entrada, FileStream salida)
    {
        _hPC = hPC;
        _hProceso = hProceso;
        Pid = pid;
        _entrada = entrada;
        _salida = salida;
    }

    /// <summary>Lanza <paramref name="lineaComando"/> en <paramref name="carpeta"/> con el entorno actual más <paramref name="env"/>.</summary>
    public static SesionPty Iniciar(string lineaComando, string carpeta, IReadOnlyDictionary<string, string>? env, int columnas, int filas)
    {
        if (!CreatePipe(out var entradaLee, out var entradaEscribe, IntPtr.Zero, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!CreatePipe(out var salidaLee, out var salidaEscribe, IntPtr.Zero, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());

        var tamano = new COORD { X = (short)columnas, Y = (short)filas };
        var hr = CreatePseudoConsole(tamano, entradaLee, salidaEscribe, 0, out var hPC);
        if (hr != 0) throw new Win32Exception(hr, "No se pudo crear la pseudoconsola (hace falta Windows 10 1809 o superior).");
        // la pseudoconsola ya tiene su copia de estos dos extremos
        entradaLee.Dispose();
        salidaEscribe.Dispose();

        var lista = IntPtr.Zero;
        var bloqueEntorno = IntPtr.Zero;
        try
        {
            var inicio = new STARTUPINFOEX();
            inicio.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

            var bytes = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);   // falla a propósito: solo devuelve el tamaño
            lista = Marshal.AllocHGlobal(bytes);
            if (!InitializeProcThreadAttributeList(lista, 1, 0, ref bytes)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!UpdateProcThreadAttribute(lista, 0, AtributoPseudoConsola, hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            inicio.lpAttributeList = lista;

            bloqueEntorno = CrearBloqueEntorno(env);
            var comando = new StringBuilder(lineaComando);
            var dir = Directory.Exists(carpeta) ? carpeta : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!CreateProcessW(null, comando, IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    bloqueEntorno, dir, ref inicio, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"No se pudo iniciar: {lineaComando}");
            CloseHandle(info.hThread);

            var entrada = new FileStream(entradaEscribe, FileAccess.Write, 4096, false);
            var salida = new FileStream(salidaLee, FileAccess.Read, 4096, false);
            var sesion = new SesionPty(hPC, info.hProcess, info.dwProcessId, entrada, salida);
            sesion.EmpezarALeer();
            return sesion;
        }
        catch
        {
            ClosePseudoConsole(hPC);
            entradaEscribe.Dispose();
            salidaLee.Dispose();
            throw;
        }
        finally
        {
            if (lista != IntPtr.Zero) { DeleteProcThreadAttributeList(lista); Marshal.FreeHGlobal(lista); }
            if (bloqueEntorno != IntPtr.Zero) Marshal.FreeHGlobal(bloqueEntorno);
        }
    }

    private static IntPtr CrearBloqueEntorno(IReadOnlyDictionary<string, string>? extra)
    {
        var d = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && k.Length > 0) d[k] = e.Value as string ?? "";
        if (extra is not null) foreach (var (k, v) in extra) d[k] = v;
        d["COLORTERM"] = "truecolor";

        var sb = new StringBuilder();
        foreach (var (k, v) in d) sb.Append(k).Append('=').Append(v).Append('\0');
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    // ------------------------------------------------------------ Hilos

    private void EmpezarALeer()
    {
        new Thread(Leer) { IsBackground = true, Name = "pty-lectura" }.Start();
        new Thread(Esperar) { IsBackground = true, Name = "pty-espera" }.Start();
    }

    private void Leer()
    {
        var buffer = new byte[16384];
        var decodificador = new UTF8Encoding(false).GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        try
        {
            while (true)
            {
                var n = _salida.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;
                var c = decodificador.GetChars(buffer, 0, n, chars, 0, false);
                if (c > 0) Salida?.Invoke(new string(chars, 0, c));
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally { try { _salida.Dispose(); } catch (IOException) { } }
    }

    private void Esperar()
    {
        WaitForSingleObject(_hProceso, 0xFFFFFFFF);
        GetExitCodeProcess(_hProceso, out var codigo);
        CloseHandle(_hProceso);
        if (!_cerrado) Terminado?.Invoke((int)codigo);
    }

    // ------------------------------------------------------------ Uso

    public void Escribir(string texto)
    {
        if (_cerrado || texto.Length == 0) return;
        var bytes = Encoding.UTF8.GetBytes(texto);
        lock (_bloqueoEscritura)
        {
            try { _entrada.Write(bytes, 0, bytes.Length); _entrada.Flush(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    public void Redimensionar(int columnas, int filas)
    {
        if (_cerrado || columnas < 2 || filas < 1) return;
        ResizePseudoConsole(_hPC, new COORD { X = (short)columnas, Y = (short)filas });
    }

    public void Dispose()
    {
        if (_cerrado) return;
        _cerrado = true;
        ProcessRunner.KillTree(Pid);
        Task.Run(() =>
        {
            try { ClosePseudoConsole(_hPC); } catch (Exception) { }
            try { _entrada.Dispose(); } catch (IOException) { }
        });
    }
}
