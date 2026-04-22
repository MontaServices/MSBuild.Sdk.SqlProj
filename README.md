This is a fork by Monta. 

To push a new version to our private NuGet feed:
- Merge your changes to the `feature/assembly-ref-after-monta-release` branch
- Checkout the branch
- Create a new NuGet package version: in the `MSBuild.Sdk.SqlProj` directory: `dotnet pack`
- Allow classic personal access tokens in the organisation settings
- Create a personal access token in your settings with `write:packages` scope and authorise with SSO
- Add local package source: `dotnet nuget add source --name "github-MontaServices" https://nuget.pkg.github.com/MontaServices/index.json --username USERNAME --password PAT --store-password-in-clear-text` (replace USERNAME and PAT)
- Push package to source: `dotnet nuget push .\bin\Release\NUGETPACKAGEFILE --source "github-MontaServices" --api-key PAT` (replace NUGETPACKAGEFILE and PAT)
- Remove package source: `dotnet nuget remove source "github-MontaServices"`
- Delete personal access token 
- Disallow classic personal access tokens in the organisation settings

# MSBuild.Sdk.SqlProj

![Build Status](https://github.com/jmezach/MSBuild.Sdk.SqlProj/workflows/CI/badge.svg)
![Latest Stable Release](https://img.shields.io/nuget/v/MSBuild.Sdk.SqlProj)
![Latest Prerelease](https://img.shields.io/nuget/vpre/MSBuild.Sdk.SqlProj)
![Downloads](https://img.shields.io/nuget/dt/MSBuild.Sdk.SqlProj)

## Introduction 

A MSBuild SDK that produces SQL Server Data-Tier Application packages (`.dacpac`) from SQL scripts using SDK-style .NET projects.

## Documentation

- [Documentation site](https://rr-wfm.github.io/MSBuild.Sdk.SqlProj/docs/getting-started.html)

## Code of conduct

- Code of conduct: [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)

## Quick Start

Install the project templates:

```bash
dotnet new install MSBuild.Sdk.SqlProj.Templates
```

Create a new SQL project:

```bash
dotnet new sqlproj
```

Build the project:

```bash
dotnet build
```

For installation details, project configuration, references, packaging, publishing, and advanced topics, use the documentation site links above.
