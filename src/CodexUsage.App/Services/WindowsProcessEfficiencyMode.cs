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

    public ProcessEfficiencyModeResult TryEnable()
    {
        using var process = Process.GetCurrentProcess();
        var errors = new List<string>();

        var state = new ProcessPowerThrottlingState
        {
            Version = ProcessPowerThrottlingCurrentVersion,
            ControlMask = ProcessPowerThrottlingExecutionSpeed,
            StateMask = ProcessPowerThrottlingExecutionSpeed,
        };
        var ecoQosEnabled = SetProcessInformation(
            process.Handle,
            ProcessPowerThrottling,
            ref state,
            checked((uint)Marshal.SizeOf<ProcessPowerThrottlingState>()));
        if (!ecoQosEnabled)
        {
            errors.Add($"EcoQoS: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        var priorityEnabled = false;
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            priorityEnabled = process.PriorityClass == ProcessPriorityClass.BelowNormal;
            if (!priorityEnabled)
            {
                errors.Add("priority: Windows did not retain BELOW_NORMAL_PRIORITY_CLASS");
            }
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            errors.Add($"priority: {error.Message}");
        }

        return new(
            ecoQosEnabled,
            priorityEnabled,
            errors.Count == 0 ? "Efficiency Mode enabled" : string.Join("; ", errors));
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
