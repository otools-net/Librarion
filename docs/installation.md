# Installation

Librarion is in early development: for now it runs from its source code. Packages (Docker image, portable download,
Windows service, systemd) come before the first release.

## From source

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and [Node.js](https://nodejs.org/) 22
   or later.
2. Get the code and start the server:

   ```sh
   git clone <repository address> librarion
   cd librarion
   dotnet run --project src/Librarion.Api
   ```

   The first start builds the web app, which takes a minute or two.
3. Open http://localhost:5090 in a browser and set up the first administrator with the code the server printed:
   [first start](first-start.md).

The database is a SQLite file in `src/Librarion.Api/data/`. For large libraries, use PostgreSQL:
[configuration](configuration.md#database).

## From other devices on your home network

At first the server answers only the computer it runs on (`http://localhost:5090`). To open it from a phone, a
tablet or a reader app on the same Wi-Fi:

1. **Let it listen on the network.** Create `src/Librarion.Api/appsettings.Local.json` (your own settings, never
   overwritten) with:

   ```json
   { "Urls": "http://0.0.0.0:5090" }
   ```

   or start it with `dotnet run --project src/Librarion.Api -- --urls http://0.0.0.0:5090`.
2. **Open the port in the firewall.** On Windows, the network must count as private, then the port is let in, in a
   PowerShell run as administrator:

   ```powershell
   Get-NetConnectionProfile                                                    # the Wi-Fi's name and category
   Set-NetConnectionProfile -InterfaceAlias "Wi-Fi" -NetworkCategory Private   # if it says Public
   New-NetFirewallRule -DisplayName "Librarion 5090" -Direction Inbound -Protocol TCP -LocalPort 5090 -Action Allow -Profile Private
   ```

   On Linux with `ufw`: `sudo ufw allow from 192.168.0.0/16 to any port 5090 proto tcp` (your network's range).
3. **Find the computer's address:** `ipconfig` on Windows (the Wi-Fi adapter's "IPv4 Address"), `ip addr` on Linux;
   for example `192.168.1.20`.
4. Open `http://192.168.1.20:5090` on the computer itself first, then on the other device. Reader apps use
   `http://192.168.1.20:5090/opds` ([reader apps](opds.md)).

- **It times out:** usually the firewall (step 2), or the server still listening on `localhost` only (step 1).
  Some routers keep Wi-Fi devices apart ("client isolation", "AP isolation", often on guest networks): put both on
  the main network.
- Signing in works over plain `http` at home. Anything reachable from the internet should go through `https`, behind
  a reverse proxy ([configuration](configuration.md#security)).
- The computer's address may change when it restarts; your router can keep it the same (a "DHCP reservation").
