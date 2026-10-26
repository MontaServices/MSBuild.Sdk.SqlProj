using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.IO.Packaging;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using System.Security.Cryptography;

namespace MSBuild.Sdk.SqlProj.DacpacTool
{
    public sealed class PackageBuilder : IDisposable
    {
        private readonly IConsole _console;
        private bool? _modelValid;

        private List<int> _suppressedWarnings = new ();
        private Dictionary<string,List<int>> _suppressedFileWarnings = new Dictionary<string, List<int>>(StringComparer.InvariantCultureIgnoreCase);

        private List<string> _dllReferences = new List<string>();

        public PackageBuilder(IConsole console)
        {
            _console = console ?? throw new ArgumentNullException(nameof(console));
        }

        public void UsingVersion(SqlServerVersion version)
        {
            Model = new TSqlModel(version, Options);
            _console.WriteLine($"Using SQL Server version {version}");
        }

        public void AddReference(string referenceFile, string externalParts = null, bool suppressErrorsForMissingDependencies = false)
        {
            // Ensure that the model has been created
            EnsureModelCreated();

            ValidateReference(referenceFile);

            _console.WriteLine($"Adding reference to {referenceFile} with external parts {externalParts} and SuppressMissingDependenciesErrors {suppressErrorsForMissingDependencies}");
            Model.AddReference(referenceFile, externalParts, suppressErrorsForMissingDependencies);
        }

        private static void ValidateReference(string referenceFile)
        {
            // Make sure the file exists
            if (!File.Exists(referenceFile))
            {
                throw new ArgumentException($"Unable to find reference file {referenceFile}", nameof(referenceFile));
            }

            // Make sure the file is a .dacpac file
            string fileType = Path.GetExtension(referenceFile);
            if (!fileType.Equals(".dacpac", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Invalid filetype {fileType}, was expecting .dacpac", nameof(referenceFile));
            }
        }

        public void AddAssemblyReference(string referenceFile)
        {
            // Ensure that the model has been created
            EnsureModelCreated();

            ValidateAssemblyReference(referenceFile);

            _console.WriteLine($"Adding assembly reference to {referenceFile}");
            _dllReferences.Add(referenceFile);
        }

        private static void ValidateAssemblyReference(string referenceFile)
        {
            // Make sure the file exists
            if (!File.Exists(referenceFile))
            {
                throw new ArgumentException($"Unable to find reference file {referenceFile}", nameof(referenceFile));
            }

            // Make sure the file is a .dll file
            string fileType = Path.GetExtension(referenceFile);
            if (!fileType.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Invalid filetype {fileType}, was expecting .dll", nameof(referenceFile));
            }
        }

        public void AddSqlCmdVariables(string[] variables)
        {
            // Ensure that the model has been created
            EnsureModelCreated();

            Model.AddSqlCmdVariables(variables);
        }

        public bool AddInputFile(FileInfo inputFile)
        {
            ArgumentNullException.ThrowIfNull(inputFile);

            // Ensure that the model has been created
            EnsureModelCreated();

            // Make sure the file exists
            if (!inputFile.Exists)
            {
                throw new ArgumentException($"Unable to find input file {inputFile}", nameof(inputFile));
            }

            _console.WriteLine($"Adding {inputFile.FullName} to the model");

            try
            {
                TSqlObjectOptions sqlObjectOptions = new TSqlObjectOptions();
                Model.AddOrUpdateObjects(File.ReadAllText(inputFile.FullName), inputFile.FullName, new TSqlObjectOptions());
                return true;
            }
            catch (DacModelException dex)
            {
                _console.WriteLine(dex.Format(inputFile.FullName));
                return false;
            }
        }

        public void AddPreDeploymentScript(FileInfo script, FileInfo outputFile, bool clrAssemblyTrustInPreDeploy)
        {
            ArgumentNullException.ThrowIfNull(outputFile);

            var trustAssembliesScript = clrAssemblyTrustInPreDeploy && _dllReferences.Count > 0 ? BuildTrustAssembliesScript(_dllReferences) : null;

            AddScript(script, outputFile, "/predeploy.sql", trustAssembliesScript);
        }

        public void AddPostDeploymentScript(FileInfo script, FileInfo outputFile)
        {
            ArgumentNullException.ThrowIfNull(outputFile);

            AddScript(script, outputFile, "/postdeploy.sql");
        }
        
        private static string BuildTrustAssembliesScript(List<string> assemblies)
        {
            var sb = new StringBuilder();
            sb.AppendLine("-- BEGIN MSBuild.Sdk.SqlProj: trust referenced SQL CLR assemblies");
            sb.AppendLine("IF OBJECT_ID('tempdb..#is_assembly_trusted') IS NOT NULL DROP PROCEDURE #is_assembly_trusted;");
            sb.AppendLine("GO");
            sb.AppendLine("CREATE PROCEDURE #is_assembly_trusted @hash varbinary(64)");
            sb.AppendLine("AS");
            sb.AppendLine("BEGIN");
            sb.AppendLine("    RETURN IIF(EXISTS (SELECT * FROM sys.trusted_assemblies WHERE [hash] = @hash), 1, 0);");
            sb.AppendLine("END");
            sb.AppendLine("GO");

            foreach (var assemblyPath in assemblies)
            {
                var assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
                // Compute the SHA-512 hash at build time so the predeploy script only carries the
                // 64-byte hash literal instead of the full assembly bytes (those are already
                // embedded once in the corresponding CREATE ASSEMBLY ... FROM 0x... statement).
                var hashHex = Convert.ToHexString(ComputeSha512(assemblyPath));
                var safeName = assemblyName.Replace("'", "''", StringComparison.OrdinalIgnoreCase);

                sb.AppendLine($"-- Trust assembly: {assemblyName}");
                sb.AppendLine($"DECLARE @name sysname = N'{safeName}';");
                sb.AppendLine("DECLARE @description nvarchar(4000) = @name;");
                sb.AppendLine($"DECLARE @hash varbinary(64) = 0x{hashHex};");
                sb.AppendLine("DECLARE @is_assembly_trusted bit;");
                sb.AppendLine("EXEC @is_assembly_trusted = #is_assembly_trusted @hash;");
                sb.AppendLine("IF @is_assembly_trusted = 1");
                sb.AppendLine("BEGIN");
                sb.AppendLine("    PRINT 'Assembly ' + @name + ' already trusted';");
                sb.AppendLine("END");
                sb.AppendLine("ELSE");
                sb.AppendLine("BEGIN");
                sb.AppendLine("    PRINT 'Assembly ' + @name + ' not trusted yet, trusting...';");
                sb.AppendLine("    EXEC sys.sp_add_trusted_assembly @hash = @hash, @description = @description;");
                sb.AppendLine("    EXEC @is_assembly_trusted = #is_assembly_trusted @hash;");
                sb.AppendLine("    IF @is_assembly_trusted = 0");
                sb.AppendLine("    BEGIN");
                sb.AppendLine("        DECLARE @msg nvarchar(max) = CONCAT('Trusting the assembly ', @name, ' failed. This may be caused by a lack of permissions. Execute the following command manually on the server to trust the assembly, then re-run the pipeline. declare @description nvarchar(4000) = ''', @description, '''; exec sys.sp_add_trusted_assembly @hash = ', CONVERT(varchar(max), @hash, 1), ', @description = @description');");
                sb.AppendLine("        ;THROW 50000, @msg, 1;");
                sb.AppendLine("    END");
                sb.AppendLine("    PRINT 'Assembly ' + @name + ' trusted';");
                sb.AppendLine("END");
                sb.AppendLine("GO");
            }

            sb.AppendLine("-- END MSBuild.Sdk.SqlProj: trust referenced SQL CLR assemblies");
            return sb.ToString();
        }

        private static byte[] ComputeSha512(string filePath)
        {
            using var sha = System.Security.Cryptography.SHA512.Create();
            using var stream = File.OpenRead(filePath);
            return sha.ComputeHash(stream);
        }

        public bool ValidateModel()
        {
            // Ensure that the model has been created
            EnsureModelCreated();

            // Validate the model and write out validation messages
            var modelErrors = Model.GetModelValidationErrors(Enumerable.Empty<string>());
            int validationErrors = 0;
            foreach (var modelError in modelErrors)
            {
                var ignoreMsg = "unresolved reference to Assembly";
                if (modelError.ErrorCode == 71501
                    && _dllReferences.Count > 0
                    && modelError.GetOutputMessage(modelError.Severity).Contains(ignoreMsg, StringComparison.OrdinalIgnoreCase))
                {
                    _console.WriteLine($"Ignored error \"{ignoreMsg}\" on {modelError.SourceName} since it is caused by the assembly workaround.");
                }
                else if (modelError.Severity == ModelErrorSeverity.Error)
                {
                    validationErrors++;
                    _console.WriteLine(modelError.GetOutputMessage(modelError.Severity));
                }
                else if (modelError.Severity == ModelErrorSeverity.Warning)
                {
                    ProcessWarning(modelError);
                }
                else
                {
                    _console.WriteLine(modelError.GetOutputMessage(modelError.Severity));
                }
            }

            if (validationErrors > 0)
            {
                _modelValid = false;
                _console.WriteLine($"Found {validationErrors} error(s), skip building package");
            }
            else
            {
                _modelValid = true;
            }

            return _modelValid.Value;

            void ProcessWarning(ModelValidationError modelError)
            {
                if (_suppressedWarnings.Contains(modelError.ErrorCode))
                    return;

                if (_suppressedFileWarnings.TryGetValue(modelError.SourceName, out var suppressedFileWarnings) && suppressedFileWarnings.Contains(modelError.ErrorCode))
                    return;

                if (TreatTSqlWarningsAsErrors)
                {
                    validationErrors++;
                }

                _console.WriteLine(modelError.GetOutputMessage(TreatTSqlWarningsAsErrors
                    ? ModelErrorSeverity.Error
                    : ModelErrorSeverity.Warning));
            }
        }

        public void SaveToDisk(FileInfo outputFile, PackageOptions packageOptions = null)
        {
            ArgumentNullException.ThrowIfNull(outputFile);

            // Ensure that the model has been created and metadata has been set
            EnsureModelCreated();
            EnsureModelValidated();
            EnsureMetadataCreated();

            // Check if the file already exists
            if (outputFile.Exists)
            {
                // Delete the existing file
                _console.WriteLine($"Deleting existing file {outputFile.FullName}");
                outputFile.Delete();
            }

            _console.WriteLine($"Writing model to {outputFile.FullName}");

            packageOptions = packageOptions ?? new PackageOptions { };

            if (_dllReferences.Count > 0)
            {
                // Ignore "AllReferencesMustBeResolved" error where there are assemblies
                // since we are adding the referenced assemblies manually to the package 
                // and those references will always show as unresolved during build time.
                var newIngnoreValidationErrors = new List<string>();
                if (packageOptions.IgnoreValidationErrors != null)
                {
                    newIngnoreValidationErrors.AddRange(packageOptions.IgnoreValidationErrors);
                }
                newIngnoreValidationErrors.Add("SR0029"); // AllReferencesMustBeResolved, https://github.com/microsoft/DacFx/issues/462

                packageOptions.IgnoreValidationErrors = newIngnoreValidationErrors;
            }

            DacPackageExtensions.BuildPackage(outputFile.FullName, Model, Metadata, packageOptions);

            if (_dllReferences.Count > 0)
            {
                using (var z = new ZipArchive(File.Open(outputFile.FullName, FileMode.Open, FileAccess.ReadWrite), ZipArchiveMode.Update))
                {
                    var modelEntry = z.GetEntry("model.xml");

                    string newModelHash;

                    using (var modelStream = modelEntry.Open())
                    {
                        var doc = XDocument.Load(modelStream);

                        XNamespace ns = doc.Root.Name.Namespace;

                        string assemblyName = null;
                        foreach(var referenceFile in _dllReferences)
                        {
                            _console.WriteLine($"Adding {referenceFile} to package");
                            
                            var dllBytes = File.ReadAllBytes(referenceFile);
                            var dllHex = "0x" + Convert.ToHexString(dllBytes);
                            assemblyName = Path.GetFileNameWithoutExtension(referenceFile);

                            var appendedContent = new XElement(ns + "Element",
                                new XAttribute("Type", "SqlAssembly"),
                                new XAttribute("Name", $"[{assemblyName}]"),
                                new XElement(ns + "Relationship",
                                    new XAttribute("Name", "AssemblySources"),
                                    new XElement(ns + "Entry",
                                        new XElement(ns + "Element",
                                            new XAttribute("Type", "SqlAssemblySource"),
                                            new XElement(ns + "Property",
                                                new XAttribute("Name", "Source"),
                                                new XElement(ns + "Value",
                                                    new XCData(dllHex)))))),
                                new XElement(ns + "Relationship",
                                    new XAttribute("Name", "Authorizer"),
                                    new XElement(ns + "Entry",
                                        new XElement(ns + "References",
                                            new XAttribute("ExternalSource", "BuiltIns"),
                                            new XAttribute("Name", "[dbo]")))));

                            doc.Root.Element(ns + "Model").Add(appendedContent);
                        }

                        // fix empty references
                        //TODO reference the right assembly if multiple exist.
                        var emptyAssemblyRelationsShips = doc.Descendants(ns + "Relationship")
                            .Where(r => r.Attribute("Name")?.Value == "Assembly")
                            .SelectMany(r => r.Elements(ns + "Entry").Where(e => !e.HasElements && !e.Attributes().Any()));
                        foreach(var emptyRelationship in emptyAssemblyRelationsShips)
                        {
                            emptyRelationship.Add(new XElement(ns + "References", new XAttribute("Name", $"[{assemblyName}]")));
                        }
                        
                        modelStream.SetLength(0);
                        doc.Save(modelStream);

                        modelStream.Position = 0;
                        using var sha256 = SHA256.Create();
                        newModelHash = string.Join("", sha256.ComputeHash(modelStream).Select(c => c.ToString("X2")));
                    }

                    var originEntry = z.GetEntry("Origin.xml");

                    using (var originStream = originEntry.Open())
                    {
                        var doc = XDocument.Load(originStream);

		                var elem = doc.Root.Elements().Single(e => e.Name.LocalName == "Checksums").Elements().Single(e => e.Name.LocalName == "Checksum" && e.Attribute("Uri")?.Value == "/model.xml");

                        elem.SetValue(newModelHash);

                        originStream.SetLength(0);
                        doc.Save(originStream);
                    }
                }
            }
        }

        public void SetMetadata(string name, string version)
        {
            Metadata = new PackageMetadata
            {
                Name = name,
                Version = version,
            };

            _console.WriteLine($"Using package name {name} and version {version}");
        }

        public void SetProperty(string key, string value)
        {
            try
            {
                // Convert value into the appropriate type depending on the key
                object propertyValue = key switch
                {
                    "QueryStoreIntervalLength" => int.Parse(value, CultureInfo.InvariantCulture),
                    "QueryStoreFlushInterval" => int.Parse(value, CultureInfo.InvariantCulture),
                    "QueryStoreDesiredState" => Enum.Parse<QueryStoreDesiredState>(value),
                    "QueryStoreCaptureMode" => Enum.Parse<QueryStoreCaptureMode>(value),
                    "ParameterizationOption" => Enum.Parse<ParameterizationOption>(value),
                    "PageVerifyMode" => Enum.Parse<PageVerifyMode>(value),
                    "QueryStoreMaxStorageSize" => int.Parse(value, CultureInfo.InvariantCulture),
                    "NumericRoundAbortOn" => bool.Parse(value),
                    "NestedTriggersOn" => bool.Parse(value),
                    "HonorBrokerPriority" => bool.Parse(value),
                    "FullTextEnabled" => bool.Parse(value),
                    "FileStreamDirectoryName" => value,
                    "DbScopedConfigQueryOptimizerHotfixesSecondary" => bool.Parse(value),
                    "DbScopedConfigQueryOptimizerHotfixes" => bool.Parse(value),
                    "NonTransactedFileStreamAccess" => Enum.Parse<NonTransactedFileStreamAccess>(value),
                    "DbScopedConfigParameterSniffingSecondary" => bool.Parse(value),
                    "QueryStoreMaxPlansPerQuery" => int.Parse(value, CultureInfo.InvariantCulture),
                    "QuotedIdentifierOn" => bool.Parse(value),
                    "VardecimalStorageFormatOn" => bool.Parse(value),
                    "TwoDigitYearCutoff" => short.Parse(value, CultureInfo.InvariantCulture),
                    "Trustworthy" => bool.Parse(value),
                    "TransformNoiseWords" => bool.Parse(value),
                    "TornPageProtectionOn" => bool.Parse(value),
                    "TargetRecoveryTimeUnit" => Enum.Parse<TimeUnit>(value),
                    "QueryStoreStaleQueryThreshold" => int.Parse(value, CultureInfo.InvariantCulture),
                    "TargetRecoveryTimePeriod" => int.Parse(value, CultureInfo.InvariantCulture),
                    "ServiceBrokerOption" => Enum.Parse<ServiceBrokerOption>(value),
                    "RecursiveTriggersOn" => bool.Parse(value),
                    "DelayedDurabilityMode" => Enum.Parse<DelayedDurabilityMode>(value),
                    "RecoveryMode" => Enum.Parse<RecoveryMode>(value),
                    "ReadOnly" => bool.Parse(value),
                    "SupplementalLoggingOn" => bool.Parse(value),
                    "DbScopedConfigParameterSniffing" => bool.Parse(value),
                    "DbScopedConfigMaxDOPSecondary" => int.Parse(value, CultureInfo.InvariantCulture),
                    "DbScopedConfigMaxDOP" => int.Parse(value, CultureInfo.InvariantCulture),
                    "AutoShrink" => bool.Parse(value),
                    "AutoCreateStatisticsIncremental" => bool.Parse(value),
                    "AutoCreateStatistics" => bool.Parse(value),
                    "AutoClose" => bool.Parse(value),
                    "ArithAbortOn" => bool.Parse(value),
                    "AnsiWarningsOn" => bool.Parse(value),
                    "AutoUpdateStatistics" => bool.Parse(value),
                    "AnsiPaddingOn" => bool.Parse(value),
                    "AnsiNullDefaultOn" => bool.Parse(value),
                    "MemoryOptimizedElevateToSnapshot" => bool.Parse(value),
                    "TransactionIsolationReadCommittedSnapshot" => bool.Parse(value),
                    "AllowSnapshotIsolation" => bool.Parse(value),
                    "Collation" => value,
                    "AnsiNullsOn" => bool.Parse(value),
                    "AutoUpdateStatisticsAsync" => bool.Parse(value),
                    "CatalogCollation" => Enum.Parse<CatalogCollation>(value),
                    "ChangeTrackingAutoCleanup" => bool.Parse(value),
                    "DbScopedConfigLegacyCardinalityEstimationSecondary" => bool.Parse(value),
                    "DbScopedConfigLegacyCardinalityEstimation" => bool.Parse(value),
                    "DBChainingOn" => bool.Parse(value),
                    "DefaultLanguage" => value,
                    "DefaultFullTextLanguage" => value,
                    "DateCorrelationOptimizationOn" => bool.Parse(value),
                    "DatabaseStateOffline" => bool.Parse(value),
                    "CursorDefaultGlobalScope" => bool.Parse(value),
                    "CursorCloseOnCommit" => bool.Parse(value),
                    "Containment" => Enum.Parse<Containment>(value),
                    "ConcatNullYieldsNull" => bool.Parse(value),
                    "CompatibilityLevel" => int.Parse(value, CultureInfo.InvariantCulture),
                    "ChangeTrackingRetentionUnit" => Enum.Parse<TimeUnit>(value),
                    "ChangeTrackingRetentionPeriod" => int.Parse(value, CultureInfo.InvariantCulture),
                    "ChangeTrackingEnabled" => bool.Parse(value),
                    "UserAccessOption" => Enum.Parse<UserAccessOption>(value),
                    "WithEncryption" => bool.Parse(value),
                    _ => throw new ArgumentException($"Unknown property with name {key}", nameof(key))
                };

                PropertyInfo property = typeof(TSqlModelOptions).GetProperty(key, BindingFlags.Public | BindingFlags.Instance);

                if (property == null)
                {
                    throw new ArgumentException($"Property with name {key} not found", nameof(key));
                }

                property.SetValue(Options, propertyValue);

                _console.WriteLine($"Setting property {key} to value {value}");
            }
            catch (FormatException)
            {
                throw new ArgumentException($"Unable to parse value for property with name {key}: {value}", nameof(value));
            }
        }

        public void Dispose()
        {
            Model?.Dispose();
            Model = null;
        }

        public TSqlModelOptions Options { get; } = new TSqlModelOptions();
        public TSqlModel Model { get; private set; }

        public PackageMetadata Metadata { get; private set; }

        private void EnsureModelCreated()
        {
            if (Model == null)
            {
                throw new InvalidOperationException("Model has not been initialized. Call UsingVersion first.");
            }
        }

        private void EnsureMetadataCreated()
        {
            if (Metadata == null)
            {
                throw new InvalidOperationException("Package metadata has not been initialized. Call SetMetadata first.");
            }
        }

        private void EnsureModelValidated()
        {
            if (_modelValid == null)
            {
                throw new InvalidOperationException("Model has not been validated. Call ValidateModel first.");
            }
        }

        private void AddScript(FileInfo script, FileInfo outputFile, string path, params string[] appendBefore)
        {
            if (_modelValid != true)
            {
                throw new InvalidOperationException("Cannot add pre and post scripts before model has been validated.");
            }

            if (script == null
                && (appendBefore == null || appendBefore.Length == 0))
            {
                return;
            }

            if (script != null && !script.Exists)
            {
                throw new ArgumentException($"Unable to find script file {script.FullName}", nameof(script));
            }

            using (var package = Package.Open(outputFile.FullName, FileMode.Open, FileAccess.ReadWrite))
            {
                var partContentsBuilder = new StringBuilder();

                if (appendBefore != null)
                {
                    foreach (var content in appendBefore)
                    {
                        partContentsBuilder.AppendLine(content);
                    }
                }

                if (script != null)
                {
                    _console.WriteLine($"Adding {script.FullName} to package");

                    using var parser = new ScriptParser(script.FullName, new IncludeVariableResolver());
                    var partContents = parser.GenerateScript();
                    partContentsBuilder.AppendLine(partContents);
                }

                WritePart(partContentsBuilder.ToString(), package, path);

                package.Close();
            }
        }

        private static void WritePart(string partContents, Package package, string path)
        {
            var part = package.CreatePart(new Uri(path, UriKind.Relative), "text/plain");

            using (var stream = part.GetStream())
            {
                var buffer = Encoding.UTF8.GetBytes(partContents);
                stream.Write(buffer, 0, buffer.Length);
            }
        }

        public bool TreatTSqlWarningsAsErrors { get; set; }

        private static readonly char[] separator = new [] { ',', ';' };

        public void AddWarningsToSuppress(string suppressionList)
        {
            _suppressedWarnings.AddRange(ParseSuppressionList(suppressionList));
        }

        public void AddFileWarningsToSuppress(FileInfo inputFile, string suppressionList)
        {
            ArgumentNullException.ThrowIfNull(inputFile);

            var warningList = ParseSuppressionList(suppressionList);
            if (warningList.Count > 0)
            {
                if (!_suppressedFileWarnings.TryGetValue(inputFile.FullName, out var list))
                {
                    _suppressedFileWarnings.Add(inputFile.FullName, warningList);
                }
                else
                {
                    list.AddRange(warningList.FindAll((x) => !list.Contains(x)));
                }
            }

        }

        private static List<int> ParseSuppressionList(string suppressionList)
        {
            var result = new List<int>();
            if (!string.IsNullOrEmpty(suppressionList))
            {
                foreach (var str in suppressionList.Split(separator, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(str.Trim(), out var value))
                    {
                        result.Add(value);
                    }
                }
            }

            return result;
        }

        public void GenerateCreateScript(FileInfo dacpacFile, string databaseName, DacDeployOptions deployOptions)
        {
            ArgumentNullException.ThrowIfNull(dacpacFile);

            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw new ArgumentException("The database name is mandatory.", nameof(databaseName));
            }

            var scriptFileName = $"{databaseName}_Create.sql";
            _console.WriteLine($"Generating create script {scriptFileName}");

            using var package = DacPackage.Load(dacpacFile.FullName);

            if (package == null)
            {
                throw new InvalidOperationException($"Unable to load package {dacpacFile.FullName}");
            }

            if (dacpacFile.DirectoryName == null)
            {
                throw new InvalidOperationException($"Unable to determine directory for package {dacpacFile.FullName}");
            }

            using var file = File.Create(Path.Combine(dacpacFile.DirectoryName, scriptFileName));

            DacServices.GenerateCreateScript(file, package, databaseName, deployOptions);
        }
    }
}
