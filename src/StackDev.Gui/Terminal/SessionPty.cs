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
/// A program (normally a shell) running inside a Windows pseudoconsole (ConPTY, Windows 10 1809 or later).
/// What it writes arrives through <see cref="Output"/> with the escape sequences untouched, ready for an emulator such as xterm.js.
/// </summary>
public sealed class SessionPty : IDisposable
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
    private static readonly IntPtr PseudoConsoleAttribute = (IntPtr)0x00020016;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle reads, out SafeFileHandle writes, IntPtr attributes, uint size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr hPC);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int amount, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr val, IntPtr size, IntPtr previous, IntPtr returned);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? application, StringBuilder lineCommand, IntPtr attrProcess, IntPtr attrThread,
        bool inherit, uint flags, IntPtr environment, string? folder, ref STARTUPINFOEX home, out PROCESS_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr h, uint ms);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr h, out uint code);

    // ------------------------------------------------------------ Estado

    private readonly IntPtr _hPC;
    private readonly IntPtr _hProcess;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly object _writeLock = new();
    private volatile bool _closed;

    public int Pid { get; }

    /// <summary>Text the program produces. Arrives from a background thread.</summary>
    public event Action<string>? Output;

    /// <summary>The program ended (with its exit code). Arrives from a background thread; it does not fire after <see cref="Dispose"/>.</summary>
    public event Action<int>? Finished;

    private SessionPty(IntPtr hPC, IntPtr hProcess, int pid, FileStream input, FileStream output)
    {
        _hPC = hPC;
        _hProcess = hProcess;
        Pid = pid;
        _input = input;
        _output = output;
    }

    /// <summary>Launches <paramref name="lineCommand"/> in <paramref name="folder"/> with the current environment plus <paramref name="env"/>.</summary>
    public static SessionPty Start(string lineCommand, string folder, IReadOnlyDictionary<string, string>? env, int columns, int rows)
    {
        if (!CreatePipe(out var inputReads, out var inputWrites, IntPtr.Zero, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!CreatePipe(out var outputReads, out var outputWrites, IntPtr.Zero, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());

        var size = new COORD { X = (short)columns, Y = (short)rows };
        var hr = CreatePseudoConsole(size, inputReads, outputWrites, 0, out var hPC);
        if (hr != 0) throw new Win32Exception(hr, "No se pudo crear la pseudoconsola (hace falta Windows 10 1809 o superior).");
        // the pseudoconsole already has its own copy of these two ends
        inputReads.Dispose();
        outputWrites.Dispose();

        var list = IntPtr.Zero;
        var blockEnvironment = IntPtr.Zero;
        try
        {
            var home = new STARTUPINFOEX();
            home.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

            var bytes = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);   // fails on purpose: it only returns the size
            list = Marshal.AllocHGlobal(bytes);
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref bytes)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!UpdateProcThreadAttribute(list, 0, PseudoConsoleAttribute, hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            home.lpAttributeList = list;

            blockEnvironment = CreateEnvironmentBlock(env);
            var command = new StringBuilder(lineCommand);
            var dir = Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    blockEnvironment, dir, ref home, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"No se pudo iniciar: {lineCommand}");
            CloseHandle(info.hThread);

            var input = new FileStream(inputWrites, FileAccess.Write, 4096, false);
            var output = new FileStream(outputReads, FileAccess.Read, 4096, false);
            var session = new SessionPty(hPC, info.hProcess, info.dwProcessId, input, output);
            session.StartReading();
            return session;
        }
        catch
        {
            ClosePseudoConsole(hPC);
            inputWrites.Dispose();
            outputReads.Dispose();
            throw;
        }
        finally
        {
            if (list != IntPtr.Zero) { DeleteProcThreadAttributeList(list); Marshal.FreeHGlobal(list); }
            if (blockEnvironment != IntPtr.Zero) Marshal.FreeHGlobal(blockEnvironment);
        }
    }

    private static IntPtr CreateEnvironmentBlock(IReadOnlyDictionary<string, string>? extra)
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

    private void StartReading()
    {
        new Thread(Read) { IsBackground = true, Name = "pty-lectura" }.Start();
        new Thread(Wait) { IsBackground = true, Name = "pty-espera" }.Start();
    }

    private void Read()
    {
        var buffer = new byte[16384];
        var decoder = new UTF8Encoding(false).GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        try
        {
            while (true)
            {
                var n = _output.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;
                var c = decoder.GetChars(buffer, 0, n, chars, 0, false);
                if (c > 0) Output?.Invoke(new string(chars, 0, c));
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally { try { _output.Dispose(); } catch (IOException) { } }
    }

    private void Wait()
    {
        WaitForSingleObject(_hProcess, 0xFFFFFFFF);
        GetExitCodeProcess(_hProcess, out var code);
        CloseHandle(_hProcess);
        if (!_closed) Finished?.Invoke((int)code);
    }

    // ------------------------------------------------------------ Uso

    public void Write(string text)
    {
        if (_closed || text.Length == 0) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        lock (_writeLock)
        {
            try { _input.Write(bytes, 0, bytes.Length); _input.Flush(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    public void Resize(int columns, int rows)
    {
        if (_closed || columns < 2 || rows < 1) return;
        ResizePseudoConsole(_hPC, new COORD { X = (short)columns, Y = (short)rows });
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        ProcessRunner.KillTree(Pid);
        Task.Run(() =>
        {
            try { ClosePseudoConsole(_hPC); } catch (Exception) { }
            try { _input.Dispose(); } catch (IOException) { }
        });
    }
}
