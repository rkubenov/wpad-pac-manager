// Sample PAC file for WPAD / PAC File Manager.
// All names and addresses here are fictional (example.com, RFC 1918 ranges).
function FindProxyForURL(url, host) {
    // Internal plain hostnames (no dot) go direct.
    if (isPlainHostName(host)) return "DIRECT";

    // Corporate domains go direct.
    if (dnsDomainIs(host, ".example.com")) return "DIRECT";

    // Ad / tracking hosts are sent to a dead proxy (blocked).
    if (shExpMatch(url, "http://ads.*/*")) return "PROXY 127.0.0.1:1";

    // Clients on subnet 10.10.0.0/24 use the regional proxy.
    if (isInNet(myIpAddress(), "10.10.0.0", "255.255.255.0"))
        return "PROXY proxy-a.example.com:3128; DIRECT";

    // Everyone else uses the main proxy, with a direct fallback.
    return "PROXY proxy-main.example.com:3128; DIRECT";
}
