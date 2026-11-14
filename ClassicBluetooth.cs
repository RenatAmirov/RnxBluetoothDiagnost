using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RnxBluetoothDiagnost;

internal static class ClassicBluetooth
{
    private const string Library = "bthprops.cpl";
    private const int NoMoreItems = 259;

    internal static IReadOnlyList<Device> FindNearby()
    {
        var search = new SearchParameters
        {
            Size = (uint)Marshal.SizeOf<SearchParameters>(),
            ReturnAuthenticated = true,
            ReturnRemembered = true,
            ReturnUnknown = true,
            ReturnConnected = true,
            IssueInquiry = true,
            TimeoutMultiplier = 8
        };
        var info = NewInfo();
        var searchHandle = BluetoothFindFirstDevice(ref search, ref info);
        if (searchHandle == IntPtr.Zero)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == NoMoreItems)
                return [];

            throw new Win32Exception(error);
        }

        var devices = new List<Device>();
        try
        {
            do
            {
                devices.Add(new Device(
                    info.Address, info.Name, info.ClassOfDevice, info.Connected,
                    info.Remembered, info.Authenticated, info.LastSeen.AsUtc()));
                info = NewInfo();
            } while (BluetoothFindNextDevice(searchHandle, ref info));

            int error = Marshal.GetLastPInvokeError();
            if (error != NoMoreItems)
                throw new Win32Exception(error);
        }
        finally
        {
            BluetoothFindDeviceClose(searchHandle);
        }

        return devices;
    }

    internal static (bool Paired, string Message) Pair(IntPtr window, ulong address)
    {
        var info = NewInfo();
        info.Address = address;
        // No out-of-band data: Windows owns the authentication dialog.
        const int generalBonding = 4;
        int error = BluetoothAuthenticateDeviceEx(window, IntPtr.Zero, ref info, IntPtr.Zero, generalBonding);
        return error switch
        {
            0 => (true, "Сопряжение выполнено (Win32 0)."),
            NoMoreItems => (true, "Windows уже хранит сопряжение (Win32 259)."),
            1223 => (false, "Сопряжение отменено (Win32 1223)."),
            _ => throw new Win32Exception(error)
        };
    }

    internal static void Forget(ulong address)
    {
        int error = BluetoothRemoveDevice(ref address);
        const int notFound = 1168;
        if (error != 0 && error != notFound)
            throw new Win32Exception(error);
    }

    private static DeviceInfo NewInfo() => new()
    {
        Size = (uint)Marshal.SizeOf<DeviceInfo>(),
        Name = string.Empty
    };

    internal sealed record Device(
        ulong Address, string Name, uint ClassOfDevice, bool Connected,
        bool Remembered, bool Authenticated, DateTimeOffset? LastSeen)
    {
        internal bool IsAudioVideo => ((ClassOfDevice >> 8) & 0x1F) == 4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SearchParameters
    {
        internal uint Size;
        [MarshalAs(UnmanagedType.Bool)] internal bool ReturnAuthenticated;
        [MarshalAs(UnmanagedType.Bool)] internal bool ReturnRemembered;
        [MarshalAs(UnmanagedType.Bool)] internal bool ReturnUnknown;
        [MarshalAs(UnmanagedType.Bool)] internal bool ReturnConnected;
        [MarshalAs(UnmanagedType.Bool)] internal bool IssueInquiry;
        internal byte TimeoutMultiplier;
        internal IntPtr Radio;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceInfo
    {
        internal uint Size;
        internal ulong Address;
        internal uint ClassOfDevice;
        [MarshalAs(UnmanagedType.Bool)] internal bool Connected;
        [MarshalAs(UnmanagedType.Bool)] internal bool Remembered;
        [MarshalAs(UnmanagedType.Bool)] internal bool Authenticated;
        internal SystemTime LastSeen;
        internal SystemTime LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        internal string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        internal ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Millisecond;

        internal readonly DateTimeOffset? AsUtc()
        {
            if (Year == 0)
                return null;

            return new DateTimeOffset(Year, Month, Day, Hour, Minute, Second, Millisecond, TimeSpan.Zero);
        }
    }

    [DllImport(Library, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(ref SearchParameters search, ref DeviceInfo info);

    [DllImport(Library, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextDevice(IntPtr search, ref DeviceInfo info);

    [DllImport(Library, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindDeviceClose(IntPtr search);

    [DllImport(Library, ExactSpelling = true)]
    private static extern int BluetoothAuthenticateDeviceEx(
        IntPtr window, IntPtr radio, ref DeviceInfo info, IntPtr oobData, int requirement);

    [DllImport(Library, ExactSpelling = true)]
    private static extern int BluetoothRemoveDevice(ref ulong address);
}
