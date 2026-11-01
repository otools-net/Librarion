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
3. Open http://localhost:5090 in a browser.

The database is a SQLite file in `src/Librarion.Api/data/`. For large libraries, use PostgreSQL:
[configuration](configuration.md#database).
