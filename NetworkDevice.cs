using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
// ReSharper disable All

namespace Silverbullet.NetworkDevice
{

    // A loopback adapter.
    public sealed class LoopbackAdapter
    {
        // The ID/GUID reported by the Windows API
        public string NetConfigInstanceId;
        // Name of the connection (e.g. "Local Area Connection 2" / "Ethernet 2", or null if not present)
        public string ConnectionName;
        // Additional description string from the Windows API
        public string Description;

        public override string ToString()
        {
            return string.Format(NetConfigInstanceId + " " + ConnectionName + " " + Description);
        }
    }

    public static class LoopbackDeviceHelper
    {
        const string Hwid = "*MSLOOP";

        delegate bool DeviceConsumer(IntPtr set, ref NativeHelper.SpDeviceInfoData device, string instanceId);
        
        /**
         * Attempts to create a new loopback adapter and waits until Windows reports it as a network connection.
         * If something goes wrong a Win32Exception is thrown (SetupAPI) + the partially created device gets cleared.
         */
        public static LoopbackAdapter Create(int waitSeconds)
        {
            EnsureNotWow64();
            string inf = Path.Combine(Environment.GetEnvironmentVariable("windir"), @"inf\netloop.inf");
            if (!File.Exists(inf))
                throw new FileNotFoundException("Windows loopback driver not found", inf);

            Guid cls;
            StringBuilder className = new StringBuilder(256);
            if (!NativeHelper.SetupDiGetINFClassW(inf, out cls, className, className.Capacity, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetINFClass");

            IntPtr set = NativeHelper.SetupDiCreateDeviceInfoList(ref cls, IntPtr.Zero);
            if (set == NativeHelper.InvalidHandleValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiCreateDeviceInfoList");

            NativeHelper.SpDeviceInfoData device = NativeHelper.NewDevInfoData();
            bool registered = false;
            try
            {
                if (!NativeHelper.SetupDiCreateDeviceInfoW(
                        set, className.ToString(), ref cls, null, IntPtr.Zero,
                        NativeHelper.DICD_GENERATE_ID, ref device)
                    ) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiCreateDeviceInfo");

                byte[] ids = Encoding.Unicode.GetBytes(Hwid + "\0\0"); // REG_MULTI_SZ
                bool result = NativeHelper.SetupDiSetDeviceRegistryPropertyW(
                    set, ref device,
                    NativeHelper.SPDRP_HARDWAREID, ids, ids.Length
                );
                
                if (!result) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiSetDeviceRegistryProperty");

                if (!NativeHelper.SetupDiCallClassInstaller(NativeHelper.DIF_REGISTERDEVICE, set, ref device))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "DIF_REGISTERDEVICE");
                registered = true;

                bool reboot; // TODO: Determine when this is "true" and pass it to the caller
                result = NativeHelper.UpdateDriverForPlugAndPlayDevicesW(
                    IntPtr.Zero, Hwid, inf, NativeHelper.INSTALLFLAG_FORCE,
                    out reboot
                );
                if (!result)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateDriverForPlugAndPlayDevices");

                string id = ReadNetConfigInstanceId(set, ref device);
                if (id == null)
                    throw new InvalidOperationException("Adapter installed, but it has no NetCfgInstanceId.");

                LoopbackAdapter adapter = new LoopbackAdapter();
                adapter.NetConfigInstanceId = id;
                QueryConnectionInfo(adapter, waitSeconds);
                return adapter;
            } catch {
                // Clear the device if we've managed to register it
                if (registered) NativeHelper.SetupDiCallClassInstaller(NativeHelper.DIF_REMOVE, set, ref device);
                throw;
            } finally {
                NativeHelper.SetupDiDestroyDeviceInfoList(set);
            }
        }
        
        /**
         * Queries all loopback adapters on the system
         */
        public static List<LoopbackAdapter> FindAll()
        {
            List<LoopbackAdapter> result = new List<LoopbackAdapter>();
            IterateLoopbackDevices(delegate(IntPtr set, ref NativeHelper.SpDeviceInfoData dev, string id)
            {
                LoopbackAdapter loopbackAdapter = new LoopbackAdapter();
                loopbackAdapter.NetConfigInstanceId = id;
                QueryConnectionInfo(loopbackAdapter, 0);
                result.Add(loopbackAdapter);
                return false;
            });
            return result;
        }
        
        /**
         * Attempts to find a specific Loopback Adapter by the connection name.
         */
        public static LoopbackAdapter FindByConnectionName(string connectionName)
        {
            foreach (var a in FindAll())
                if (a.ConnectionName != null && 
                    string.Equals(a.ConnectionName, connectionName, StringComparison.OrdinalIgnoreCase))
                    return a;
            return null;
        }
        
        /**
         * Removes the loopback adapter with this net config instance ID.
         * Returns false if no such adapter exists.
         */
        public static bool Remove(string instanceId)
        {
            EnsureNotWow64();
            var found = false;
            IterateLoopbackDevices(delegate(IntPtr set, ref NativeHelper.SpDeviceInfoData device, string id)
            {
                if (!string.Equals(id, instanceId, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!NativeHelper.SetupDiCallClassInstaller(NativeHelper.DIF_REMOVE, set, ref device))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "DIF_REMOVE");
                found = true;
                return true;
            });
            return found;
        }
        
        /**
         * Calls the "consume" function for every present device with hardware ID
         */
        static void IterateLoopbackDevices(DeviceConsumer consumer)
        {
            var guid = NativeHelper.GuidDevClassNet;
            var set = NativeHelper.SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, NativeHelper.DIGCF_PRESENT);
            
            if (set == NativeHelper.InvalidHandleValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs");

            try
            {
                var device = NativeHelper.NewDevInfoData();
                for (var index = 0; NativeHelper.SetupDiEnumDeviceInfo(set, index, ref device); index++)
                {
                    if (!HasHardwareId(set, ref device, Hwid)) continue;
                    string id = ReadNetConfigInstanceId(set, ref device);
                    if (id != null && consumer(set, ref device, id)) return;
                }
            } finally {
                NativeHelper.SetupDiDestroyDeviceInfoList(set);
            }
        }

        /**
         * Helper function that checks if a hardware ID is already in use.
         */
        static bool HasHardwareId(IntPtr set, ref NativeHelper.SpDeviceInfoData device, string targetId)
        {
            var bytes = new byte[1024];
            if (!NativeHelper.SetupDiGetDeviceRegistryPropertyW(
                    set,
                    ref device,
                    NativeHelper.SPDRP_HARDWAREID,
                    out _,
                    bytes,
                    bytes.Length,
                    out var required)
                )
                return false; // Device doesn't have a HWID

            var text = Encoding.Unicode.GetString(bytes, 0, Math.Min(required, bytes.Length));
            // Note: LINQ expressions cannot be used because it would break compatibility with .NET 2.0 (Win XP)
            foreach (var part in text.Split('\0'))
                if (string.Equals(part, targetId, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /**
         * Reads the "NetCfgInstanceId" value from the device's driver key.
         * (Located in ...\Class\{4D36E972-...}\00xx (null is returned if absent)
         */
        static string ReadNetConfigInstanceId(IntPtr set, ref NativeHelper.SpDeviceInfoData device)
        {
            var regKeyPtr = NativeHelper.SetupDiOpenDevRegKey(
                set,
                ref device,
                NativeHelper.DICS_FLAG_GLOBAL,
                0,
                NativeHelper.DIREG_DRV,
                NativeHelper.KEY_READ
            );
            if (regKeyPtr == NativeHelper.InvalidHandleValue) return null;

            try
            {
                var size = 512;
                var bytes = new byte[size];

                var readResult = NativeHelper.RegQueryValueExW(
                    regKeyPtr,
                    "NetCfgInstanceId",
                    IntPtr.Zero,
                    out var type,
                    bytes,
                    ref size
                );
                if (readResult != 0 || type != NativeHelper.REG_SZ) return null;
                const char padding = '\0';
                return Encoding.Unicode.GetString(bytes, 0, size).TrimEnd(padding);
            } finally {
                NativeHelper.RegCloseKey(regKeyPtr);
            }
        }

        /**
         * Fills a "LoopbackAdapter" class as long as it's found in the network interface list within the given time
         * frame.
         */
        public static void QueryConnectionInfo(LoopbackAdapter adapter, int timeout)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeout);
            while(true)
            {
                foreach(var iface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if(!string.Equals(iface.Id, adapter.NetConfigInstanceId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Found something!
                    adapter.ConnectionName = iface.Name;
                    adapter.Description = iface.Description;
                    return;
                }

                // Exit if the deadline was reached. It's up to the caller whether a missing connection is problematic
                if (DateTime.UtcNow >= deadline) return;

                // Add some artificial waiting time between iterations to prevent wasting CPU cycles
                Thread.Sleep(100);
            }
        }

        /**
         * Helper function that throws an exception if Silverbullet is running in an unsupported environment
         */
        static void EnsureNotWow64()
        {
            if (IntPtr.Size != 4) return; // 64-bit process

            var isWow64 = false;
            try
            {
                if (!NativeHelper.IsWow64Process(NativeHelper.GetCurrentProcess(), out isWow64))
                    return;       
            } catch(EntryPointNotFoundException)
            {
                return; // XP before SP2 lacks IsWow64Process
            }
            // Should never happen as long as the program is built as AnyCPU with "Prefer 32 bit" turned off
            // This would hinder us from installing a network driver
            if (isWow64)
                throw new InvalidOperationException("Silverbullet was incorrectly built. (Running as 64-bit process)");
        }

    }

    static class NativeHelper
    {
        public static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        // Device category = Network Adaper
        // (https://learn.microsoft.com/en-us/windows-hardware/drivers/install/system-defined-device-setup-classes-available-to-vendors)
        public static readonly Guid GuidDevClassNet = new Guid("4d36e972-e325-11ce-bfc1-08002be10318");

        public const int DICD_GENERATE_ID = 0x1;
        public const int DIGCF_PRESENT = 0x2;
        public const int SPDRP_HARDWAREID = 0x1;
        public const int DIF_REMOVE = 0x5;
        public const int DIF_REGISTERDEVICE = 0x19;
        public const int INSTALLFLAG_FORCE = 0x1;
        public const int DICS_FLAG_GLOBAL = 0x1;
        public const int DIREG_DRV = 0x2;
        public const int KEY_READ = 0x20019;
        public const int REG_SZ = 1;


        [StructLayout(LayoutKind.Sequential)]
        public struct SpDeviceInfoData
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        public static SpDeviceInfoData NewDevInfoData()
        {
            SpDeviceInfoData devInfo = new SpDeviceInfoData();
            devInfo.cbSize = Marshal.SizeOf(typeof(SpDeviceInfoData)); // 28 on 32 bit, 32 on 64 bit
            return devInfo;
        }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetINFClassW(string infName, out Guid classGuid, StringBuilder className,
                                                      int classNameSize, IntPtr requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string deviceName, ref Guid classGuid,
                                                           string description, IntPtr hwndParent, int flags,
                                                           ref SpDeviceInfoData data);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SpDeviceInfoData data);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref SpDeviceInfoData data, int property,
                                                                    byte[] buffer, int size);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SpDeviceInfoData data, int property,
                                                                    out int regType, byte[] buffer, int size,
                                                                    out int requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiCallClassInstaller(int function, IntPtr set, ref SpDeviceInfoData data);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref SpDeviceInfoData data, int scope, int hwProfile,
                                                         int keyType, int samDesired);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwndParent, string hardwareId, string fullInfPath,
                                                                     int installFlags,
                                                                     [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegQueryValueExW(IntPtr key, string valueName, IntPtr reserved, out int type,
                                                  byte[] data, ref int dataSize);

        [DllImport("advapi32.dll")]
        public static extern int RegCloseKey(IntPtr key);

        // Native function that exposes our current PID
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        // Native function for checking if we're running as a "Windows-On-Windows 64" process
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWow64Process(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool wow64);
    }

}