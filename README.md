This is a fork of [rr-wfm/MSBuild.Sdk.SqlProj](https://github.com/rr-wfm/MSBuild.Sdk.SqlProj) to support including referenced C# projects as 
CLR libraries in the Dacpac. It includes the DLL's by hacking into the Dacpac ZIP. This is the only way to support building on Linux, 
because the assemblies are validated by DacFX which only works on Windows. 
The referenced assemblies need to be .NET Framework as .NET Core is not supported by SQL CLR. 