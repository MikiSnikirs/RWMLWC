# Start a game instance without taking keyboard focus from what the person is doing (a game, a video...).
# The process starts minimized and inactive (SW_SHOWMINNOACTIVE); once its window exists it is shown in its
# normal place (windowPositionX/Y from tools/TestWindow.ps1) without activation (SW_SHOWNOACTIVATE).
# Dot-source, then: $p = Start-GameQuiet $exe $arguments $workDir ; ... Show-GameQuiet $p
# Messages are ASCII on purpose: PowerShell 5.1 misreads UTF-8 scripts without BOM.

if (-not ('RwmlwcQuiet' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RwmlwcQuiet
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, int flags,
        IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);

    const int STARTF_USESHOWWINDOW = 1;
    const short SW_SHOWMINNOACTIVE = 7;
    public const int SW_SHOWNOACTIVATE = 4;

    public static int Start(string exe, string args, string dir)
    {
        STARTUPINFO si = new STARTUPINFO();
        si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
        si.dwFlags = STARTF_USESHOWWINDOW;
        si.wShowWindow = SW_SHOWMINNOACTIVE;
        PROCESS_INFORMATION pi;
        if (!CreateProcess(exe, "\"" + exe + "\" " + args, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, dir, ref si, out pi))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return pi.dwProcessId;
    }
}
'@
}

function Start-GameQuiet([string]$Exe, [string]$Arguments, [string]$WorkDir) {
    $id = [RwmlwcQuiet]::Start($Exe, $Arguments, $WorkDir)
    return Get-Process -Id $id
}

# Show the window (restored, not activated) once it exists. Waits up to $TimeoutSec; returns $true when shown.
function Show-GameQuiet($Process, [int]$TimeoutSec = 90) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline -and -not $Process.HasExited) {
        $Process.Refresh()
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            Start-Sleep -Seconds 2   # let the game finish creating the window first
            [void][RwmlwcQuiet]::ShowWindow($Process.MainWindowHandle, [RwmlwcQuiet]::SW_SHOWNOACTIVATE)
            return $true
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}
