using System;
using System.Runtime.InteropServices;

namespace RingworldTechDemo.Diagnostics;

/// <summary>
/// Cheap current-process memory/CPU readers. System.Diagnostics.Process.WorkingSet64 / PrivateMemorySize64 /
/// Threads go through NtQuerySystemInformation and enumerate every process on the machine (~24 ms per call),
/// so periodic gameplay polling must not use them.
/// </summary>
public static class RingProcessMemory
{
	[StructLayout(LayoutKind.Sequential)]
	private struct ProcessMemoryCountersEx
	{
		public uint cb;
		public uint PageFaultCount;
		public nuint PeakWorkingSetSize;
		public nuint WorkingSetSize;
		public nuint QuotaPeakPagedPoolUsage;
		public nuint QuotaPagedPoolUsage;
		public nuint QuotaPeakNonPagedPoolUsage;
		public nuint QuotaNonPagedPoolUsage;
		public nuint PagefileUsage;
		public nuint PeakPagefileUsage;
		public nuint PrivateUsage;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct FileTime
	{
		public uint Low;
		public uint High;
		public readonly long Ticks => ((long)High << 32) | Low;
	}

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetCurrentProcess();

	[DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCountersEx counters, uint size);

	[DllImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetProcessTimes(IntPtr process, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);

	/// <summary>Resident working set of this process (no process enumeration).</summary>
	public static long WorkingSetBytes => Environment.WorkingSet;

	/// <summary>Private (committed) bytes of this process; 0 on failure or non-Windows.</summary>
	public static long PrivateBytes
	{
		get
		{
			if (!OperatingSystem.IsWindows()) return 0L;
			try
			{
				ProcessMemoryCountersEx counters = default;
				counters.cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx>();
				return GetProcessMemoryInfo(GetCurrentProcess(), out counters, counters.cb) ? (long)counters.PrivateUsage : 0L;
			}
			catch (Exception) { return 0L; }
		}
	}

	/// <summary>Kernel plus user CPU time of this process in milliseconds; 0 on failure or non-Windows.</summary>
	public static double ProcessCpuMilliseconds
	{
		get
		{
			if (!OperatingSystem.IsWindows()) return 0.0;
			try
			{
				return GetProcessTimes(GetCurrentProcess(), out _, out _, out FileTime kernel, out FileTime user)
					? (kernel.Ticks + user.Ticks) / 10000.0
					: 0.0;
			}
			catch (Exception) { return 0.0; }
		}
	}
}
