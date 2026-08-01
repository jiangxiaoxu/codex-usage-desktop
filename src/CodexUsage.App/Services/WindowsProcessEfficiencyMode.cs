using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CodexUsage.Application;

namespace CodexUsage.App.Services;

public sealed class WindowsProcessEfficiencyMode : IProcessEfficiencyMode
{
    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint ProcessPowerThrottlingExecutionSpeed = 0x1;
    private const int ProcessPowerThrottling = 4;

    public ProcessEfficiencyModeResult TryApply(ProcessExecutionMode mode)
    {
        using var process = Process.GetCurrentProcess();
        var errors = new List<string>();

        var state = new ProcessPowerThrottlingState
        {
            Version = ProcessPowerThrottlingCurrentVersion,
            ControlMask = ProcessPowerThrottlingExecutionSpeed,
            StateMask = mode == ProcessExecutionMode.Efficiency
                ? ProcessPowerThrottlingExecutionSpeed
                : 0,
        };
        var powerThrottlingApplied = SetProcessInformation(
            process.Handle,
            ProcessPowerThrottling,
            ref state,
            checked((uint)Marshal.SizeOf<ProcessPowerThrottlingState>()));
        if (!powerThrottlingApplied)
        {
            errors.Add($"EcoQoS: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        var priorityClassApplied = false;
        var expectedPriority = mode == ProcessExecutionMode.Efficiency
            ? ProcessPriorityClass.BelowNormal
            : ProcessPriorityClass.Normal;
        try
        {
            process.PriorityClass = expectedPriority;
            priorityClassApplied = process.PriorityClass == expectedPriority;
            if (!priorityClassApplied)
            {
                errors.Add($"priority: Windows did not retain {expectedPriority}");
            }
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            errors.Add($"priority: {error.Message}");
        }

        return new(
            mode,
            powerThrottlingApplied,
            priorityClassApplied,
            errors.Count == 0
                ? mode == ProcessExecutionMode.Efficiency
                    ? "Efficiency Mode enabled while the dashboard is inactive"
                    : "Interactive scheduling restored while the dashboard is active"
                : $"{mode} transition partially failed: {string.Join("; ", errors)}");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        IntPtr process,
        int informationClass,
        ref ProcessPowerThrottlingState information,
        uint informationSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }
}
