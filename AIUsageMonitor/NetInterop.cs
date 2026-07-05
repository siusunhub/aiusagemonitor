using System.Runtime.InteropServices;

namespace AIUsageMonitor;

/// <summary>Lists TCP listening ports per process (GetExtendedTcpTable).</summary>
public static class NetInterop
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen,
        bool sort, int ipVersion, int tableClass, uint reserved);

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    public static List<int> GetListeningPorts(int pid)
    {
        var ports = new List<int>();
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (size <= 0) return ports;

        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0)
                return ports;

            int count = Marshal.ReadInt32(buf);
            IntPtr row = buf + 4;
            const int rowSize = 24; // MIB_TCPROW_OWNER_PID: 6 DWORDs
            for (int i = 0; i < count; i++, row += rowSize)
            {
                int owningPid = Marshal.ReadInt32(row + 20);
                if (owningPid != pid) continue;
                int rawPort = Marshal.ReadInt32(row + 8); // network byte order in low word
                int port = ((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF);
                if (!ports.Contains(port)) ports.Add(port);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }

        ports.Sort();
        return ports;
    }
}
