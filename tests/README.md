# Tests

`Gubal.Core.Tests` uses xUnit v3 to test pack selection, coverage, categories and version messages against `Gubal.Core`.
Use the same Dalamud dependency directory as the application build.

```powershell
dotnet test tests/Gubal.Core.Tests/Gubal.Core.Tests.csproj -c Release
```

Native hooks and game UI changes require an in-game check. These tests do not start FFXIV.
