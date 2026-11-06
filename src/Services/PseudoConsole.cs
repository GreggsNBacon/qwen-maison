using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Maison.Services;

/// <summary>
/// Runs a process attached to a Windows pseudo console (ConPTY), so full-screen TUIs such as
/// Claude Code and Qwen Code work exactly like in Windows Terminal. Output is VT text for xterm.js.
/// </summary>
public sealed class PseudoConsole : IDisposable
{
    private IntPtr _hpc;
    private readonly SafeFileHandle _inputWrite, _outputRead;
    private readonly IntPtr _process, _thread;
    private readonly FileStream _input;
    private readonly Thread _reader;
    private bool _disposed;

    /// <summary>Raised on a background thread with decoded output text.</summary>
    public event Action<string>? Output;
    /// <summary>Raised on a background thread when the process exits.</summary>
    public event Action<int>? Exited;

    public int ProcessId { get; }

    private PseudoConsole(IntPtr hpc, SafeFileHandle inputWrite, SafeFileHandle outputRead, PROCESS_INFORMATION pi)
    {
        _hpc = hpc;
        _inputWrite = inputWrite;
        _outputRead = outputRead;
        _process = pi.hProcess;
        _thread = pi.hThread;
        ProcessId = pi.dwProcessId;
        _input = new FileStream(_inputWrite, FileAccess.Write, 1);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "conpty-reader" };
        _reader.Start();
        new Thread(WaitLoop) { IsBackground = true, Name = "conpty-wait" }.Start();
    }

    public static PseudoConsole Start(string commandLine, string workDir, short cols, short rows, IDictionary<string, string>? env = null)
    {
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0)) throw new Win32Exception();

        int hr = CreatePseudoConsole(new COORD { X = cols, Y = rows }, inputRead, outputWrite, 0, out var hpc);
        if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole failed");

        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        si.lpAttributeList = Marshal.AllocHGlobal(size);
        IntPtr envBlock = IntPtr.Zero;
        try
        {
            if (!InitializeProcThreadAttributeList(si.lpAttributeList, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(si.lpAttributeList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            envBlock = Marshal.StringToHGlobalUni(BuildEnvironment(env));
            if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, envBlock, workDir, ref si, out var pi))
                throw new Win32Exception();

            // The child owns these ends now.
            inputRead.Dispose();
            outputWrite.Dispose();
            return new PseudoConsole(hpc, inputWrite, outputRead, pi);
        }
        catch
        {
            ClosePseudoConsole(hpc);
            inputRead.Dispose(); inputWrite.Dispose(); outputRead.Dispose(); outputWrite.Dispose();
            throw;
        }
        finally
        {
            DeleteProcThreadAttributeList(si.lpAttributeList);
            Marshal.FreeHGlobal(si.lpAttributeList);
            if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
        }
    }

    /// <summary>Current environment with a fresh PATH (so newly installed tools resolve) plus overrides.</summary>
    private static string BuildEnvironment(IDictionary<string, string>? overrides)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            vars[(string)e.Key] = (string?)e.Value ?? "";
        vars["PATH"] = SystemProbe.FreshPath;
        vars["TERM_PROGRAM"] = "Maison";
        vars["COLORTERM"] = "truecolor";
        if (overrides is not null) foreach (var (k, v) in overrides) vars[k] = v;
        var sb = new StringBuilder();
        foreach (var (k, v) in vars) sb.Append(k).Append('=').Append(v).Append('\0');
        sb.Append('\0');
        return sb.ToString();
    }

    public void Write(string text)
    {
        if (_disposed) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        try { _input.Write(bytes); _input.Flush(); } catch (IOException) { /* process gone */ }
    }

    public void Resize(int cols, int rows)
    {
        if (_disposed || cols <= 0 || rows <= 0) return;
        ResizePseudoConsole(_hpc, new COORD { X = (short)cols, Y = (short)rows });
    }

    private void ReadLoop()
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[16384];
        var chars = new char[16384 + 4];
        try
        {
            using var output = new FileStream(_outputRead, FileAccess.Read, 1);
            int n;
            while ((n = output.Read(bytes, 0, bytes.Length)) > 0)
            {
                int c = decoder.GetChars(bytes, 0, n, chars, 0, false);
                if (c > 0) Output?.Invoke(new string(chars, 0, c));
            }
        }
        catch (IOException) { /* pipe closed */ }
        catch (ObjectDisposedException) { }
    }

    private void WaitLoop()
    {
        WaitForSingleObject(_process, INFINITE);
        GetExitCodeProcess(_process, out var code);
        // Closing the pseudo console flushes and ends the output pipe, letting the reader finish.
        Thread.Sleep(150);
        Exited?.Invoke((int)code);
    }

    public void Kill()
    {
        if (!_disposed) TerminateProcess(_process, 1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { TerminateProcess(_process, 0); } catch { }
        if (_hpc != IntPtr.Zero) { ClosePseudoConsole(_hpc); _hpc = IntPtr.Zero; }
        _input.Dispose();
        CloseHandle(_thread);
        CloseHandle(_process);
    }

    // ------------------------------------------------------------------ interop

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000, CREATE_UNICODE_ENVIRONMENT = 0x00000400, INFINITE = 0xFFFFFFFF;
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attrs, int size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr hpc);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr hpc, COORD size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr hpc);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr ret);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
