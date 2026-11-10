# Hotspot Unblocker

Windows console proxy for the hotspot + proxy setup described in https://www.routetoretire.com/cruise-internet-sharing/ . The PC opens outgoing TCP connections on behalf of clients. HTTPS stays encrypted end to end; no certificate installation is needed.

## Start

1. Connect the PC to the upstream Wi-Fi and complete any captive-portal login.
2. Enable **Settings > Network & internet > Mobile hotspot**, sharing the upstream connection over Wi-Fi. Your adapter must support simultaneous client and hotspot operation.
3. Start `bin\Release\net7.0\hotspot-unblocker.exe`, or use `dotnet run -c Release -- --hotspot` to also open hotspot settings. The app does not automatically enable the hotspot.
4. Connect the iPhone to **the PC hotspot**, not directly to the upstream Wi-Fi.
5. On iPhone: **Settings > Wi-Fi > (i) beside the PC hotspot > Configure Proxy > Manual**. Enter the server, port, username and password printed in the console; turn Authentication on and save.
6. Test Safari and your actual 2FA app. CONNECT messages in the console show HTTPS connections reaching the proxy.

The default listener is `192.168.137.1:8080`. If Windows assigns a different hotspot IP, use `--bind <PC-hotspot-IP> --port 8080`. Available interface addresses are printed on startup. Keep the PC awake. Ctrl+C closes proxy sessions; disable Mobile hotspot separately. Set the phone proxy back to Off when finished. Passwords change on each restart.

If Windows Firewall blocks access, allow inbound TCP on the hotspot interface. An optional scoped rule, run in administrator PowerShell (adjust IP/subnet as needed):

```powershell
New-NetFirewallRule -DisplayName 'Hotspot Unblocker' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8080 -LocalAddress 192.168.137.1 -RemoteAddress 192.168.137.0/24 -Profile Any
```

Remove with `Remove-NetFirewallRule -DisplayName 'Hotspot Unblocker'`. The application does not modify firewall or network settings. Avoid binding to the upstream interface or all interfaces. Use a strong hotspot Wi-Fi password; Basic proxy credentials are not encrypted on the local link.

## Scope

With other devices connected only to the PC hotspot, the upstream access point has the PC as its directly associated client. Proxy connections originate in the PC networking stack. This is not a guarantee of undetectability: client TLS fingerprints, destinations and behavior can still reveal differences. Ordinary NAT also hides downstream MAC addresses; the article's explanation of MAC forwarding is misleading for NAT hotspots.

Only apps honoring Wi-Fi HTTP proxy settings use this proxy. No UDP/QUIC support or system-wide tunnel. Windows sharing can still forward non-proxy traffic through NAT; this application does not block it. SMS needs cellular service, and push authentication may not use the proxy. Test your actual authentication flow before relying on it.

HTTP requests with Content-Length and HTTPS CONNECT are supported. Plain HTTP chunked request bodies return 501; chunked responses pass through. Each plain HTTP connection handles one request. Limits: 128 simultaneous clients, 20-second initial header/CONNECT timeout, 32 KiB headers, 30-minute sessions. Logs show destination hosts, not URL paths. Local integration tests do not establish compatibility with a particular Wi-Fi provider or iPhone app.

## Build and test

```powershell
dotnet build -c Release
node tests/integration.cjs
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```

The workspace has .NET 7, so that is the current target. Its runtime is required for the executable; for maintained use, install a supported SDK/runtime and update TargetFramework. No external NuGet dependencies.

Official setup references: https://support.microsoft.com/en-gb/windows/experience/connectivity-networking/use-your-windows-device-as-a-mobile-hotspot and https://support.apple.com/en-ie/guide/iphone/iphw5gjwl8k2/ios .

## Automatic proxy setup (one URL)

Start `publish\hotspot-unblocker.exe --auto` after enabling the Windows hotspot. On iPhone, join the PC hotspot and select **Settings > Wi-Fi > (i) > Configure Proxy > Automatic**. Enter:

```
http://192.168.137.1:8080/
```

Save. The console prints the correct URL if you change `--bind` or `--port`. `/proxy.pac` and `/wpad.dat` also work. The root URL is shortest and needs no DNS setup. A friendly hostname requires reliable local DNS/mDNS resolution and is not configured by this app.

`--auto` disables proxy username/password authentication because a PAC file cannot embed credentials and automatic-mode authentication varies by client. Access is restricted to the bound local IPv4 interface's subnet; binding to all interfaces or a public address is rejected. Use only your PC hotspot interface and a strong hotspot password: anyone on that subnet can use the proxy. Without `--auto`, the original authenticated mode remains the default. The manual credentials printed at startup do not apply in auto mode.

PAC requests need no authentication and contain no credentials. The PAC returns only the proxy address, with no DIRECT fallback. This does not prevent apps from bypassing system proxy settings. PAC generation and use are tested locally, but iPhone behavior still needs a device check.

## Diagnose partial failures

Diagnostic logs are written beside the executable as `proxy.log` (rotated at 5 MiB). Override with `--log <path>`. They include PAC fetches, destination hostnames, CONNECT failures, duration and bytes in each direction, but no traffic contents or credentials. Treat destination history as private.

If the PC stays online but an iPhone site fails, compare Safari with the native app and check the console around the failure. A missing CONNECT means that request did not reach this proxy. A CONNECT FAILED entry indicates DNS/TCP setup trouble. An established tunnel with a reset or very little received data needs further investigation; it does not itself prove network detection. Requests inside HTTPS, including URL paths and HTTP response codes, remain encrypted.

For a direct hotspot-link test, temporarily set Configure Proxy to Off and open `http://192.168.137.1:8080/health` in Safari, then restore Automatic. The health endpoint checks reachability of the PC only, not internet access. Tunnel relay now preserves half-closes in both directions, logs transferred bytes, and has no arbitrary 30-minute session cutoff. Local tests include delayed 2 MiB binary transfers; they cannot reproduce a provider's filtering.
