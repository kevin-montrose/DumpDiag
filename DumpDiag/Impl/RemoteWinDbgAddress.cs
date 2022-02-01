using System;
using System.Diagnostics.CodeAnalysis;

namespace DumpDiag.Impl
{
    internal readonly struct RemoteWinDbgAddress : IEquatable<RemoteWinDbgAddress>
    {
        public string IPAddress { get; }
        public ushort Port { get; }

        private RemoteWinDbgAddress(string addr, ushort port)
        {
            IPAddress = addr;
            Port = port;
        }

        public bool Equals(RemoteWinDbgAddress other)
        => other.Port == Port && other.IPAddress == IPAddress;

        public override bool Equals(object? obj)
        => obj is RemoteWinDbgAddress other && Equals(other);

        public override int GetHashCode()
        => HashCode.Combine(IPAddress, Port);

        public override string ToString()
        => $"{IPAddress}:{Port}";

        internal static bool TryParse(string connectionString, out RemoteWinDbgAddress address, [NotNullWhen(returnValue: false)] out string? error)
        {
            var sepIx = connectionString.IndexOf(':');
            if (sepIx == -1)
            {
                address = default;
                error = $"No separator found in {connectionString}, expected <ip>:<port>";
                return false;
            }

            var ip = connectionString[0..sepIx];
            var portStr = connectionString[(sepIx + 1)..];

            if (!ushort.TryParse(portStr, out var port))
            {
                address = default;
                error = $"Could not parse port {portStr}";
                return false;
            }

            return TryCreate(ip, port, out address, out error);
        }

        internal static bool TryCreate(string ip, ushort port, out RemoteWinDbgAddress address, [NotNullWhen(returnValue: false)] out string? error)
        {
            if (!System.Net.IPAddress.TryParse(ip, out _))
            {
                address = default;
                error = $"Could not parse ip: {ip}";
                return false;
            }

            address = new RemoteWinDbgAddress(ip, port);
            error = null;
            return true;
        }
    }
}
