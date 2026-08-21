# Build and Release Guidelines

- Always build/publish the application directly to the `./publish` directory using:
  ```powershell
  dotnet publish AIUsageMonitor\AIUsageMonitor.csproj -c Release
  ```
- Do not rely solely on `dotnet build` without publishing to `./publish`.
